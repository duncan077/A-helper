// SPDX-License-Identifier: GPL-3.0-or-later
//
// Per-thermal-profile settings, with a separate set for USB-C charging.
//
// Modelled on G-Helper's power-user config, with one deliberate difference:
// modes are keyed by NAME, not number. G-Helper uses 0 balanced / 1 turbo /
// 2 silent, but Acer's own profile values are Quiet=0, Balanced=1,
// Performance=4, Eco=6 - so reusing numbers would silently mean different things
// on the two projects. "scheme_Performance" cannot be misread.
//
//   scheme_<Profile>        power plan GUID for that profile
//   scheme_usbc_<Profile>   plan used instead while on a USB-C charger
//   maxproc_<Profile>       maximum processor state, percent
//   boost_<Profile>         processor boost mode, 0-6
//   fancurve_<Profile>      temp:duty pairs, e.g. 50:30,60:45,70:60,80:80
//
// Anything omitted is simply not applied, so a profile can control only the
// parts you care about.

using System.Globalization;
using AcerHelper.Hardware;

namespace AcerHelper.App;

/// <summary>One temperature/duty point on a fan curve.</summary>
public readonly record struct FanCurvePoint(int TemperatureC, byte DutyPercent);

/// <summary>Everything configurable for a single thermal profile.</summary>
public sealed class ProfileSettings
{
    public Guid? PowerPlan { get; set; }
    public Guid? UsbcPowerPlan { get; set; }
    public int? MaxProcessorState { get; set; }
    public ProcessorBoostMode? Boost { get; set; }
    public IReadOnlyList<FanCurvePoint>? FanCurve { get; set; }

    public bool IsEmpty => PowerPlan is null && UsbcPowerPlan is null
                           && MaxProcessorState is null && Boost is null
                           && (FanCurve is null || FanCurve.Count == 0);

    /// <summary>
    /// Duty for a temperature, linearly interpolated between points. Returns
    /// null when no curve is configured.
    /// </summary>
    public byte? DutyFor(int temperatureC)
    {
        if (FanCurve is not { Count: > 0 } curve) return null;

        // Points are kept sorted at parse time.
        if (temperatureC <= curve[0].TemperatureC) return curve[0].DutyPercent;
        if (temperatureC >= curve[^1].TemperatureC) return curve[^1].DutyPercent;

        for (var i = 1; i < curve.Count; i++)
        {
            var upper = curve[i];
            if (temperatureC > upper.TemperatureC) continue;

            var lower = curve[i - 1];
            var span = upper.TemperatureC - lower.TemperatureC;
            if (span <= 0) return upper.DutyPercent;

            var t = (double)(temperatureC - lower.TemperatureC) / span;
            return (byte)Math.Round(lower.DutyPercent + (t * (upper.DutyPercent - lower.DutyPercent)));
        }

        return curve[^1].DutyPercent;
    }
}

/// <summary>Parses and holds the per-profile section of acerhelper.conf.</summary>
public sealed class ProfileConfig
{
    private readonly Dictionary<ThermalProfile, ProfileSettings> _profiles = [];

    public ProfileSettings For(ThermalProfile profile)
        => _profiles.TryGetValue(profile, out var s) ? s : new ProfileSettings();

    public bool HasAny => _profiles.Count > 0;

    public IEnumerable<ThermalProfile> ConfiguredProfiles => _profiles.Keys;

    private ProfileSettings Ensure(ThermalProfile profile)
    {
        if (!_profiles.TryGetValue(profile, out var settings))
            _profiles[profile] = settings = new ProfileSettings();

        return settings;
    }

    /// <summary>
    /// Consumes one key=value line. Returns true when it was a profile setting,
    /// so the caller can leave unrelated keys to its own parser.
    /// </summary>
    public bool TryApply(string key, string value)
    {
        // Longest prefix first: scheme_usbc_ would otherwise match scheme_.
        foreach (var (prefix, apply) in Handlers)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var name = key[prefix.Length..];
            if (!Enum.TryParse<ThermalProfile>(name, ignoreCase: true, out var profile)) return false;

            apply(Ensure(profile), value);
            return true;
        }

        return false;
    }

    private (string Prefix, Action<ProfileSettings, string> Apply)[] Handlers =>
    [
        ("scheme_usbc_", static (s, v) => { if (TryParseGuid(v, out var g)) s.UsbcPowerPlan = g; }),
        ("scheme_", static (s, v) => { if (TryParseGuid(v, out var g)) s.PowerPlan = g; }),
        ("maxproc_", static (s, v) =>
        {
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                s.MaxProcessorState = Math.Clamp(p, 5, 100);
        }),
        ("boost_", static (s, v) =>
        {
            if (uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b <= 6)
                s.Boost = (ProcessorBoostMode)b;
        }),
        ("fancurve_", static (s, v) => s.FanCurve = ParseCurve(v)),
    ];

    private static bool TryParseGuid(string value, out Guid guid)
        => Guid.TryParse(value.Trim().Trim('"', '{', '}'), out guid);

    /// <summary>Parses "50:30,60:45,70:60" into sorted, clamped points.</summary>
    private static IReadOnlyList<FanCurvePoint>? ParseCurve(string value)
    {
        var points = new List<FanCurvePoint>();

        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length != 2) continue;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var temp))
                continue;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duty))
                continue;

            // The floor matches AcerFanGuard's: below it the fans can stall.
            points.Add(new FanCurvePoint(Math.Clamp(temp, 0, 110),
                                         (byte)Math.Clamp(duty, 30, 100)));
        }

        if (points.Count == 0) return null;

        points.Sort((a, b) => a.TemperatureC.CompareTo(b.TemperatureC));
        return points;
    }

    /// <summary>Renders the current configuration back out, for the sample file.</summary>
    public IEnumerable<string> ToLines()
    {
        foreach (var (profile, s) in _profiles.OrderBy(p => p.Key.ToString()))
        {
            if (s.PowerPlan is { } plan) yield return $"scheme_{profile}={plan}";
            if (s.UsbcPowerPlan is { } usbc) yield return $"scheme_usbc_{profile}={usbc}";
            if (s.MaxProcessorState is { } max) yield return $"maxproc_{profile}={max}";
            if (s.Boost is { } boost) yield return $"boost_{profile}={(uint)boost}";
            if (s.FanCurve is { Count: > 0 } curve)
                yield return $"fancurve_{profile}=" +
                             string.Join(",", curve.Select(p => $"{p.TemperatureC}:{p.DutyPercent}"));
        }
    }
}
