// SPDX-License-Identifier: GPL-3.0-or-later
//
// Screen refresh rate control.
//
// Pure Win32 - nothing Acer-specific. The panel's refresh rate is a display
// driver setting, not an EC one, so it works the same on any machine and is
// independent of whether the gaming WMI interface is present.
//
// DEVMODEW uses fixed char buffers rather than [MarshalAs(ByValTStr)] strings so
// the struct stays blittable, which [LibraryImport] requires.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.App;

[SupportedOSPlatform("windows")]
internal static unsafe partial class DisplayControl
{
    private const int CCHDEVICENAME = 32;
    private const int CCHFORMNAME = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODEW
    {
        public fixed char dmDeviceName[CCHDEVICENAME];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // Union: display devices use POINTL + orientation + fixed output (16 bytes).
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[CCHFORMNAME];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(char* deviceName, uint modeNum, DEVMODEW* devMode);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")]
    private static partial int ChangeDisplaySettingsEx(
        char* deviceName, DEVMODEW* devMode, nint hwnd, uint flags, void* param);

    private const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const uint CDS_TEST = 0x02;

    private const int DISP_CHANGE_SUCCESSFUL = 0;

    public sealed record DisplayMode(int Width, int Height, int RefreshHz);

    /// <summary>Current mode of the primary display, or null if it cannot be read.</summary>
    public static DisplayMode? GetCurrentMode()
    {
        var mode = default(DEVMODEW);
        mode.dmSize = (ushort)sizeof(DEVMODEW);

        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, &mode)) return null;

        return new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight,
                               (int)mode.dmDisplayFrequency);
    }

    /// <summary>
    /// Refresh rates available at the current resolution and colour depth,
    /// ascending. Filtering by the current resolution avoids offering rates that
    /// would silently also change resolution.
    /// </summary>
    public static IReadOnlyList<int> GetAvailableRefreshRates()
    {
        var current = GetCurrentMode();
        if (current is null) return [];

        var rates = new SortedSet<int>();
        var mode = default(DEVMODEW);
        mode.dmSize = (ushort)sizeof(DEVMODEW);

        for (uint i = 0; EnumDisplaySettings(null, i, &mode); i++)
        {
            if (mode.dmPelsWidth != (uint)current.Width) continue;
            if (mode.dmPelsHeight != (uint)current.Height) continue;

            // 0 and 1 are documented placeholders meaning "hardware default".
            if (mode.dmDisplayFrequency > 1) rates.Add((int)mode.dmDisplayFrequency);

            mode.dmSize = (ushort)sizeof(DEVMODEW);
        }

        return [.. rates];
    }

    /// <summary>
    /// Sets the refresh rate at the current resolution.
    /// Returns null on success, or a description of the failure.
    /// </summary>
    public static string? SetRefreshRate(int hz)
    {
        var mode = default(DEVMODEW);
        mode.dmSize = (ushort)sizeof(DEVMODEW);

        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, &mode))
            return "Could not read the current display mode.";

        if (mode.dmDisplayFrequency == (uint)hz) return null;

        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        // Ask the driver first, so an unsupported rate is reported rather than
        // applied and rolled back with a visible flicker.
        var test = ChangeDisplaySettingsEx(null, &mode, 0, CDS_TEST, null);
        if (test != DISP_CHANGE_SUCCESSFUL)
            return $"Display driver rejected {hz} Hz (code {test}).";

        var result = ChangeDisplaySettingsEx(null, &mode, 0, CDS_UPDATEREGISTRY, null);
        return result == DISP_CHANGE_SUCCESSFUL
            ? null
            : $"Failed to apply {hz} Hz (code {result}).";
    }
}
