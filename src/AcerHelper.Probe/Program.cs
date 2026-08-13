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
using AcerHelper.Hardware.Input;
using AcerHelper.Hardware.Interop;
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
    Console.WriteLine($"PawnIO install directory  : {PawnIoModule.InstallDirectory ?? "NOT FOUND"}");
    Console.WriteLine($"PawnIOLib loadable        : {PawnIoModule.IsLibraryAvailable()}");
    Console.WriteLine();
    Console.WriteLine("Looked for PawnIOLib.dll in:");
    foreach (var p in PawnIoModule.LibrarySearchPaths)
        Console.WriteLine($"  {(File.Exists(p) ? "[FOUND]" : "[     ]")} {p}");
    Console.WriteLine();
    Console.WriteLine("Looked for RyzenSMU.bin in:");
    foreach (var p in PawnIoModule.ModuleSearchPaths("RyzenSMU.bin"))
        Console.WriteLine($"  {(File.Exists(p) ? "[FOUND]" : "[     ]")} {p}");

    if (PawnIoModule.InstallDirectory is null)
    {
        Console.WriteLine();
        Console.WriteLine("If PawnIO IS installed, run with --find-pawnio to search the disk.");
    }
    return 0;
}

// Dumps the firmware's own ACPI tables. Read-only, no elevation, no vendor
// software involved - this is where the WMI methods are actually implemented.
if (args.Contains("--dump-acpi"))
{
    var directory = Path.Combine(AppContext.BaseDirectory, "acpi");

    Console.WriteLine("ACPI tables present:");
    Console.WriteLine("  " + string.Join(" ", AcpiTables.Enumerate()));
    Console.WriteLine();

    var written = AcpiTables.DumpAll(directory);
    foreach (var (signature, path, bytes) in written)
        Console.WriteLine($"  {signature,-6} {bytes,8:N0} bytes  ->  {path}");

    // The firmware API hands back only the first table for a duplicated
    // signature, so every SSDT beyond the first is invisible to it. The
    // registry has them all.
    Console.WriteLine();
    Console.WriteLine("From the registry (all SSDTs, not just the first):");

    var tables = new List<(string Name, byte[] Data)>();
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var (name, data) in AcpiTables.ReadFromRegistry())
    {
        counts.TryGetValue(name, out var seen);
        counts[name] = seen + 1;

        var fileName = seen == 0 ? $"{name}.aml" : $"{name}-{seen + 1}.aml";
        var path = Path.Combine(directory, fileName);

        File.WriteAllBytes(path, data);
        tables.Add((fileName, data));
        Console.WriteLine($"  {fileName,-14} {data.Length,8:N0} bytes");
    }

    if (written.Count == 0 && tables.Count == 0)
    {
        Console.Error.WriteLine("Could not read any ACPI table.");
        return 2;
    }

    // Locate the control methods behind each interface so the decompiled output
    // can be searched directly rather than read end to end.
    var interfaces = new (string Label, Guid Id)[]
    {
        ("AcerGamingFunction", new Guid("7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56")),
        ("BatteryControl", new Guid("79772EC5-04B1-4BFD-843C-61E7F77B6CC9")),
        ("APGeAction", new Guid("61EF69EA-865C-4BC3-A502-A0DEBA0CB531")),
        ("APGeEvent", new Guid("676AA15E-6A47-4D9F-A2CC-1E6D18D14026")),
    };

    Console.WriteLine();
    Console.WriteLine("Control methods behind the WMI interfaces (from _WDG):");

    var found = false;
    foreach (var (fileName, data) in tables)
    {
        foreach (var (label, id) in interfaces)
        {
            foreach (var method in AcpiTables.FindWmiMethodNames(data, id))
            {
                Console.WriteLine($"  {label,-20} -> {method,-6} in {fileName}");
                found = true;
            }
        }
    }

    if (!found)
        Console.WriteLine("  none located - the interfaces may be in a table Windows did not record.");

    Console.WriteLine();
    Console.WriteLine("Decompile with the ACPI compiler (part of the ACPICA tools):");
    Console.WriteLine($"    iasl -d \"{Path.Combine(directory, "DSDT.aml")}\"");
    Console.WriteLine();
    Console.WriteLine("Then open DSDT.dsl and find the control method named above.");
    Console.WriteLine("SetGamingFanTable is WMI method 18, so look for how that");
    Console.WriteLine("method dispatches on its index argument - the case for 18");
    Console.WriteLine("shows exactly what the fan table input must contain.");
    return 0;
}

