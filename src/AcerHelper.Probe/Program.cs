// SPDX-License-Identifier: GPL-3.0-or-later
//
// Console harness for the AcerHelper hardware layer.
//
//   AcerHelper.Probe                 read-only dump (default)
//   AcerHelper.Probe --sweep         also sweep misc-setting indices 0x00-0x1F
//   AcerHelper.Probe --test-profile  reversible thermal-profile round-trip
//   AcerHelper.Probe --test-fans     reversible manual fan-duty test
//
// Write tests record the original state first and restore it in a finally
// block. --test-fans additionally runs under AcerFanGuard, so the fans revert
// to Auto even if this process is killed mid-test.

using AcerHelper.Hardware;
using AcerHelper.Hardware.Amd;
using AcerHelper.Hardware.Nvidia;

// GPU probing needs no Acer hardware and no elevation, so it runs before the
// elevation gate and works on any machine with an NVIDIA driver.
if (args.Contains("--gpu") || args.Any(a => a.StartsWith("--gpu-core=", StringComparison.Ordinal))
                           || args.Any(a => a.StartsWith("--gpu-mem=", StringComparison.Ordinal)))
{
    return Gpu(args);
}

// CPU identification is CPUID only - no driver, no elevation.
if (args.Contains("--cpu"))
{
    var id = CpuInfo.Identify();
    Console.WriteLine($"Brand      : {id.BrandString}");
    Console.WriteLine($"Vendor     : {id.Vendor}");
    Console.WriteLine($"Family     : 0x{id.Family:X2}  Model: 0x{id.Model:X2}  Stepping: {id.Stepping}");
    Console.WriteLine($"Codename   : {id.Codename}");
    Console.WriteLine();
    Console.WriteLine($"Curve Optimizer plausible : {id.SupportsCurveOptimizer}");
    Console.WriteLine($"PawnIO driver installed   : {CpuInfo.IsPawnIoInstalled()}");
    Console.WriteLine();
    Console.WriteLine("Undervolting needs BOTH: a supported codename and the PawnIO driver.");
    Console.WriteLine("Nothing was written - this is identification only.");
    Console.WriteLine();
    Console.WriteLine($"PawnIOLib available       : {PawnIoModule.IsLibraryAvailable()}");
    Console.WriteLine("Module search paths:");
    foreach (var p in PawnIoModule.ModuleSearchPaths("RyzenSMU.bin"))
        Console.WriteLine($"  {(File.Exists(p) ? "[FOUND]" : "[     ]")} {p}");
    return 0;
}

