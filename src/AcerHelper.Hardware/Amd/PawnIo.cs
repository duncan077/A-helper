// SPDX-License-Identifier: GPL-3.0-or-later
//
// PawnIOLib interop.
//
// PawnIO is a signed kernel driver that executes sandboxed bytecode modules in
// ring 0. Userspace loads a module blob and calls its exported ioctls. The
// driver verifies the blob's signature, so only modules signed by the PawnIO
// project will load - we cannot supply our own.
//
// The RyzenSMU module blob is LGPL-2.1 and ships with PawnIO itself, so it is
// located on disk rather than redistributed here.
//
// Every export returns an HRESULT.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Amd;

[SupportedOSPlatform("windows")]
internal static partial class PawnIoNative
{
    private const string Lib = "PawnIOLib";

    // Declaring a static constructor makes this type non-beforefieldinit, so the
    // runtime must run it before the first P/Invoke below. That is what puts the
    // DLL resolver in place - PawnIOLib.dll is not on any default search path.
    static PawnIoNative() => PawnIoLocator.Register();

    [LibraryImport(Lib)]
    internal static partial int pawnio_version(out uint version);

    [LibraryImport(Lib)]
    internal static partial int pawnio_open(out nint handle);

    [LibraryImport(Lib)]
    internal static partial int pawnio_load(nint handle, [In] byte[] blob, nuint size);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pawnio_execute(
        nint handle,
        string name,
        [In] ulong[] input, nuint inSize,
        [Out] ulong[] output, nuint outSize,
        out nuint returnSize);

    [LibraryImport(Lib)]
    internal static partial int pawnio_close(nint handle);
}

/// <summary>Why the PawnIO layer is unavailable, for reporting to the user.</summary>
public enum PawnIoStatus
{
    Ok,
    LibraryMissing,
    DriverNotRunning,
    ModuleNotFound,
    ModuleLoadFailed,
    NotElevated,
}

/// <summary>
/// A loaded PawnIO module. Disposing closes the driver handle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PawnIoModule : IDisposable
{
    private nint _handle;
    private bool _disposed;

    private PawnIoModule(nint handle) => _handle = handle;

    /// <summary>Where a module blob may live. PawnIO installs its own modules.</summary>
    public static IEnumerable<string> ModuleSearchPaths(string moduleName)
        => PawnIoLocator.ModulePaths(moduleName);

    /// <summary>Directory PawnIOLib.dll was found in, or null.</summary>
    public static string? InstallDirectory => PawnIoLocator.InstallDirectory;

    /// <summary>Every location examined while looking for PawnIOLib.dll.</summary>
    public static IReadOnlyList<string> LibrarySearchPaths => PawnIoLocator.CheckedPaths;

    /// <summary>Whether PawnIOLib is present at all, without opening the driver.</summary>
    public static bool IsLibraryAvailable()
    {
        try
        {
            return PawnIoNative.pawnio_version(out _) >= 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Opens the driver and loads a module blob. Returns null with a reason
    /// rather than throwing, because every failure here is expected on a machine
    /// that simply has not installed PawnIO.
    /// </summary>
    public static PawnIoModule? Load(string moduleName, out PawnIoStatus status)
    {
        status = PawnIoStatus.Ok;

        byte[] blob;
        var path = ModuleSearchPaths(moduleName).FirstOrDefault(File.Exists);
        if (path is null)
        {
            status = PawnIoStatus.ModuleNotFound;
            return null;
        }

        try { blob = File.ReadAllBytes(path); }
        catch { status = PawnIoStatus.ModuleNotFound; return null; }

        nint handle;
        try
        {
            if (PawnIoNative.pawnio_open(out handle) < 0 || handle == 0)
            {
                status = PawnIoStatus.DriverNotRunning;
                return null;
            }
        }
        catch (DllNotFoundException) { status = PawnIoStatus.LibraryMissing; return null; }
        catch (EntryPointNotFoundException) { status = PawnIoStatus.LibraryMissing; return null; }

        if (PawnIoNative.pawnio_load(handle, blob, (nuint)blob.Length) < 0)
        {
            PawnIoNative.pawnio_close(handle);
            status = PawnIoStatus.ModuleLoadFailed;
            return null;
        }

        return new PawnIoModule(handle);
    }

    /// <summary>Calls an exported ioctl. Returns false on any driver-level failure.</summary>
    public bool Execute(string name, ulong[]? input, ulong[]? output)
    {
        if (_disposed) return false;

        input ??= [];
        output ??= [];

        try
        {
            var hr = PawnIoNative.pawnio_execute(
                _handle, name,
                input, (nuint)input.Length,
                output, (nuint)output.Length,
                out _);

            return hr >= 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != 0)
        {
            try { PawnIoNative.pawnio_close(_handle); } catch { /* tearing down */ }
            _handle = 0;
        }
    }
}
