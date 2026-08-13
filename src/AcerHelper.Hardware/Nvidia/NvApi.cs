// SPDX-License-Identifier: GPL-3.0-or-later
//
// Minimal NVAPI interop for GPU clock offsets.
//
// NvAPIWrapper.Net cannot be used: it resolves every entry point through
// NvAPI_QueryInterface and Marshal.GetDelegateForFunctionPointer, and Native AOT
// cannot generate marshalling stubs for delegates it never sees statically.
// Publishing warns (IL2104 / IL3053) and at runtime it throws
// "NvAPI_Initialize is missing delegate marshalling data".
//
// So the same approach as the WMI layer: resolve function pointers by ordinal
// and call them through delegate* unmanaged. No reflection, no delegates.
//
// Function IDs are the well-known NVAPI ordinals used by every open-source tool
// that talks to this interface; they are stable across driver versions.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Nvidia;

/// <summary>A GPU clock domain, as NVAPI numbers them.</summary>
public enum NvClockDomain : uint
{
    Graphics = 0,
    Memory = 4,
    Processor = 7,
    Video = 8,
}

public sealed class NvApiException(string what, int status)
    : Exception($"{what} failed with NVAPI status {status}.")
{
    public int Status { get; } = status;
}

[SupportedOSPlatform("windows")]
public sealed unsafe partial class NvApi : IDisposable
{
    // ------------------------------------------------------------- ordinals

    private const uint ID_Initialize = 0x0150E828;
    private const uint ID_Unload = 0xD22BDD7E;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GPU_GetFullName = 0xCEEE8E9F;
    private const uint ID_GPU_GetPstates20 = 0x6FF81213;
    private const uint ID_GPU_SetPstates20 = 0x0F4DAE6B;
    private const uint ID_GPU_GetAllClockFrequencies = 0xDCB616C3;

    private const int NVAPI_OK = 0;
    private const int MaxPhysicalGpus = 64;

    // ---------------------------------------------------- pstates20 layout
    //
    // NV_GPU_PERF_PSTATES20_INFO_V2. Nested fixed-size arrays of structs are not
    // expressible in C#, so the pstate table is a flat byte block indexed with
    // the strides below. Sizes are from the NVAPI headers:
    //
    //   PARAM_DELTA  = value + range.min + range.max                = 12
    //   clock entry  = domainId + typeId + flags + delta + union20  = 44
    //   voltage      = domainId + flags + volt_uV + delta           = 24
    //   pstate       = id + flags + 8 clocks + 4 voltages           = 456
    //   header       = version + flags + numPstates/Clocks/Voltages = 20

    private const int ClockEntrySize = 44;
    private const int VoltageEntrySize = 24;
    private const int MaxClocksPerPstate = 8;
    private const int MaxVoltagesPerPstate = 4;
    private const int MaxPstates = 16;

    private const int PstateStride =
        8 + (MaxClocksPerPstate * ClockEntrySize) + (MaxVoltagesPerPstate * VoltageEntrySize);

    private const int HeaderSize = 20;

    // V1 ends after the pstate table.
    private const int Pstates20SizeV1 = HeaderSize + (MaxPstates * PstateStride);   // 7316

    // V2 appends an over-voltage block: numVoltages + 4 voltage entries.
    private const int OverVoltageBlockSize = 4 + (MaxVoltagesPerPstate * VoltageEntrySize);
    private const int Pstates20SizeV2 = Pstates20SizeV1 + OverVoltageBlockSize;      // 7416

    // MAKE_NVAPI_VERSION(struct, n) == sizeof(struct) | (n << 16)
    private const uint Pstates20VersionV1 = Pstates20SizeV1 | (1u << 16);
    private const uint Pstates20VersionV2 = Pstates20SizeV2 | (2u << 16);

    private const int NVAPI_INCOMPATIBLE_STRUCT_VERSION = -9;