// Last-resort locator: scan for PawnIO files when the usual paths miss.
if (args.Contains("--find-pawnio"))
{
    Console.WriteLine("Searching common roots for PawnIOLib.dll and *.bin modules...");
    Console.WriteLine();

    var roots = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
    }.Where(r => !string.IsNullOrEmpty(r)).Distinct();

    var hits = 0;
    foreach (var root in roots)
    {
        Console.WriteLine($"  scanning {root} ...");
        foreach (var pattern in new[] { "PawnIOLib.dll", "PawnIO.sys", "RyzenSMU.bin" })
        {
            IEnumerable<string> found;
            try
            {
                // Bounded depth keeps this from walking an entire user profile.
                found = Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                });
            }
            catch { continue; }

            foreach (var f in found)
            {
                Console.WriteLine($"    FOUND {f}");
                hits++;
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine(hits == 0
        ? "Nothing found. PawnIO does not appear to be installed."
        : $"{hits} file(s) found. Send this list and the locator will be taught these paths.");
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

        Console.WriteLine();
        Console.WriteLine("Mailbox registers (raw reads):");
        foreach (var (mailbox, register, address, value) in smu.ReadMailboxRegisters())
        {
            Console.WriteLine(address == 0
                ? $"  {mailbox,-5} {register,-4} not defined for this family"
                : $"  {mailbox,-5} {register,-4} 0x{address:X7} = "
                  + (value is { } v ? $"0x{v:X8}" : "<read rejected>"));
        }
        Console.WriteLine();
        Console.WriteLine("A response register reading 0x00000000 means that mailbox has never");
        Console.WriteLine("completed a transaction - usually the wrong one for this model.");

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
        Console.WriteLine($"  result: {result}");
        Console.WriteLine();

        Console.WriteLine(result.IsOk
            ? "Accepted. NOTE: acceptance is not stability - instability from an\n"
              + "aggressive offset usually appears only under sustained load.\n"
              + "Test with a stress run, and reboot or --smu-reset to revert."
            : result.Failure switch
            {
                SmuFailure.ResponseTimeout =>
                    "The mailbox accepted the writes but never answered, which usually\n"
                    + "means the wrong mailbox for this model. Compare the register dump\n"
                    + "above: a response register stuck at 0 is the giveaway.",
                SmuFailure.SmuError =>
                    "The mailbox works and the SMU explicitly refused the command.\n"
                    + "The command ID is likely wrong for this silicon.",
                SmuFailure.RegisterWriteRejected =>
                    "The PawnIO module refused a register write - the address is outside\n"
                    + "the range its policy permits.",
                _ => "Nothing was applied.",
            });
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

    if (args.Contains("--learn-nitro"))
    {
        Section("LEARN THE NITRO KEY");
        LearnNitroKey();
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

            var info = nv.GetPowerLimitInfo(i);
            if (info is null)
            {
                Console.WriteLine("  power      not exposed by the driver");
            }
            else
            {
                var current = nv.GetPowerLimit(i);
                Console.WriteLine($"  power      {current?.ToString() ?? "?"}%   "
                                  + $"range {info.MinPercent}..{info.MaxPercent}% "
                                  + $"(default {info.DefaultPercent}%)   "
                                  + (info.IsAdjustable ? "adjustable" : "LOCKED"));
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
    Console.WriteLine("To identify the Nitro key: press ONLY that key and note which");
    Console.WriteLine("function/key pair appears. ANV15-41 never emits function 0x07,");
    Console.WriteLine("so the documented turbo event is not what this model sends.");
    Console.WriteLine();

    using var watcher = AcerEventWatcher.Start();
    var count = 0;

    watcher.EventReceived += (_, e) =>
    {
        count++;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] function={e.Function} (0x{(byte)e.Function:X2})  "
                          + $"key=0x{e.KeyNumber:X2} {e.KeyName}  state=0x{e.DeviceState:X4}");
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
/// Captures the event a single key press produces and prints the config lines
/// that bind profile cycling to it.
///
/// Needed because ANV15-41 never emits the documented gaming/Turbo event
/// (function 0x07), so the binding cannot be hard-coded from the kernel's
/// keymap - it differs per model.
/// </summary>
static void LearnNitroKey()
{
    Console.WriteLine("Watching BOTH sources: APGeEvent (WMI) and the raw keyboard.");
    Console.WriteLine("The Nitro key may be either - on ANV15-41 it is an ordinary HID");
    Console.WriteLine("key that types '@', so it never appears as a WMI event.");
    Console.WriteLine();
    Console.WriteLine("Press the Nitro key ONCE, and nothing else.");
    Console.WriteLine("Waiting up to 30 seconds. Ctrl+C to abort.");
    Console.WriteLine();

    var strokes = new List<KeyStroke>();
    if (KeyboardHook.Start())
    {
        KeyboardHook.KeyPressed += stroke =>
        {
            lock (strokes) strokes.Add(stroke);
            Console.WriteLine($"  keyboard  {stroke}");
        };
    }
    else
    {
        Console.WriteLine("  (keyboard hook could not be installed; WMI only)");
    }

    try { LearnFromEvents(strokes); }
    finally { KeyboardHook.Stop(); }
}

static void LearnFromEvents(List<KeyStroke> strokes)
{

    using var watcher = AcerEventWatcher.Start();
    var captured = new List<AcerHotkeyEvent>();

    watcher.EventReceived += (_, e) =>
    {
        lock (captured) captured.Add(e);
        Console.WriteLine($"  captured function=0x{(byte)e.Function:X2} key=0x{e.KeyNumber:X2} "
                          + $"{e.KeyName} state=0x{e.DeviceState:X4}");
    };

    var deadline = DateTime.Now.AddSeconds(30);
    while (DateTime.Now < deadline)
    {
        Thread.Sleep(200);
        lock (captured) { if (captured.Count > 0 && DateTime.Now > deadline.AddSeconds(-27)) break; }
    }

    Thread.Sleep(500);   // let a paired follow-up event arrive

    List<AcerHotkeyEvent> events;
    lock (captured) events = [.. captured];

    Console.WriteLine();

    List<KeyStroke> keystrokes;
    lock (strokes) keystrokes = [.. strokes];

    if (events.Count == 0)
    {
        if (keystrokes.Count == 0)
        {
            Console.WriteLine("Nothing captured from either source. Either the key was not");
            Console.WriteLine("pressed, or Acer's driver swallows it entirely.");
            return;
        }

        Console.WriteLine($"No WMI event, but {keystrokes.Count} keystroke(s) seen.");
        Console.WriteLine("The Nitro key is a plain keyboard key on this model.");
        Console.WriteLine();

        // A single press can emit modifiers plus the key; the last non-modifier
        // stroke is the one that identifies it.
        var key = keystrokes.LastOrDefault(s => s.VirtualKey is not (0x10 or 0x11 or 0x12 or 0xA0
                                                                     or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5));

        Console.WriteLine("Full sequence:");
        foreach (var s in keystrokes) Console.WriteLine($"    {s}");
        Console.WriteLine();
        Console.WriteLine("Add these lines to acerhelper.conf:");
        Console.WriteLine();
        Console.WriteLine($"    NitroKeyVirtual=0x{key.VirtualKey:X2}");
        Console.WriteLine($"    NitroKeyScanCode=0x{key.ScanCode:X2}");
        Console.WriteLine();
        Console.WriteLine("Scan code is the reliable field - the virtual key may collide");
        Console.WriteLine("with a character you type normally.");
        return;
    }

    // A single press can emit a paired event; the first is the trigger.
    var chosen = events[0];

    Console.WriteLine($"{events.Count} event(s) captured. Using the first:");
    Console.WriteLine();
    Console.WriteLine($"  function 0x{(byte)chosen.Function:X2}  key 0x{chosen.KeyNumber:X2}");
    Console.WriteLine();
    Console.WriteLine("Add these lines to acerhelper.conf beside AcerHelper.App.exe:");
    Console.WriteLine();
    Console.WriteLine($"    NitroKeyFunction=0x{(byte)chosen.Function:X2}");
    Console.WriteLine($"    NitroKeyNumber=0x{chosen.KeyNumber:X2}");
    Console.WriteLine();
    Console.WriteLine("Use NitroKeyNumber=0xFF to match any key for that function.");

    if (events.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine("Other events in the same window (ignore unless the first is wrong):");
        foreach (var e in events.Skip(1))
            Console.WriteLine($"    function 0x{(byte)e.Function:X2}  key 0x{e.KeyNumber:X2}  {e.KeyName}");
    }
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



