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
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("settings load", ex);
        }

        return settings;
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
            };

            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("settings save", ex);
        }
    }
}