// SMU probe. Read-only by default; --smu-co= applies an undervolt.
if (args.Contains("--smu") || args.Any(a => a.StartsWith("--smu-co=", StringComparison.Ordinal))
                           || args.Contains("--smu-reset"))
{
    if (!AcerGamingWmi.IsElevated)
    {
        Console.Error.WriteLine("SMU access must run elevated.");
        return 1;
    }

    var smu = RyzenSmu.TryOpen(out var pawnStatus);
    if (smu is null)
    {
        Console.WriteLine($"SMU unavailable: {pawnStatus}");
        Console.WriteLine();
        Console.WriteLine(pawnStatus switch
        {
            PawnIoStatus.LibraryMissing => "PawnIOLib not found. Install PawnIO from https://pawnio.eu",
            PawnIoStatus.ModuleNotFound => "RyzenSMU.bin not found in any search path (see --cpu).",
            PawnIoStatus.DriverNotRunning => "The PawnIO driver is not running.",
            PawnIoStatus.ModuleLoadFailed => "Module loaded but the SMU did not answer the read-only probe.",
            _ => "Unknown reason.",
        });
        return 2;
    }

    using (smu)
    {
        Console.WriteLine($"CPU          : {smu.Cpu.BrandString}");
        Console.WriteLine($"Codename     : {smu.Cpu.Codename}");
        Console.WriteLine($"SMU family   : {smu.Family}");
        Console.WriteLine($"SMU version  : 0x{smu.SmuVersion:X8}");
        Console.WriteLine($"Usable       : {smu.IsUsable}");
        Console.WriteLine();
        Console.WriteLine("Validation probe passed - the mailbox answered read-only calls.");

        if (args.Contains("--smu-reset"))
        {
            smu.ResetCurveOptimizer();
            Console.WriteLine("Curve Optimizer reset to 0.");
            return 0;
        }

        var coArg = args.FirstOrDefault(a => a.StartsWith("--smu-co=", StringComparison.Ordinal));
        if (coArg is null)
        {
            Console.WriteLine();
            Console.WriteLine("Read-only. Use --smu-co=-20 to apply an all-core offset,");
            Console.WriteLine("or --smu-reset to clear it. A reboot also clears it.");
            return 0;
        }

        if (!int.TryParse(coArg["--smu-co=".Length..], out var offset))
        {
            Console.Error.WriteLine("Could not parse the offset.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Applying all-core Curve Optimizer offset {offset}...");
        var result = smu.SetCurveOptimizerAll(offset);
        Console.WriteLine($"  SMU status: {result}");
        Console.WriteLine();
        Console.WriteLine(result == SmuStatus.Ok
            ? "Accepted. NOTE: acceptance is not stability - instability from an\n"
              + "aggressive offset usually appears only under sustained load.\n"
              + "Test with a stress run, and reboot or --smu-reset to revert."
            : "Rejected. Nothing was applied.");
    }

    return 0;
}

// Does System.Management's COM layer survive Native AOT at all? This path needs
// no elevation and no Acer hardware, so it isolates the interop question.
if (args.Contains("--wmi-selftest"))
{
    try
    {
        AcerGamingWmi.InteropSelfTest();
        Console.WriteLine("SELFTEST PASS - AOT COM transport connected, queried and "
                          + "resolved an instance path.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SELFTEST FAIL - {ex.GetType().Name}: {ex.Message}");
        return 3;
    }
}

// Every flag that can write must be listed here, or the closing summary
// claims nothing changed when something did.
string[] writeFlags =
[
    "--test-profile", "--test-fans", "--health-on", "--health-off",
    "--overdrive-on", "--overdrive-off",
];
var readOnly = !args.Any(writeFlags.Contains);

if (!AcerGamingWmi.IsElevated)
{
    Console.Error.WriteLine("This tool must run elevated.");
    return 1;
}

AcerGamingWmi wmi;
try
{
    wmi = AcerGamingWmi.Open();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot open AcerGamingFunction: {ex.Message}");
    return 2;
}

using (wmi)
{
    Section("CAPABILITIES");

    IReadOnlyList<ThermalProfile> supported = [];
    try
    {
        supported = wmi.GetSupportedProfiles();
        Console.WriteLine($"Supported profiles : {string.Join(", ", supported)}");
    }
    catch (AcerWmiException ex)
    {
        Console.WriteLine($"Supported profiles : unavailable ({ex.Status:X2})");
    }

    Console.WriteLine($"Current profile    : {wmi.GetThermalProfile()}");
    Console.WriteLine($"CPU fan mode       : {wmi.GetFanMode(FanSelect.Cpu)}");
    Console.WriteLine($"GPU fan mode       : {wmi.GetFanMode(FanSelect.Gpu)}");
    Console.WriteLine($"CPU manual duty    : {wmi.GetFanDuty(FanId.Cpu)}%");
    Console.WriteLine($"GPU manual duty    : {wmi.GetFanDuty(FanId.Gpu)}%");
    Console.WriteLine($"Keyboard backlight : {wmi.TryGetKeyboardBacklight()?.ToString() ?? "n/a"}");

    Section("SENSORS");
    PrintSensors(wmi);

    if (args.Contains("--sweep"))
    {
        Section("MISC-SETTING SWEEP (read-only)");
        for (byte i = 0; i <= 0x1F; i++)
        {
            var v = wmi.TryGetMiscSetting(i);
            if (v is null) continue;

            var label = Enum.IsDefined(typeof(MiscSetting), i)
                ? ((MiscSetting)i).ToString()
                : "undocumented";
            Console.WriteLine($"  index 0x{i:X2}  value={v,-5} {label}");
        }
    }

    if (args.Contains("--battery") || args.Contains("--health-on") || args.Contains("--health-off"))
    {
        Section("BATTERY HEALTH (BatteryControl)");
        Battery(args);
    }

    if (args.Contains("--overdrive") || args.Contains("--overdrive-on") || args.Contains("--overdrive-off"))
    {
        Section("DISPLAY OVERDRIVE (GetGamingProfile / SetGamingProfile)");
        Overdrive(wmi, args);
    }

    if (args.Contains("--events"))
    {
        Section("HOTKEY EVENTS (APGeEvent)");
        WatchEvents();
    }

    if (args.Contains("--watch"))
    {
        Section("LIVE DIFFERENTIAL WATCH (read-only)");
        Watch(wmi);
    }

    if (args.Contains("--test-profile"))
    {
        Section("PROFILE ROUND-TRIP (reversible)");
        TestProfiles(wmi, supported);
    }

    if (args.Contains("--test-fans"))
    {
        Section("MANUAL FAN DUTY (guarded, reversible)");
        TestFans(wmi);
    }

    if (readOnly)
    {
        Console.WriteLine();
        Console.WriteLine("Read-only pass complete. No state was modified.");
        Console.WriteLine("Add --test-profile or --test-fans to exercise writes.");
    }
}

return 0;

/// <summary>
/// Reads and optionally sets NVIDIA clock offsets.
///
/// Offsets apply to pstate 0 and are NOT persisted by the driver - a reboot
/// clears them, which makes this safe to experiment with. Ranges come from the
/// driver itself, so an out-of-range value is refused rather than applied.
/// </summary>
static int Gpu(string[] args)
{
    var nv = NvApi.TryOpen();
    if (nv is null)
    {
        Console.WriteLine("NVAPI unavailable - no NVIDIA GPU, or the driver is not installed.");
        return 2;
    }

    using (nv)
    {
        Console.WriteLine($"NVAPI initialised. {nv.GpuCount} GPU(s).");
        Console.WriteLine();

        for (var i = 0; i < nv.GpuCount; i++)
        {
            Console.WriteLine($"GPU {i}: {nv.GetGpuName(i)}");

            IReadOnlyList<NvApi.ClockOffset> offsets;
            try
            {
                offsets = nv.GetClockOffsets(i);
            }
            catch (NvApiException ex)
            {
                Console.WriteLine($"  GetPstates20 failed: status {ex.Status}");
                continue;
            }

            if (offsets.Count == 0)
            {
                Console.WriteLine("  no editable clock domains reported");
                continue;
            }

            foreach (var o in offsets)
            {
                Console.WriteLine($"  {o.Domain,-10} offset {o.CurrentMhz,+5} MHz   "
                                  + $"range {o.MinMhz}..{o.MaxMhz} MHz   "
                                  + (o.IsEditable ? "editable" : "LOCKED"));
            }
        }

        var core = ParseOffset(args, "--gpu-core=");
        var mem = ParseOffset(args, "--gpu-mem=");
        if (core is null && mem is null) return 0;

        Console.WriteLine();
        if (core is { } c) Apply(nv, NvClockDomain.Graphics, c);
        if (mem is { } m) Apply(nv, NvClockDomain.Memory, m);

        Console.WriteLine();
        Console.WriteLine("Offsets are not persisted by the driver; a reboot clears them.");
    }

    return 0;

    static int? ParseOffset(string[] args, string prefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (arg is null) return null;

        return int.TryParse(arg[prefix.Length..], out var v) ? v : null;
    }

    static void Apply(NvApi nv, NvClockDomain domain, int mhz)
    {
        try
        {
            nv.SetClockOffset(0, domain, mhz * 1000);
            var now = nv.GetClockOffsets(0).FirstOrDefault(o => o.Domain == domain);
            Console.WriteLine($"  {domain} offset set to {mhz} MHz; reads back {now?.CurrentMhz} MHz");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {domain} offset failed - {ex.Message}");
        }
    }
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 60));
}