    // Offsets within one clock entry.
    private const int ClockDomainOffset = 0;
    private const int ClockTypeOffset = 4;
    private const int ClockDeltaValueOffset = 12;
    private const int ClockDeltaMinOffset = 16;
    private const int ClockDeltaMaxOffset = 20;

    // ------------------------------------------------------------ interop

    [LibraryImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    private static partial void* QueryInterface(uint id);

    private readonly nint[] _gpuHandles;
    private bool _disposed;

    private static void* Resolve(uint id, string name)
    {
        var fn = QueryInterface(id);
        if (fn is null)
            throw new NvApiException($"nvapi_QueryInterface({name})", -1);
        return fn;
    }

    private static void Check(int status, string what)
    {
        if (status != NVAPI_OK) throw new NvApiException(what, status);
    }

    private NvApi(nint[] handles) => _gpuHandles = handles;

    /// <summary>Initialises NVAPI and enumerates physical GPUs.</summary>
    public static NvApi Open()
    {
        var init = (delegate* unmanaged[Cdecl]<int>)Resolve(ID_Initialize, "NvAPI_Initialize");
        Check(init(), "NvAPI_Initialize");

        var enumGpus = (delegate* unmanaged[Cdecl]<nint*, int*, int>)
            Resolve(ID_EnumPhysicalGPUs, "NvAPI_EnumPhysicalGPUs");

        var handles = new nint[MaxPhysicalGpus];
        var count = 0;

        fixed (nint* p = handles)
            Check(enumGpus(p, &count), "NvAPI_EnumPhysicalGPUs");

        if (count == 0) throw new NvApiException("NvAPI_EnumPhysicalGPUs", 0);

        return new NvApi(handles[..count]);
    }

    /// <summary>Opens NVAPI, or returns null when no NVIDIA GPU or driver is present.</summary>
    public static NvApi? TryOpen()
    {
        try { return Open(); }
        catch { return null; }
    }

    public int GpuCount => _gpuHandles.Length;

    public string GetGpuName(int index)
    {
        var getName = (delegate* unmanaged[Cdecl]<nint, byte*, int>)
            Resolve(ID_GPU_GetFullName, "NvAPI_GPU_GetFullName");

        var buffer = stackalloc byte[64];
        Check(getName(_gpuHandles[index], buffer), "NvAPI_GPU_GetFullName");

        return Marshal.PtrToStringAnsi((nint)buffer) ?? "unknown";
    }

    // ------------------------------------------------------- clock offsets

    /// <summary>One editable clock domain within the highest-performance pstate.</summary>
    public sealed record ClockOffset(
        NvClockDomain Domain,
        int CurrentKhz,
        int MinKhz,
        int MaxKhz,
        bool IsEditable)
    {
        public int CurrentMhz => CurrentKhz / 1000;
        public int MinMhz => MinKhz / 1000;
        public int MaxMhz => MaxKhz / 1000;
    }

    /// <summary>
    /// Reads the frequency deltas of pstate 0 (the highest-performance state),
    /// which is what "core clock offset" and "memory clock offset" refer to.
    /// </summary>
    public IReadOnlyList<ClockOffset> GetClockOffsets(int gpuIndex)
    {
        var get = (delegate* unmanaged[Cdecl]<nint, byte*, int>)
            Resolve(ID_GPU_GetPstates20, "NvAPI_GPU_GetPstates20");

        var buffer = stackalloc byte[Pstates20SizeV2];

        // Try V2, fall back to V1. Older drivers reject V2 with
        // NVAPI_INCOMPATIBLE_STRUCT_VERSION rather than negotiating.
        var status = NVAPI_INCOMPATIBLE_STRUCT_VERSION;
        foreach (var version in stackalloc[] { Pstates20VersionV2, Pstates20VersionV1 })
        {
            new Span<byte>(buffer, Pstates20SizeV2).Clear();
            *(uint*)buffer = version;

            status = get(_gpuHandles[gpuIndex], buffer);
            if (status != NVAPI_INCOMPATIBLE_STRUCT_VERSION) break;
        }

        Check(status, "NvAPI_GPU_GetPstates20");

        var numPstates = *(uint*)(buffer + 8);
        var numClocks = *(uint*)(buffer + 12);
        if (numPstates == 0) return [];

        var result = new List<ClockOffset>();

        // Pstate 0 is the highest-performance state; offsets live there.
        var pstate = buffer + HeaderSize;
        var clocks = pstate + 8;

        for (var i = 0; i < Math.Min(numClocks, MaxClocksPerPstate); i++)
        {
            var entry = clocks + (i * ClockEntrySize);

            result.Add(new ClockOffset(
                Domain: (NvClockDomain)(*(uint*)(entry + ClockDomainOffset)),
                CurrentKhz: *(int*)(entry + ClockDeltaValueOffset),
                MinKhz: *(int*)(entry + ClockDeltaMinOffset),
                MaxKhz: *(int*)(entry + ClockDeltaMaxOffset),
                IsEditable: (*(uint*)(entry + 8) & 1) != 0));
        }

        return result;
    }

    /// <summary>
    /// Sets a frequency offset for one clock domain, in kHz. Negative values
    /// underclock. The offset applies to pstate 0 and is not persisted by the
    /// driver - a reboot clears it.
    /// </summary>
    public void SetClockOffset(int gpuIndex, NvClockDomain domain, int offsetKhz)
    {
        // Refuse values the driver itself says are out of range, so a typo
        // becomes an exception rather than an applied extreme.
        var existing = GetClockOffsets(gpuIndex).FirstOrDefault(c => c.Domain == domain)
            ?? throw new NotSupportedException($"This GPU does not expose a {domain} clock offset.");

        if (!existing.IsEditable)
            throw new NotSupportedException($"{domain} clock offset is locked on this GPU.");

        if (offsetKhz < existing.MinKhz || offsetKhz > existing.MaxKhz)
            throw new ArgumentOutOfRangeException(nameof(offsetKhz), offsetKhz,
                $"Driver allows {existing.MinKhz}..{existing.MaxKhz} kHz for {domain}.");

        var set = (delegate* unmanaged[Cdecl]<nint, byte*, int>)
            Resolve(ID_GPU_SetPstates20, "NvAPI_GPU_SetPstates20");

        var buffer = stackalloc byte[Pstates20SizeV2];

        var status = NVAPI_INCOMPATIBLE_STRUCT_VERSION;
        foreach (var version in stackalloc[] { Pstates20VersionV2, Pstates20VersionV1 })
        {
            new Span<byte>(buffer, Pstates20SizeV2).Clear();

            *(uint*)buffer = version;
            *(uint*)(buffer + 8) = 1;    // numPstates
            *(uint*)(buffer + 12) = 1;   // numClocks
            *(uint*)(buffer + 16) = 0;   // numBaseVoltages

            var pstate = buffer + HeaderSize;
            *(uint*)pstate = 0;          // pstateId = P0

            var entry = pstate + 8;
            *(uint*)(entry + ClockDomainOffset) = (uint)domain;
            *(uint*)(entry + ClockTypeOffset) = 0;           // single frequency
            *(int*)(entry + ClockDeltaValueOffset) = offsetKhz;

            status = set(_gpuHandles[gpuIndex], buffer);
            if (status != NVAPI_INCOMPATIBLE_STRUCT_VERSION) break;
        }

        Check(status, "NvAPI_GPU_SetPstates20");
    }

    /// <summary>Clears the offset for a domain.</summary>
    public void ResetClockOffset(int gpuIndex, NvClockDomain domain)
        => SetClockOffset(gpuIndex, domain, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var unload = (delegate* unmanaged[Cdecl]<int>)Resolve(ID_Unload, "NvAPI_Unload");
            unload();
        }
        catch { /* nothing useful to do while tearing down */ }
    }
}
