// SPDX-License-Identifier: GPL-3.0-or-later
//
// AC / battery detection.
//
// Uses GetSystemPowerStatus rather than Microsoft.Win32.SystemEvents: it needs
// no extra package, is trivially AOT-safe via [LibraryImport], and the app
// already polls once a second, so an event subscription buys nothing.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.App;

public enum PowerSource
{
    Unknown,
    Battery,
    AC,
}

[SupportedOSPlatform("windows")]
internal static partial class SystemPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;  // 255 when unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Current power source, or <see cref="PowerSource.Unknown"/> if it cannot be determined.</summary>
    public static PowerSource GetSource()
    {
        if (!GetSystemPowerStatus(out var status)) return PowerSource.Unknown;

        return status.ACLineStatus switch
        {
            0 => PowerSource.Battery,
            1 => PowerSource.AC,
            _ => PowerSource.Unknown,
        };
    }

    /// <summary>Battery charge percentage, or null when unknown or absent.</summary>
    public static int? GetBatteryPercent()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        return status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent;
    }
}
