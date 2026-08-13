// SPDX-License-Identifier: GPL-3.0-or-later
//
// Settings persistence.
//
// Deliberately a flat key=value file rather than JSON: System.Text.Json needs a
// source-generated context to work under Native AOT, and this is four values.
// Reading is tolerant - an unparsable line is ignored rather than fatal, so a
// hand-edited or partially written file cannot stop the app starting.

using System.Globalization;
using AcerHelper.Hardware;

namespace AcerHelper.App;

public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "acerhelper.conf");

    public bool AutoSwitchEnabled { get; set; }
    public ThermalProfile AcProfile { get; set; } = ThermalProfile.Performance;
    public ThermalProfile BatteryProfile { get; set; } = ThermalProfile.Eco;
    public bool MinimiseToTray { get; set; } = true;

    /// <summary>
    /// Which APGeEvent triggers profile cycling.
    ///
    /// Defaults to the documented gaming/Turbo event (function 0x07), but
    /// ANV15-41 never emits that - its Nitro key sends something else. Rather
    /// than hard-code one model's value, the binding is learned:
    /// <c>AcerHelper.Probe.exe --learn-nitro</c> prints the two lines to paste.
    /// </summary>
    public byte NitroKeyFunction { get; set; } = 0x07;

    /// <summary>Key number to match, or 0xFF to match any key for the function.</summary>
    public byte NitroKeyNumber { get; set; } = 0xFF;

    /// <summary>
    /// Keyboard scan code of the Nitro key, for models where it is an ordinary
    /// HID key rather than a WMI event.
    /// </summary>
    /// <remarks>
    /// ANV15-41 reports scan 0x75 extended with vk 0xFF - Windows has no virtual
    /// key for it at all, which is why the scan code is the field to match on.
    /// Set to 0 to disable keyboard-based triggering.
    /// </remarks>
    public uint NitroKeyScanCode { get; set; } = 0x75;

    /// <summary>Whether the scan code above is an extended (E0-prefixed) key.</summary>
    public bool NitroKeyExtended { get; set; } = true;

    /// <summary>Per-profile power plans, processor limits, boost and fan curves.</summary>
    public ProfileConfig Profiles { get; } = new();

    /// <summary>
    /// Profile applied automatically when a USB-C charger is connected, if
    /// USB-C detection is configured.
    /// </summary>
    public ThermalProfile? UsbcProfile { get; set; }

    /// <summary>
    /// Misc-setting index whose value identifies a USB-C charger, or 0xFF when
    /// USB-C detection is disabled.
    /// </summary>
    /// <remarks>
    /// Acer exposes no documented charger-type signal, and the ANV15-41 sensor
    /// bitmap (0x0227) has nothing for it either. The reliable way to find one is
    /// differential: run the probe's --watch, swap chargers, and note which index
    /// moves. Guessing an index here would silently mis-detect, so detection
    /// stays off until it has actually been observed.
    /// </remarks>
    public byte UsbcDetectMiscIndex { get; set; } = 0xFF;

    /// <summary>Value at that index meaning "USB-C charger connected".</summary>
    public byte UsbcDetectMiscValue { get; set; } = 1;

    public bool UsbcDetectionConfigured => UsbcDetectMiscIndex != 0xFF;

    public static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            if (!File.Exists(FilePath)) return settings;

            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                switch (key)
                {
                    case nameof(AutoSwitchEnabled) when bool.TryParse(value, out var b):
                        settings.AutoSwitchEnabled = b; break;
                    case nameof(MinimiseToTray) when bool.TryParse(value, out var m):
                        settings.MinimiseToTray = m; break;
                    case nameof(AcProfile) when Enum.TryParse<ThermalProfile>(value, out var ac):
                        settings.AcProfile = ac; break;
                    case nameof(BatteryProfile) when Enum.TryParse<ThermalProfile>(value, out var bat):
                        settings.BatteryProfile = bat; break;
                    case nameof(NitroKeyFunction) when TryParseByte(value, out var fn):
                        settings.NitroKeyFunction = fn; break;
                    case nameof(NitroKeyNumber) when TryParseByte(value, out var kn):
                        settings.NitroKeyNumber = kn; break;
                    case nameof(NitroKeyScanCode) when TryParseUInt(value, out var sc):
                        settings.NitroKeyScanCode = sc; break;
                    case nameof(NitroKeyExtended) when bool.TryParse(value, out var ext):
                        settings.NitroKeyExtended = ext; break;
                    case "usbc_profile" when Enum.TryParse<ThermalProfile>(value, true, out var up):
                        settings.UsbcProfile = up; break;
                    case nameof(UsbcDetectMiscIndex) when TryParseByte(value, out var ui):
                        settings.UsbcDetectMiscIndex = ui; break;
                    case nameof(UsbcDetectMiscValue) when TryParseByte(value, out var uv):
                        settings.UsbcDetectMiscValue = uv; break;

                    // Anything else may be a per-profile key (scheme_*, boost_*, ...).
                    default:
                        settings.Profiles.TryApply(key, value);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("settings load", ex);
        }

        return settings;
    }

    /// <summary>Accepts both decimal and 0x-prefixed hex, since these are event IDs.</summary>
    private static bool TryParseByte(string value, out byte result)
    {
        result = 0;
        return TryParseUInt(value, out var wide) && wide <= byte.MaxValue && (result = (byte)wide) == wide;
    }

    private static bool TryParseUInt(string value, out uint result)
    {
        value = value.Trim();

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)
            : uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    public void Save()
    {
        try
        {
            var lines = new[]
            {
                "# AcerHelper settings",
                string.Create(CultureInfo.InvariantCulture, $"{nameof(AutoSwitchEnabled)}={AutoSwitchEnabled}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(AcProfile)}={AcProfile}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(BatteryProfile)}={BatteryProfile}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(MinimiseToTray)}={MinimiseToTray}"),
                "",
                "# APGeEvent that cycles thermal profiles. Learn it with:",
                "#   AcerHelper.Probe.exe --learn-nitro",
                "# NitroKeyNumber=0xFF matches any key for that function.",
                string.Create(CultureInfo.InvariantCulture, $"{nameof(NitroKeyFunction)}=0x{NitroKeyFunction:X2}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(NitroKeyNumber)}=0x{NitroKeyNumber:X2}"),
                "",
                "# Keyboard fallback, for models where the Nitro key is a plain HID key.",
                "# ANV15-41 reports scan 0x75 extended. Set the scan code to 0 to disable.",
                string.Create(CultureInfo.InvariantCulture, $"{nameof(NitroKeyScanCode)}=0x{NitroKeyScanCode:X2}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(NitroKeyExtended)}={NitroKeyExtended}"),
            };

            var extra = new List<string>
            {
                "",
                "# USB-C charger. Acer exposes no documented charger-type signal, so",
                "# detection must be observed: run the probe's --watch, swap chargers,",
                "# and set the index that moves. 0xFF leaves detection off.",
                string.Create(CultureInfo.InvariantCulture, $"{nameof(UsbcDetectMiscIndex)}=0x{UsbcDetectMiscIndex:X2}"),
                string.Create(CultureInfo.InvariantCulture, $"{nameof(UsbcDetectMiscValue)}=0x{UsbcDetectMiscValue:X2}"),
            };

            if (UsbcProfile is { } up) extra.Add($"usbc_profile={up}");

            extra.AddRange(
            [
                "",
                "# Per-profile settings. Keyed by NAME because Acer's profile numbers",
                "# (Quiet=0 Balanced=1 Performance=4 Eco=6) differ from other tools'.",
                "#   scheme_<Profile>       power plan GUID",
                "#   scheme_usbc_<Profile>  plan used while on a USB-C charger",
                "#   maxproc_<Profile>      maximum processor state, percent",
                "#   boost_<Profile>        boost mode 0=off 1=on 2=aggressive 3-6 efficient",
                "#   fancurve_<Profile>     temp:duty pairs, duty floor 30",
                "#",
                "# Balanced (default)   381b4222-f694-41f0-9685-ff5bb260df2e",
                "# High Performance     8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                "# Ultimate Performance e9a42b02-d5df-448d-aa00-03f14749eb61",
                "#",
                "# Example:",
                "#   scheme_Performance=8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                "#   maxproc_Quiet=70",
                "#   boost_Quiet=0",
                "#   fancurve_Performance=50:35,60:50,70:70,80:90,90:100",
            ]);

            extra.AddRange(Profiles.ToLines());

            File.WriteAllLines(FilePath, [.. lines, .. extra]);
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("settings save", ex);
        }
    }
}
