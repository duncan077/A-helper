// SPDX-License-Identifier: GPL-3.0-or-later
//
// Windows power management: power mode overlay, power plans, and processor
// state limits.
//
// Used instead of SMU undervolting and SMU package power limits. Those need the
// PawnIO kernel driver and, on ANV15-41, gave nothing back: the GPU power target
// is vendor-locked and the CPU has no ACPI power interface to fall back on.
// Windows' own controls need no driver, no elevation for the overlay, and
// survive a reboot.
//
// The SMU code remains in AcerHelper.Hardware and is still reachable from the
// probe's --smu flags; the app simply no longer drives it.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.App;

/// <summary>
/// The Windows 11 power slider positions, as overlay scheme GUIDs.
/// </summary>
public enum WindowsPowerMode
{
    Unknown,
    BestEfficiency,
    Balanced,
    BestPerformance,
}

public sealed record PowerPlan(Guid Id, string Name);

/// <summary>
/// Processor performance boost mode - what Windows exposes as turbo behaviour.
/// Values are the documented PERFBOOSTMODE settings.
/// </summary>
public enum ProcessorBoostMode : uint
{
    Disabled = 0,
    Enabled = 1,
    Aggressive = 2,
    EfficientEnabled = 3,
    EfficientAggressive = 4,
    AggressiveAtGuaranteed = 5,
    EfficientAggressiveAtGuaranteed = 6,
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsPower
{
    private const string Lib = "powrprof.dll";
    private const int ERROR_SUCCESS = 0;

    // Overlay GUIDs behind the "Power mode" control. The all-zero GUID means
    // "no overlay", i.e. the plan's own balanced behaviour.
    private static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBalanced = Guid.Empty;
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Processor power management subgroup and the throttle limits within it.
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    /// <summary>Processor performance boost mode - the turbo selector.</summary>
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>Well-known Windows power plans, for config convenience.</summary>
    public static readonly Guid PlanBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PlanHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PlanUltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const uint ACCESS_SCHEME = 16;

    [LibraryImport(Lib)]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport(Lib)]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport(Lib)]
    private static partial uint PowerGetActualOverlayScheme(out Guid overlayGuid);

    [LibraryImport(Lib)]
    private static partial uint PowerSetActiveOverlayScheme(in Guid overlayGuid);

    [LibraryImport(Lib)]
    private static partial uint PowerEnumerate(
        nint rootPowerKey, nint schemeGuid, nint subGroupGuid,
        uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport(Lib)]
    private static partial uint PowerReadFriendlyName(
        nint rootPowerKey, in Guid schemeGuid, nint subGroupGuid, nint settingGuid,
        byte* buffer, ref uint bufferSize);

    [LibraryImport(Lib)]
    private static partial uint PowerWriteACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint value);

    [LibraryImport(Lib)]
    private static partial uint PowerWriteDCValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint value);

    [LibraryImport(Lib)]
    private static partial uint PowerReadACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint value);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    // ---------------------------------------------------------- power mode

    /// <summary>Current position of the Windows power slider.</summary>
    public static WindowsPowerMode GetPowerMode()
    {
        if (PowerGetActualOverlayScheme(out var overlay) != ERROR_SUCCESS)
            return WindowsPowerMode.Unknown;

        if (overlay == OverlayBestEfficiency) return WindowsPowerMode.BestEfficiency;
        if (overlay == OverlayBestPerformance) return WindowsPowerMode.BestPerformance;
        if (overlay == OverlayBalanced) return WindowsPowerMode.Balanced;

        return WindowsPowerMode.Unknown;
    }

    /// <summary>Moves the Windows power slider. Returns null on success.</summary>
    public static string? SetPowerMode(WindowsPowerMode mode)
    {
        var overlay = mode switch
        {
            WindowsPowerMode.BestEfficiency => OverlayBestEfficiency,
            WindowsPowerMode.BestPerformance => OverlayBestPerformance,
            WindowsPowerMode.Balanced => OverlayBalanced,
            _ => (Guid?)null,
        };

        if (overlay is not { } guid) return "Unknown power mode.";

        var result = PowerSetActiveOverlayScheme(guid);
        return result == ERROR_SUCCESS ? null : $"PowerSetActiveOverlayScheme failed ({result}).";
    }

    // --------------------------------------------------------- power plans

    public static Guid? GetActivePlan()
    {
        if (PowerGetActiveScheme(0, out var ptr) != ERROR_SUCCESS || ptr == 0) return null;

        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    /// <summary>Enumerates the machine's power plans with their friendly names.</summary>
    public static IReadOnlyList<PowerPlan> GetPlans()
    {
        var plans = new List<PowerPlan>();

        for (uint index = 0; ; index++)
        {
            var guid = Guid.Empty;
            var size = (uint)sizeof(Guid);

            if (PowerEnumerate(0, 0, 0, ACCESS_SCHEME, index, (byte*)&guid, ref size) != ERROR_SUCCESS)
                break;

            plans.Add(new PowerPlan(guid, ReadFriendlyName(guid) ?? guid.ToString()));

            if (index > 64) break;   // defensive: never spin on a misbehaving API
        }

        return plans;
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, scheme, 0, 0, null, ref size) != ERROR_SUCCESS || size == 0)
            return null;

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (PowerReadFriendlyName(0, scheme, 0, 0, p, ref size) != ERROR_SUCCESS)
                return null;

            // Friendly names come back as a null-terminated wide string.
            return new string((char*)p).TrimEnd('\0');
        }
    }

    public static string? SetActivePlan(Guid scheme)
    {
        var result = PowerSetActiveScheme(0, scheme);
        return result == ERROR_SUCCESS ? null : $"PowerSetActiveScheme failed ({result}).";
    }

    // --------------------------------------------------- processor limits

    /// <summary>
    /// Maximum processor state as a percentage, for the active plan on AC.
    /// This is the practical stand-in for an SMU package power limit: capping it
    /// holds boost clocks down, which drops package power and temperature.
    /// </summary>
    public static int? GetMaxProcessorState()
    {
        if (GetActivePlan() is not { } plan) return null;

        return PowerReadACValueIndex(0, plan, SubProcessor, ProcThrottleMax, out var value) == ERROR_SUCCESS
            ? (int)value
            : null;
    }

    /// <summary>
    /// Sets maximum processor state for the active plan, on both AC and battery.
    /// Returns null on success.
    /// </summary>
    /// <remarks>
    /// The change only takes effect once the plan is re-activated, which is why
    /// this re-applies it rather than trusting the write alone.
    /// </remarks>
    public static string? SetMaxProcessorState(int percent)
    {
        if (GetActivePlan() is not { } plan) return "No active power plan.";

        percent = Math.Clamp(percent, 5, 100);

        var ac = PowerWriteACValueIndex(0, plan, SubProcessor, ProcThrottleMax, (uint)percent);
        if (ac != ERROR_SUCCESS) return $"PowerWriteACValueIndex failed ({ac}).";

        var dc = PowerWriteDCValueIndex(0, plan, SubProcessor, ProcThrottleMax, (uint)percent);
        if (dc != ERROR_SUCCESS) return $"PowerWriteDCValueIndex failed ({dc}).";

        return SetActivePlan(plan);
    }

    public static ProcessorBoostMode? GetBoostMode()
    {
        if (GetActivePlan() is not { } plan) return null;

        return PowerReadACValueIndex(0, plan, SubProcessor, PerfBoostMode, out var value) == ERROR_SUCCESS
            ? (ProcessorBoostMode)value
            : null;
    }

    /// <summary>Sets boost mode on the active plan, AC and battery.</summary>
    public static string? SetBoostMode(ProcessorBoostMode mode)
    {
        if (GetActivePlan() is not { } plan) return "No active power plan.";

        var ac = PowerWriteACValueIndex(0, plan, SubProcessor, PerfBoostMode, (uint)mode);
        if (ac != ERROR_SUCCESS) return $"PowerWriteACValueIndex(boost) failed ({ac}).";

        PowerWriteDCValueIndex(0, plan, SubProcessor, PerfBoostMode, (uint)mode);

        // As with the throttle limits, the write is inert until re-activation.
        return SetActivePlan(plan);
    }

    /// <summary>Minimum processor state for the active plan, AC and battery.</summary>
    public static string? SetMinProcessorState(int percent)
    {
        if (GetActivePlan() is not { } plan) return "No active power plan.";

        percent = Math.Clamp(percent, 0, 100);

        PowerWriteACValueIndex(0, plan, SubProcessor, ProcThrottleMin, (uint)percent);
        PowerWriteDCValueIndex(0, plan, SubProcessor, ProcThrottleMin, (uint)percent);

        return SetActivePlan(plan);
    }
}