static void PrintSensors(AcerGamingWmi wmi)
{
    var r = wmi.ReadAllSensors();
    Console.WriteLine($"  CPU temperature : {Fmt(r.CpuTemperatureC, "C")}");
    Console.WriteLine($"  CPU fan         : {Fmt(r.CpuFanRpm, "rpm")}");
    Console.WriteLine($"  External temp 2 : {Fmt(r.ExternalTemperature2C, "C")}");
    Console.WriteLine($"  GPU fan         : {Fmt(r.GpuFanRpm, "rpm")}");
    Console.WriteLine($"  GPU temperature : {Fmt(r.GpuTemperatureC, "C")}"
                      + (r.GpuLikelyAsleep ? "   (dGPU appears powered down)" : ""));

    static string Fmt(int? v, string unit) => v is null ? "n/a" : $"{v} {unit}";
}

/// <summary>
/// Reads the GetGamingProfile word that carries display overdrive, and
/// optionally toggles it.
///
/// The encoding differs between models. Linuwu-Sense compares the whole word
/// against 0x1000001000000 (on) / 0x1000000 (off); ANV15-41 returns
/// 0x00FF000001000000, matching neither. The raw value is printed so a
/// set/read round-trip can establish which bits actually move.
/// </summary>
static void Overdrive(AcerGamingWmi wmi, string[] args)
{
    var before = wmi.GetGamingProfileRaw();
    Console.WriteLine($"GetGamingProfile raw = 0x{before:X16}");
    Console.WriteLine($"  status byte        = 0x{before & 0xFF:X2}");
    Console.WriteLine($"  bit 24             = {((before >> 24) & 1) != 0}");
    Console.WriteLine($"  bit 48             = {((before >> 48) & 1) != 0}   <- read as the state bit");
    Console.WriteLine($"  bits 55:48         = 0x{(before >> 48) & 0xFF:X2}");
    Console.WriteLine($"  decoded state      = {wmi.GetLcdOverdrive()?.ToString() ?? "unrecognised"}");

    Console.WriteLine();
    Console.WriteLine("  Linuwu-Sense reference: 0x1000001000000 = on, 0x1000000 = off");

    var on = args.Contains("--overdrive-on");
    var off = args.Contains("--overdrive-off");
    if (!on && !off) return;

    Console.WriteLine();
    Console.WriteLine($"Setting overdrive {(on ? "ON" : "OFF")}...");
    try
    {
        wmi.SetLcdOverdrive(on);
    }
    catch (AcerWmiException ex)
    {
        Console.WriteLine($"  rejected: status 0x{ex.Status:X2}");
        return;
    }

    Thread.Sleep(500);
    var after = wmi.GetGamingProfileRaw();
    Console.WriteLine($"  raw after = 0x{after:X16}");

    if (after == before)
    {
        Console.WriteLine("  UNCHANGED - this model may not expose overdrive here.");
        return;
    }

    Console.WriteLine($"  CHANGED - bits that moved: 0x{before ^ after:X16}");
    Console.WriteLine("  Report that mask; it identifies the state bit on this model.");
}

