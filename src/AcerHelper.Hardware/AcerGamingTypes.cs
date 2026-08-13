// SPDX-License-Identifier: GPL-3.0-or-later
//
// Interface constants for Acer's ACPI-WMI "gaming" interface.
//
// Protocol source: Linux kernel drivers/platform/x86/acer-wmi.c (GPL-2.0).
// Method names and parameter types verified against the BIOS-declared
// AcerGamingFunction class on Nitro ANV15-41, BIOS V1.51.
//
// No Acer software was disassembled to produce this file.

namespace AcerHelper.Hardware;

/// <summary>ACPI method IDs on the AcerGamingFunction class.</summary>
public enum AcerMethod
{
    SetGamingProfile = 1,
    SetGamingLED = 2,
    GetGamingProfile = 3,
    GetGamingLED = 4,
    GetGamingSysInfo = 5,
    SetGamingRgbKb = 6,
    GetGamingRgbKb = 7,
    SetGamingProfileSetting = 8,
    GetGamingProfileSetting = 9,
    SetGamingLEDBehavior = 10,
    GetGamingLEDBehavior = 11,
    SetGamingLEDColor = 12,
    GetGamingLEDColor = 13,
    SetGamingFanBehavior = 14,
    GetGamingFanBehavior = 15,
    SetGamingFanSpeed = 16,
    GetGamingFanSpeed = 17,
    SetGamingFanTable = 18,
    GetGamingFanTable = 19,
    SetGamingKBBacklight = 20,
    GetGamingKBBacklight = 21,
    SetGamingMiscSetting = 22,
    GetGamingMiscSetting = 23,
    SetCpuOverclockingProfile = 24,
    GetCpuOverclockingProfile = 25,
}

/// <summary>
/// Thermal profile values written to / read from misc setting 0x0B.
/// </summary>
/// <remarks>
/// ANV15-41 reports a supported-profile bitmap of 0x53, i.e. Quiet, Balanced,
/// Performance and Eco. <see cref="Turbo"/> is NOT set on this model - it is a
/// Predator-tier feature. Always gate the UI on the probed bitmap rather than
/// assuming the full enum.
/// </remarks>
public enum ThermalProfile : byte
{
    Quiet = 0x00,
    Balanced = 0x01,
    Performance = 0x04,
    Turbo = 0x05,
    Eco = 0x06,
}

/// <summary>Fan behaviour mode, per fan.</summary>
public enum FanMode : byte
{
    Auto = 0x01,
    Turbo = 0x02,

    /// <summary>
    /// Manual duty control. While in this mode the EC honours
    /// <see cref="AcerMethod.SetGamingFanSpeed"/> and stops regulating on its
    /// own - the host becomes responsible for thermals.
    /// </summary>
    Custom = 0x03,
}

/// <summary>Fan identifiers for the fan-speed (duty) methods.</summary>
public enum FanId : byte
{
    Cpu = 0x01,
    Gpu = 0x04,
}

/// <summary>
/// Fan selector bits for the fan-behaviour methods. Note these are a DIFFERENT
/// encoding from <see cref="FanId"/> - behaviour uses a bitmap, duty uses an id.
/// </summary>
[Flags]
public enum FanSelect : ushort
{
    None = 0,
    Cpu = 1 << 0,
    Gpu = 1 << 3,
    Both = Cpu | Gpu,
}

/// <summary>Sensor identifiers for GetGamingSysInfo reads.</summary>
public enum SensorId : byte
{
    CpuTemperature = 0x01,
    CpuFanSpeed = 0x02,
    ExternalTemperature2 = 0x03,
    GpuFanSpeed = 0x06,
    GpuTemperature = 0x0A,
}

/// <summary>Sub-commands for GetGamingSysInfo.</summary>
public enum SysInfoCommand : ushort
{
    SupportedSensors = 0x0000,
    SensorReading = 0x0001,
    BatteryStatus = 0x0002,
}

/// <summary>
/// Indices for the misc-setting get/set pair (methods 22/23).
/// </summary>
public enum MiscSetting : byte
{
    // Documented by the kernel.
    Overclock1 = 0x05,          // absent on ANV15-41
    Overclock2 = 0x07,          // returns 0xFF (unset) on ANV15-41
    SupportedProfiles = 0x0A,   // bitmap; kernel warns it is unreliable on some models
    PlatformProfile = 0x0B,

    // Present on ANV15-41 but undocumented upstream. Semantics unconfirmed -
    // do not expose these in a UI until each has been identified.
    Unknown01 = 0x01,
    Unknown02 = 0x02,
    Unknown06 = 0x06,
    Unknown08 = 0x08,
    Unknown09 = 0x09,
}

/// <summary>A decoded sensor snapshot. Values are raw EC units.</summary>
public sealed record SensorReadings(
    int? CpuTemperatureC,
    int? CpuFanRpm,
    int? ExternalTemperature2C,
    int? GpuFanRpm,
    int? GpuTemperatureC)
{
    /// <summary>
    /// The dGPU reports 0 C while powered down under dynamic switching. Treat a
    /// zero reading as "asleep", not as a real temperature.
    /// </summary>
    public bool GpuLikelyAsleep => GpuTemperatureC is null or 0;
}

/// <summary>Raised when the BIOS returns a non-zero status byte.</summary>
public sealed class AcerWmiException : Exception
{
    public AcerMethod Method { get; }
    public byte Status { get; }
    public ulong RawResult { get; }

    public AcerWmiException(AcerMethod method, byte status, ulong raw)
        : base($"{method} failed with status 0x{status:X2} (raw 0x{raw:X16}).")
    {
        Method = method;
        Status = status;
        RawResult = raw;
    }
}
