// SPDX-License-Identifier: GPL-3.0-or-later
//
// Finds a PawnIO installation.
//
// PawnIO's install location is not documented and PawnIOLib.dll does not go into
// System32, so [LibraryImport("PawnIOLib")] fails with DllNotFoundException even
// on a machine where PawnIO is correctly installed - the default DLL search path
// never looks in Program Files.
//
// This locates the install directory (registry first, then well-known paths) and
// registers a DllImportResolver so the plain [LibraryImport] declarations resolve
// against it.
//
// Registry access is raw RegGetValueW rather than Microsoft.Win32.Registry, to
// avoid taking a package dependency for two lookups.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Amd;

[SupportedOSPlatform("windows")]
public static partial class PawnIoLocator
{
    private const string LibraryName = "PawnIOLib";
    private const string LibraryFile = "PawnIOLib.dll";

    private static readonly List<string> Checked = [];
    private static string? _installDirectory;
    private static bool _searched;

    // ------------------------------------------------------------- registry

    private static readonly nint HKEY_LOCAL_MACHINE = unchecked((nint)0x80000002);
    private const uint RRF_RT_REG_SZ = 0x00000002;
    private const uint RRF_SUBKEY_WOW6464KEY = 0x00010000;

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValue(
        nint hkey, string subKey, string? value, uint flags,
        nint type, [Out] char[]? data, ref uint dataSize);

    private static string? ReadRegistryString(string subKey, string valueName)
    {
        try
        {
            uint size = 0;
            if (RegGetValue(HKEY_LOCAL_MACHINE, subKey, valueName,
                            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, 0, null, ref size) != 0)
                return null;

            var buffer = new char[size / sizeof(char) + 1];
            if (RegGetValue(HKEY_LOCAL_MACHINE, subKey, valueName,
                            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, 0, buffer, ref size) != 0)
                return null;

            return new string(buffer).TrimEnd('\0').Trim().Trim('"');
        }
        catch { return null; }
    }

    // -------------------------------------------------------------- search

    private static IEnumerable<string> CandidateDirectories()
    {
        // Recorded by the installer, when present.
        foreach (var (key, value) in new[]
                 {
                     (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", "InstallLocation"),
                     (@"SOFTWARE\PawnIO", "InstallLocation"),
                     (@"SOFTWARE\PawnIO", "Path"),
                 })
        {
            var path = ReadRegistryString(key, value);
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }

        // The driver's ImagePath points at the .sys; its directory sometimes
        // holds the rest of the install.
        var imagePath = ReadRegistryString(@"SYSTEM\CurrentControlSet\Services\PawnIO", "ImagePath");
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var cleaned = imagePath.Replace(@"\??\", "").Replace("%SystemRoot%",
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            var dir = Path.GetDirectoryName(cleaned);
            if (!string.IsNullOrWhiteSpace(dir)) yield return dir;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "PawnIO");
        }

        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return AppContext.BaseDirectory;
    }

    /// <summary>Directory containing PawnIOLib.dll, or null if not found.</summary>
    public static string? InstallDirectory
    {
        get
        {
            if (_searched) return _installDirectory;
            _searched = true;

            foreach (var dir in CandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                var candidate = Path.Combine(dir, LibraryFile);
                Checked.Add(candidate);

                if (File.Exists(candidate))
                {
                    _installDirectory = dir;
                    return _installDirectory;
                }
            }

            return null;
        }
    }

    /// <summary>Every path examined, for diagnostics.</summary>
    public static IReadOnlyList<string> CheckedPaths
    {
        get { _ = InstallDirectory; return Checked; }
    }

    /// <summary>
    /// Locations a module blob may live, given the discovered install directory.
    /// PawnIO's own modules sit beside or under the library.
    /// </summary>
    public static IEnumerable<string> ModulePaths(string moduleName)
    {
        // The discovered install directory usually also appears in the
        // well-known roots below, so deduplicate rather than listing it twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Enumerate())
            if (seen.Add(path))
                yield return path;

        IEnumerable<string> Enumerate()
        {
            yield return Path.Combine(AppContext.BaseDirectory, moduleName);

            foreach (var dir in Directories())
            {
                yield return Path.Combine(dir, moduleName);
                yield return Path.Combine(dir, "modules", moduleName);
                yield return Path.Combine(dir, "Modules", moduleName);
            }
        }

        IEnumerable<string> Directories()
        {
            if (InstallDirectory is { } install) yield return install;

            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     })
            {
                if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "PawnIO");
            }
        }
    }

    // ------------------------------------------------------------- resolver

    /// <summary>
    /// Installs the resolver. Called from PawnIoNative's static constructor,
    /// which the runtime guarantees runs before that type's first P/Invoke -
    /// a module initializer would work too but forces assembly-load side
    /// effects on every consumer (CA2255).
    /// </summary>
    internal static void Register()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PawnIoLocator).Assembly, Resolve);
        }
        catch { /* already registered; harmless */ }
    }

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
            return nint.Zero;

        var dir = InstallDirectory;
        if (dir is null) return nint.Zero;

        return NativeLibrary.TryLoad(Path.Combine(dir, LibraryFile), out var handle)
            ? handle
            : nint.Zero;
    }
}