/// <summary>
/// Prints raw APGeEvent payloads. The BIOS declares these property names and
/// they are documented nowhere, so this is how the layout gets established.
/// </summary>
static void WatchEvents()
{
    Console.WriteLine("Listening. Press the Nitro key, Fn combinations, or plug/unplug AC.");
    Console.WriteLine("Ctrl+C to stop.");
    Console.WriteLine();

    using var watcher = AcerEventWatcher.Start();
    var count = 0;

    watcher.EventReceived += (_, e) =>
    {
        count++;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] function={e.Function} (0x{(byte)e.Function:X2}) "
                          + $"key={e.KeyNumber}");
        Console.WriteLine($"             raw: {e.DescribeRaw()}");
    };

    watcher.Failed += (_, ex) => Console.WriteLine($"  watcher failed: {ex.Message}");

    var stop = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
    while (!stop) Thread.Sleep(200);

    Console.WriteLine();
    Console.WriteLine($"Stopped. {count} event(s) captured. Nothing was modified.");
}

/// <summary>
/// Reports battery health state, and optionally toggles the ~80% charge cap.
/// Calibration is deliberately not exposed here - it is a multi-hour discharge
/// cycle and has no place behind a probe flag.
/// </summary>
static void Battery(string[] args)
{
    var battery = AcerBatteryWmi.TryOpen();
    if (battery is null)
    {
        Console.WriteLine("BatteryControl unavailable (class absent, or not elevated).");
        return;
    }

    using (battery)
    {
        var before = battery.GetHealthStatus();
        Console.WriteLine($"uFunctionList        : 0x{before.FunctionList:X2} "
                          + $"(binary {Convert.ToString(before.FunctionList, 2).PadLeft(8, '0')})");
        Console.WriteLine($"Health mode (80% cap): "
                          + Describe(before.HealthModeSupported, before.HealthModeEnabled));
        Console.WriteLine($"Calibration mode     : "
                          + Describe(before.CalibrationSupported, before.CalibrationEnabled));

        Console.WriteLine(before.HasUndocumentedFunctions
            ? $"Undocumented function bits present: 0x{before.UndocumentedBits:X2}"
            : "No functions beyond health/calibration - this firmware has no bypass mode.");

        Console.WriteLine();
        Console.WriteLine("Design capacity (index 15): "
                          + (battery.GetBatteryInfo(15)?.ToString() ?? "n/a") + " mAh");

        var turnOn = args.Contains("--health-on");
        var turnOff = args.Contains("--health-off");
        if (!turnOn && !turnOff) return;

        if (!before.HealthModeSupported)
        {
            Console.WriteLine("Health mode is not supported by this firmware - refusing to write.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Setting health mode {(turnOn ? "ON" : "OFF")}...");
        battery.SetHealthMode(turnOn);

        // The EC applies this asynchronously, so poll rather than trust the return.
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(600);
            var now = battery.GetHealthStatus();
            if (now.HealthModeEnabled == turnOn)
            {
                Console.WriteLine($"Confirmed: health mode is now {(turnOn ? "ON" : "OFF")}.");
                Console.WriteLine("Verify independently with: Get-CimInstance Win32_Battery");
                return;
            }
        }

        Console.WriteLine("Write returned success but the state did not change within 3 s.");
        Console.WriteLine("Re-run with --battery to re-read.");
    }

    static string Describe(bool supported, bool enabled) =>
        !supported ? "not supported" : enabled ? "SUPPORTED, currently ON" : "SUPPORTED, currently OFF";
}

