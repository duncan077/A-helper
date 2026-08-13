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
//   fancurve_<Profile>      CPU fan curve, temp:duty pairs
//   fancurve_gpu_<Profile>  GPU fan curve, same format
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

    /// <summary>CPU fan curve. Null means the EC keeps control.</summary>
    public IReadOnlyList<FanCurvePoint>? FanCurve { get; set; }

    /// <summary>GPU fan curve, kept separate because the two fans sit on
    /// different thermal loads and G-Helper's users expect independent control.</summary>
    public IReadOnlyList<FanCurvePoint>? GpuFanCurve { get; set; }

    public bool IsEmpty => PowerPlan is null && UsbcPowerPlan is null
                           && MaxProcessorState is null && Boost is null
                           && (FanCurve is null || FanCurve.Count == 0)
                           && (GpuFanCurve is null || GpuFanCurve.Count == 0);

    /// <summary>
    /// Duty for a temperature, linearly interpolated between points. Returns
    /// null when no curve is configured.
    /// </summary>
    public byte? DutyFor(int temperatureC) => DutyFor(FanCurve, temperatureC);

    public byte? GpuDutyFor(int temperatureC) => DutyFor(GpuFanCurve, temperatureC);

    private static byte? DutyFor(IReadOnlyList<FanCurvePoint>? curve, int temperatureC)
    {
        if (curve is not { Count: > 0 }) return null;

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

/// <summary>
/// One editable row in the fan curve editor: a fixed temperature band with an
/// adjustable duty.
/// </summary>
public sealed class FanCurveRow(int temperatureC, double duty)
    : System.ComponentModel.INotifyPropertyChanged
{
    private double _duty = duty;

    public int TemperatureC { get; } = temperatureC;
    public string Label => $"{TemperatureC} °C";

    public double Duty
    {
        get => _duty;
        set
        {
            if (Math.Abs(_duty - value) < 0.5) return;
            _duty = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Duty)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Parses and holds the per-profile section of acerhelper.conf.</summary>
public sealed class ProfileConfig
{
    /// <summary>
    /// Temperature bands offered by the editor. Fixed rather than free-form so
    /// the UI stays a handful of sliders instead of a point-dragging surface.
    /// </summary>
    public static readonly int[] EditorBands = [40, 50, 60, 70, 80, 90];

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
        ("fancurve_gpu_", static (s, v) => s.GpuFanCurve = ParseCurve(v)),
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

            // 0 is legal and means the fan stops - the EC does this itself at
            // idle. Only 1..29 is the stall band, so those lift to 30.
            var clamped = duty <= 0 ? 0 : Math.Clamp(duty, 30, 100);
            points.Add(new FanCurvePoint(Math.Clamp(temp, 0, 110), (byte)clamped));
        }

        if (points.Count == 0) return null;

        points.Sort((a, b) => a.TemperatureC.CompareTo(b.TemperatureC));
        return points;
    }

    /// <summary>Stores a fan curve for a profile, replacing any existing one.</summary>
    public void SetFanCurve(ThermalProfile profile, IReadOnlyList<FanCurvePoint> cpu,
                            IReadOnlyList<FanCurvePoint> gpu)
    {
        var settings = Ensure(profile);
        settings.FanCurve = cpu.Count == 0 ? null : cpu;
        settings.GpuFanCurve = gpu.Count == 0 ? null : gpu;
    }

    /// <summary>Removes a profile's fan curves.</summary>
    public void ClearFanCurve(ThermalProfile profile)
    {
        if (!_profiles.TryGetValue(profile, out var settings)) return;

        settings.FanCurve = null;
        settings.GpuFanCurve = null;
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
            if (s.GpuFanCurve is { Count: > 0 } gpu)
                yield return $"fancurve_gpu_{profile}=" +
                             string.Join(",", gpu.Select(p => $"{p.TemperatureC}:{p.DutyPercent}"));
        }
    }
}