/// <summary>
/// Polls every readable misc-setting index and reports only what changes.
///
/// This is how the undocumented indices get identified without disassembling
/// anything: leave this running, toggle ONE feature in NitroSense (or press a
/// hotkey), and whichever index moves is that feature. Purely observational -
/// nothing is written.
/// </summary>
static void Watch(AcerGamingWmi wmi)
{
    var baseline = new Dictionary<byte, byte>();
    for (byte i = 0; i <= 0x1F; i++)
    {
        var v = wmi.TryGetMiscSetting(i);
        if (v is not null) baseline[i] = v.Value;
    }

    var profile = wmi.GetThermalProfile();
    var cpuMode = wmi.GetFanMode(FanSelect.Cpu);
    var gpuMode = wmi.GetFanMode(FanSelect.Gpu);

    Console.WriteLine($"Watching {baseline.Count} readable indices, plus profile and fan modes.");
    Console.WriteLine("Baseline: " + string.Join("  ",
        baseline.OrderBy(k => k.Key).Select(k => $"0x{k.Key:X2}={k.Value}")));
    Console.WriteLine();
    Console.WriteLine("Now change ONE thing in NitroSense (battery charge limit first),");
    Console.WriteLine("or press the Nitro / mode key. Ctrl+C to stop.");
    Console.WriteLine();

    var stop = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

    while (!stop)
    {
        Thread.Sleep(500);

        foreach (var index in baseline.Keys.ToList())
        {
            var now = wmi.TryGetMiscSetting(index);
            if (now is null || now.Value == baseline[index]) continue;

            var label = Enum.IsDefined(typeof(MiscSetting), index)
                ? ((MiscSetting)index).ToString()
                : "UNDOCUMENTED";

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] misc 0x{index:X2} " +
                              $"{baseline[index]} -> {now.Value}   ({label})");
            baseline[index] = now.Value;
        }

        var p = wmi.GetThermalProfile();
        if (p != profile)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] profile {profile} -> {p}");
            profile = p;
        }

        var c = wmi.GetFanMode(FanSelect.Cpu);
        if (c != cpuMode)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CPU fan mode {cpuMode} -> {c}");
            cpuMode = c;
        }

        var g = wmi.GetFanMode(FanSelect.Gpu);
        if (g != gpuMode)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] GPU fan mode {gpuMode} -> {g}");
            gpuMode = g;
        }
    }

    Console.WriteLine();
    Console.WriteLine("Watch stopped. Nothing was modified.");
}

static void TestProfiles(AcerGamingWmi wmi, IReadOnlyList<ThermalProfile> supported)
{
    var original = wmi.GetThermalProfile();
    Console.WriteLine($"Original profile: {original}");

    var candidates = supported.Count > 0
        ? supported
        : [ThermalProfile.Quiet, ThermalProfile.Balanced, ThermalProfile.Performance, ThermalProfile.Eco];

    try
    {
        foreach (var p in candidates)
        {
            try
            {
                wmi.SetThermalProfile(p);
                Thread.Sleep(400);
                var actual = wmi.GetThermalProfile();
                var ok = actual == p;
                Console.WriteLine($"  set {p,-12} -> read back {actual,-12} {(ok ? "OK" : "MISMATCH")}");
            }
            catch (AcerWmiException ex)
            {
                Console.WriteLine($"  set {p,-12} -> rejected (status 0x{ex.Status:X2})");
            }
        }
    }
    finally
    {
        wmi.SetThermalProfile(original);
        Console.WriteLine($"Restored profile: {wmi.GetThermalProfile()}");
    }
}

static void TestFans(AcerGamingWmi wmi)
{
    Console.WriteLine("Engaging Custom mode under watchdog. Ctrl+C reverts to Auto.");
    Console.WriteLine();

    var options = new AcerFanGuardOptions
    {
        MinimumDutyPercent = 30,
        MaxTemperatureC = 85,
        HeartbeatTimeout = TimeSpan.FromSeconds(8),
    };

    using var guard = AcerFanGuard.Engage(wmi, options);
    guard.SafetyTripped += (_, e) => Console.WriteLine($"  !! SAFETY TRIP: {e.Reason}");

    try
    {
        foreach (byte duty in (byte[])[40, 60, 40])
        {
            guard.SetDuty(FanId.Cpu, duty);
            guard.SetDuty(FanId.Gpu, duty);
            Console.WriteLine($"  duty {duty}% programmed; settling...");

            for (var i = 0; i < 4; i++)
            {
                Thread.Sleep(1000);
                guard.Heartbeat();
            }

            var r = wmi.ReadAllSensors();
            Console.WriteLine($"    CPU {r.CpuFanRpm} rpm / {r.CpuTemperatureC} C" +
                              $"   GPU {r.GpuFanRpm} rpm");

            if (!guard.IsEngaged)
            {
                Console.WriteLine("    guard released early - aborting test.");
                break;
            }
        }
    }
    finally
    {
        guard.Release();
        Console.WriteLine();
        Console.WriteLine($"CPU fan mode restored to: {wmi.GetFanMode(FanSelect.Cpu)}");
        Console.WriteLine($"GPU fan mode restored to: {wmi.GetFanMode(FanSelect.Gpu)}");
    }
}


