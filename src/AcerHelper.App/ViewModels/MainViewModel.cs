// SPDX-License-Identifier: GPL-3.0-or-later
//
// Every hardware call - including opening and disposing the channels - is
// marshalled onto AcerHardwareDispatcher's dedicated MTA thread.
//
// The earlier version opened the channels in this constructor, which runs on
// Avalonia's STA UI thread, then called them from MTA thread-pool threads. The
// open succeeded, so the window appeared, but every read failed with
// RPC_E_WRONG_THREAD and the sensors stayed blank. Raw COM pointers are
// apartment-bound; nothing here may touch them off the dispatcher thread.
//
// INotifyPropertyChanged is hand-rolled: ReactiveUI and similar frameworks are
// reflection-heavy and fight Native AOT.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AcerHelper.Hardware;
using AcerHelper.Hardware.Amd;
using AcerHelper.Hardware.Input;
using AcerHelper.Hardware.Nvidia;
using Avalonia.Threading;

namespace AcerHelper.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AcerHardwareDispatcher _hw = new();
    private readonly CancellationTokenSource _cts = new();

    // Owned by the dispatcher thread. Never touch these from anywhere else.
    private AcerGamingWmi? _wmi;
    private AcerBatteryWmi? _battery;
    private AcerFanGuard? _fanGuard;

    // Owns its own thread and connection - see AcerEventWatcher.
    private AcerEventWatcher? _eventWatcher;

    // Optional and absent on most machines.
    private NvApi? _nvApi;

    private readonly AppSettings _settings = AppSettings.Load();

    // Auto-switching acts on TRANSITIONS only. Reapplying on every poll would
    // silently undo a manual profile change a second after the user made it.
    private PowerSource _lastPowerSource = PowerSource.Unknown;

    private bool _disposed;

    public MainViewModel()
    {
        SetProfileCommand = new RelayCommand(p => _ = SetProfileAsync((ThermalProfile)p!),
                                             _ => IsHardwareAvailable);
        ToggleCoolBoostCommand = new RelayCommand(_ => _ = ToggleCoolBoostAsync(),
                                                  _ => IsHardwareAvailable && !CustomFanEnabled);
        ToggleCustomFanCommand = new RelayCommand(_ => _ = ToggleCustomFanAsync(),
                                                  _ => IsHardwareAvailable);
        ToggleHealthModeCommand = new RelayCommand(_ => _ = ToggleHealthModeAsync(),
                                                   _ => HealthModeSupported);
        ToggleOverdriveCommand = new RelayCommand(_ => _ = ToggleOverdriveAsync(),
                                                  _ => OverdriveSupported);

        ApplyGpuOffsetsCommand = new RelayCommand(_ => _ = ApplyGpuOffsetsAsync(), _ => GpuAvailable);
        ResetGpuOffsetsCommand = new RelayCommand(_ => _ = ResetGpuOffsetsAsync(), _ => GpuAvailable);
        ApplyProcessorStateCommand = new RelayCommand(_ => ApplyProcessorState(),
                                                      _ => WindowsPowerAvailable);

        LoadRefreshRates();
        InitialiseTuning();

        Diagnostics.Write("---- AcerHelper starting ----");
        _ = InitialiseAsync();
    }

    // ------------------------------------------------------------- sensors

    private string _cpuTemp = "--";
    public string CpuTemp { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }

    private string _cpuFan = "--";
    public string CpuFan { get => _cpuFan; private set => Set(ref _cpuFan, value); }

    private string _gpuTemp = "--";
    public string GpuTemp { get => _gpuTemp; private set => Set(ref _gpuTemp, value); }

    private string _gpuFan = "--";
    public string GpuFan { get => _gpuFan; private set => Set(ref _gpuFan, value); }

    private string _systemTemp = "--";
    public string SystemTemp { get => _systemTemp; private set => Set(ref _systemTemp, value); }

    // ------------------------------------------------------------ profiles

    public ObservableCollection<ThermalProfile> SupportedProfiles { get; } = [];

    private ThermalProfile _currentProfile = ThermalProfile.Balanced;
    public ThermalProfile CurrentProfile
    {
        get => _currentProfile;
        private set { if (Set(ref _currentProfile, value)) OnPropertyChanged(nameof(CurrentProfileName)); }
    }

    public string CurrentProfileName => _currentProfile.ToString();

    // ----------------------------------------------------------- fan state

    private string _fanModeText = "--";
    public string FanModeText { get => _fanModeText; private set => Set(ref _fanModeText, value); }

    private bool _coolBoostEnabled;
    public bool CoolBoostEnabled { get => _coolBoostEnabled; private set => Set(ref _coolBoostEnabled, value); }

    private bool _customFanEnabled;
    public bool CustomFanEnabled
    {
        get => _customFanEnabled;
        private set { if (Set(ref _customFanEnabled, value)) RaiseCanExecuteChanged(); }
    }

    private double _cpuDuty = 50;
    public double CpuDuty
    {
        get => _cpuDuty;
        set { if (Set(ref _cpuDuty, value) && CustomFanEnabled) _ = ApplyDutyAsync(); }
    }

    private double _gpuDuty = 50;
    public double GpuDuty
    {
        get => _gpuDuty;
        set { if (Set(ref _gpuDuty, value) && CustomFanEnabled) _ = ApplyDutyAsync(); }
    }

    // ------------------------------------------------------------- battery

    private bool _healthModeEnabled;
    public bool HealthModeEnabled { get => _healthModeEnabled; private set => Set(ref _healthModeEnabled, value); }

    private bool _healthModeSupported;
    public bool HealthModeSupported
    {
        get => _healthModeSupported;
        private set { if (Set(ref _healthModeSupported, value)) RaiseCanExecuteChanged(); }
    }

    private string _designCapacity = "--";
    public string DesignCapacity { get => _designCapacity; private set => Set(ref _designCapacity, value); }

    // ---------------------------------------------------- advanced: GPU/CPU

    private bool _gpuAvailable;
    public bool GpuAvailable
    {
        get => _gpuAvailable;
        private set { if (Set(ref _gpuAvailable, value)) RaiseCanExecuteChanged(); }
    }

    private string _gpuName = "--";
    public string GpuName { get => _gpuName; private set => Set(ref _gpuName, value); }

    private string _gpuOffsetRange = "--";
    public string GpuOffsetRange { get => _gpuOffsetRange; private set => Set(ref _gpuOffsetRange, value); }

    private double _gpuCoreOffset;
    public double GpuCoreOffset { get => _gpuCoreOffset; set => Set(ref _gpuCoreOffset, value); }

    private double _gpuMemoryOffset;
    public double GpuMemoryOffset { get => _gpuMemoryOffset; set => Set(ref _gpuMemoryOffset, value); }

    // ------------------------------------------------------- windows power

    private bool _windowsPowerAvailable;
    public bool WindowsPowerAvailable
    {
        get => _windowsPowerAvailable;
        private set { if (Set(ref _windowsPowerAvailable, value)) RaiseCanExecuteChanged(); }
    }

    private string _cpuName = "--";
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }

    public ObservableCollection<WindowsPowerMode> PowerModes { get; } = [];

    private WindowsPowerMode _selectedPowerMode = WindowsPowerMode.Unknown;
    public WindowsPowerMode SelectedPowerMode
    {
        get => _selectedPowerMode;
        set
        {
            if (value == WindowsPowerMode.Unknown || !Set(ref _selectedPowerMode, value)) return;

            var error = WindowsPower.SetPowerMode(value);
            Status = error ?? $"Power mode set to {Describe(value)}";
            Diagnostics.Write($"power mode -> {value}: {error ?? "ok"}");
        }
    }

    public ObservableCollection<PowerPlan> PowerPlans { get; } = [];

    private PowerPlan? _selectedPowerPlan;
    public PowerPlan? SelectedPowerPlan
    {
        get => _selectedPowerPlan;
        set
        {
            if (value is null || !Set(ref _selectedPowerPlan, value)) return;

            var error = WindowsPower.SetActivePlan(value.Id);
            Status = error ?? $"Power plan set to {value.Name}";

            // Max processor state is per-plan, so re-read after switching.
            if (error is null)
            {
                _maxProcessorState = WindowsPower.GetMaxProcessorState() ?? _maxProcessorState;
                OnPropertyChanged(nameof(MaxProcessorState));
            }
        }
    }

    private double _maxProcessorState = 100;
    public double MaxProcessorState { get => _maxProcessorState; set => Set(ref _maxProcessorState, value); }

    public static string Describe(WindowsPowerMode mode) => mode switch
    {
        WindowsPowerMode.BestEfficiency => "Best efficiency",
        WindowsPowerMode.Balanced => "Balanced",
        WindowsPowerMode.BestPerformance => "Best performance",
        _ => "Unknown",
    };

    private bool _gpuPowerAdjustable;
    public bool GpuPowerAdjustable { get => _gpuPowerAdjustable; private set => Set(ref _gpuPowerAdjustable, value); }

    private string _gpuPowerRange = "not exposed";
    public string GpuPowerRange { get => _gpuPowerRange; private set => Set(ref _gpuPowerRange, value); }

    private double _gpuPowerLimit = 100;
    public double GpuPowerLimit { get => _gpuPowerLimit; set => Set(ref _gpuPowerLimit, value); }

    private double _gpuPowerMin = 50;
    public double GpuPowerMin { get => _gpuPowerMin; private set => Set(ref _gpuPowerMin, value); }

    private double _gpuPowerMax = 100;
    public double GpuPowerMax { get => _gpuPowerMax; private set => Set(ref _gpuPowerMax, value); }

    private double _cpuPowerLimit = 35;
    public double CpuPowerLimit { get => _cpuPowerLimit; set => Set(ref _cpuPowerLimit, value); }

    private double _cpuTempLimit = 90;
    public double CpuTempLimit { get => _cpuTempLimit; set => Set(ref _cpuTempLimit, value); }


    public ICommand ApplyGpuOffsetsCommand { get; private set; } = null!;
    public ICommand ResetGpuOffsetsCommand { get; private set; } = null!;
    public ICommand ApplyProcessorStateCommand { get; private set; } = null!;

    // -------------------------------------------------------------- display

    public ObservableCollection<int> RefreshRates { get; } = [];

    private int _selectedRefreshRate;
    public int SelectedRefreshRate
    {
        get => _selectedRefreshRate;
        set
        {
            // ComboBox pushes 0 while its item source is being repopulated.
            if (value <= 0 || !Set(ref _selectedRefreshRate, value)) return;
            ApplyRefreshRate(value);
        }
    }

    private string _displayMode = "--";
    public string DisplayMode { get => _displayMode; private set => Set(ref _displayMode, value); }

    private bool _overdriveSupported;
    public bool OverdriveSupported
    {
        get => _overdriveSupported;
        private set { if (Set(ref _overdriveSupported, value)) RaiseCanExecuteChanged(); }
    }

    private bool _overdriveEnabled;
    public bool OverdriveEnabled { get => _overdriveEnabled; private set => Set(ref _overdriveEnabled, value); }

    private string _overdriveRaw = "--";
    public string OverdriveRaw { get => _overdriveRaw; private set => Set(ref _overdriveRaw, value); }

    // -------------------------------------------------- power auto-switching

    private string _powerSourceText = "--";
    public string PowerSourceText { get => _powerSourceText; private set => Set(ref _powerSourceText, value); }

    public bool AutoSwitchEnabled
    {
        get => _settings.AutoSwitchEnabled;
        set
        {
            if (_settings.AutoSwitchEnabled == value) return;
            _settings.AutoSwitchEnabled = value;
            _settings.Save();
            OnPropertyChanged();

            // Apply immediately on enable so the setting visibly takes effect
            // instead of waiting for the next plug/unplug.
            if (value) _ = ApplyProfileForPowerAsync(_lastPowerSource, "auto-switch enabled");
        }
    }

    public ThermalProfile AcProfile
    {
        get => _settings.AcProfile;
        set
        {
            if (_settings.AcProfile == value) return;
            _settings.AcProfile = value;
            _settings.Save();
            OnPropertyChanged();
            if (AutoSwitchEnabled && _lastPowerSource == PowerSource.AC)
                _ = ApplyProfileForPowerAsync(PowerSource.AC, "AC profile changed");
        }
    }

    public ThermalProfile BatteryProfile
    {
        get => _settings.BatteryProfile;
        set
        {
            if (_settings.BatteryProfile == value) return;
            _settings.BatteryProfile = value;
            _settings.Save();
            OnPropertyChanged();
            if (AutoSwitchEnabled && _lastPowerSource == PowerSource.Battery)
                _ = ApplyProfileForPowerAsync(PowerSource.Battery, "battery profile changed");
        }
    }

    public bool MinimiseToTray
    {
        get => _settings.MinimiseToTray;
        set
        {
            if (_settings.MinimiseToTray == value) return;
            _settings.MinimiseToTray = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // -------------------------------------------------------------- status

    private bool _isHardwareAvailable;
    public bool IsHardwareAvailable
    {
        get => _isHardwareAvailable;
        private set { if (Set(ref _isHardwareAvailable, value)) RaiseCanExecuteChanged(); }
    }

    private string _status = "Opening hardware interface...";
    public string Status { get => _status; private set => Set(ref _status, value); }

    // ------------------------------------------------------------ commands

    public ICommand SetProfileCommand { get; }
    public ICommand ToggleCoolBoostCommand { get; }
    public ICommand ToggleCustomFanCommand { get; }
    public ICommand ToggleHealthModeCommand { get; }
    public ICommand ToggleOverdriveCommand { get; }

    // --------------------------------------------------------------- logic

    private async Task InitialiseAsync()
    {
        try
        {
            // Opened ON the dispatcher thread, so the COM pointers belong to
            // the same apartment that every later call runs in.
            var opened = await _hw.InvokeAsync(() =>
            {
                _wmi = AcerGamingWmi.TryOpen();
                if (_wmi is null) return (false, "", 0, false, false, "--");

                IReadOnlyList<ThermalProfile> profiles;
                try { profiles = _wmi.GetSupportedProfiles(); }
                catch (AcerWmiException)
                {
                    profiles = [ThermalProfile.Quiet, ThermalProfile.Balanced,
                                ThermalProfile.Performance, ThermalProfile.Eco];
                }

                var current = _wmi.GetThermalProfile();

                var healthOn = false;
                var healthSupported = false;
                var capacity = "--";

                _battery = AcerBatteryWmi.TryOpen();
                if (_battery is not null)
                {
                    try
                    {
                        var s = _battery.GetHealthStatus();
                        healthOn = s.HealthModeEnabled;
                        healthSupported = s.HealthModeSupported;
                        capacity = _battery.GetBatteryInfo(15)?.ToString() ?? "--";
                    }
                    catch (Exception ex) { Diagnostics.WriteException("battery init", ex); }
                }

                var names = string.Join(",", profiles);
                return (true, names, (int)current, healthOn, healthSupported, capacity);
            });

            if (!opened.Item1)
            {
                Status = "Acer gaming interface not found. Run elevated on a supported Acer laptop.";
                Diagnostics.Write("AcerGamingWmi.TryOpen returned null");
                return;
            }

            SupportedProfiles.Clear();
            foreach (var name in opened.Item2.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<ThermalProfile>(name, out var p)) SupportedProfiles.Add(p);

            CurrentProfile = (ThermalProfile)opened.Item3;
            HealthModeEnabled = opened.Item4;
            HealthModeSupported = opened.Item5;
            DesignCapacity = opened.Item6 == "--" ? "--" : $"{opened.Item6} mAh";

            IsHardwareAvailable = true;
            Status = "Ready";
            Diagnostics.Write($"init ok: profiles={opened.Item2} current={CurrentProfile}");

            await RefreshOverdriveAsync();
            StartEventWatcher();

            _ = PollLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Status = $"Init failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("init", ex);
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var consecutiveErrors = 0;

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                var s = await _hw.InvokeAsync(() =>
                {
                    var sensors = _wmi!.ReadAllSensors();
                    var cpuMode = _wmi.GetFanMode(FanSelect.Cpu);
                    var profile = _wmi.GetThermalProfile();
                    _fanGuard?.Heartbeat();
                    var guardEngaged = _fanGuard?.IsEngaged ?? false;
                    return (sensors, cpuMode, profile, guardEngaged);
                }).ConfigureAwait(false);

                consecutiveErrors = 0;

                // Cheap kernel call, safe from any thread - no WMI involved.
                var power = SystemPower.GetSource();
                var batteryPercent = SystemPower.GetBatteryPercent();

                if (power != _lastPowerSource)
                {
                    var previous = _lastPowerSource;
                    _lastPowerSource = power;
                    Diagnostics.Write($"power source {previous} -> {power}");

                    // Do not act on the very first reading: applying a profile at
                    // startup would override whatever the user last chose.
                    if (AutoSwitchEnabled && previous != PowerSource.Unknown)
                        await ApplyProfileForPowerAsync(power, $"switched to {power}")
                            .ConfigureAwait(false);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PowerSourceText = power switch
                    {
                        PowerSource.AC => batteryPercent is { } p ? $"AC ({p}%)" : "AC",
                        PowerSource.Battery => batteryPercent is { } p ? $"Battery ({p}%)" : "Battery",
                        _ => "unknown",
                    };

                    var r = s.sensors;
                    CpuTemp = r.CpuTemperatureC is { } ct ? $"{ct} °C" : "--";
                    CpuFan = r.CpuFanRpm is { } cf ? $"{cf} rpm" : "--";
                    GpuFan = r.GpuFanRpm is { } gf ? $"{gf} rpm" : "--";
                    SystemTemp = r.ExternalTemperature2C is { } et ? $"{et} °C" : "--";
                    GpuTemp = r.GpuLikelyAsleep ? "asleep" : $"{r.GpuTemperatureC} °C";

                    CurrentProfile = s.profile;
                    FanModeText = s.cpuMode.ToString();
                    CoolBoostEnabled = s.cpuMode == FanMode.Turbo;

                    if (CustomFanEnabled && !s.guardEngaged)
                    {
                        CustomFanEnabled = false;
                        Status = "Fan guard reverted to Auto (safety trip).";
                        Diagnostics.Write("fan guard tripped");
                        _ = _hw.InvokeAsync(() => { _fanGuard?.Dispose(); _fanGuard = null; });
                    }
                    else if (Status.StartsWith("Poll error", StringComparison.Ordinal))
                    {
                        Status = "Ready";
                    }
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                if (consecutiveErrors <= 3) Diagnostics.WriteException("poll", ex);

                var message = Diagnostics.Describe(ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    Status = $"Poll error - {message}  (see {Diagnostics.LogPath})");

                // A persistently failing poll is not worth hammering once a second.
                if (consecutiveErrors == 10)
                {
                    Diagnostics.Write("poll failing repeatedly; stopping poll loop");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        Status = $"Sensor polling stopped after repeated errors. See {Diagnostics.LogPath}");
                    return;
                }
            }
        }
    }

    // --------------------------------------------------- advanced: GPU / CPU

    /// <summary>
    /// Opens the optional tuning back-ends. Both are absent on most machines, so
    /// every failure here is expected and simply leaves the feature disabled.
    /// </summary>
    private void InitialiseTuning()
    {
        try
        {
            _nvApi = NvApi.TryOpen();
            if (_nvApi is { GpuCount: > 0 })
            {
                GpuName = _nvApi.GetGpuName(0);

                var offsets = _nvApi.GetClockOffsets(0);
                var core = offsets.FirstOrDefault(o => o.Domain == NvClockDomain.Graphics);
                var memory = offsets.FirstOrDefault(o => o.Domain == NvClockDomain.Memory);

                // Only offer control the driver says is editable.
                GpuAvailable = core is { IsEditable: true } || memory is { IsEditable: true };

                _gpuCoreOffset = core?.CurrentMhz ?? 0;
                _gpuMemoryOffset = memory?.CurrentMhz ?? 0;
                OnPropertyChanged(nameof(GpuCoreOffset));
                OnPropertyChanged(nameof(GpuMemoryOffset));

                GpuOffsetRange = core is null
                    ? "no editable clock domains"
                    : $"core {core.MinMhz}..{core.MaxMhz} MHz"
                      + (memory is null ? "" : $", memory {memory.MinMhz}..{memory.MaxMhz} MHz");

                var power = _nvApi.GetPowerLimitInfo(0);
                if (power is { IsAdjustable: true })
                {
                    GpuPowerAdjustable = true;
                    GpuPowerMin = power.MinPercent;
                    GpuPowerMax = power.MaxPercent;
                    _gpuPowerLimit = _nvApi.GetPowerLimit(0) ?? power.DefaultPercent;
                    OnPropertyChanged(nameof(GpuPowerLimit));
                    GpuPowerRange = $"{power.MinPercent}..{power.MaxPercent}% "
                                    + $"(default {power.DefaultPercent}%)";
                }
                else
                {
                    // Laptop boards frequently pin all three values together.
                    GpuPowerRange = power is null
                        ? "not exposed by the driver"
                        : $"locked at {power.DefaultPercent}%";
                }

                Diagnostics.Write($"nvapi: {GpuName}, editable={GpuAvailable}, {GpuOffsetRange}, "
                                  + $"power {GpuPowerRange}");
            }
            else
            {
                GpuName = "no NVIDIA GPU";
            }
        }
        catch (Exception ex)
        {
            GpuAvailable = false;
            Diagnostics.WriteException("nvapi init", ex);
        }

        try
        {
            CpuName = CpuInfo.Identify().BrandString;

            PowerModes.Clear();
            foreach (var mode in new[]
                     {
                         WindowsPowerMode.BestEfficiency,
                         WindowsPowerMode.Balanced,
                         WindowsPowerMode.BestPerformance,
                     })
            {
                PowerModes.Add(mode);
            }

            _selectedPowerMode = WindowsPower.GetPowerMode();
            OnPropertyChanged(nameof(SelectedPowerMode));

            PowerPlans.Clear();
            foreach (var plan in WindowsPower.GetPlans()) PowerPlans.Add(plan);

            var active = WindowsPower.GetActivePlan();
            _selectedPowerPlan = PowerPlans.FirstOrDefault(p => p.Id == active);
            OnPropertyChanged(nameof(SelectedPowerPlan));

            _maxProcessorState = WindowsPower.GetMaxProcessorState() ?? 100;
            OnPropertyChanged(nameof(MaxProcessorState));

            WindowsPowerAvailable = true;
            Diagnostics.Write($"windows power: mode={_selectedPowerMode} "
                              + $"plan={_selectedPowerPlan?.Name ?? "?"} maxproc={_maxProcessorState}%");
        }
        catch (Exception ex)
        {
            WindowsPowerAvailable = false;
            Diagnostics.WriteException("windows power init", ex);
        }
    }

    private void ApplyProcessorState()
    {
        var percent = (int)MaxProcessorState;
        var error = WindowsPower.SetMaxProcessorState(percent);

        Status = error ?? $"Maximum processor state set to {percent}%";
        Diagnostics.Write($"max processor state -> {percent}%: {error ?? "ok"}");
    }

    private async Task ApplyGpuOffsetsAsync()
    {
        var core = (int)GpuCoreOffset;
        var memory = (int)GpuMemoryOffset;

        try
        {
            var power = GpuPowerAdjustable ? (int)GpuPowerLimit : (int?)null;

            await Task.Run(() =>
            {
                _nvApi!.SetClockOffset(0, NvClockDomain.Graphics, core * 1000);
                _nvApi.SetClockOffset(0, NvClockDomain.Memory, memory * 1000);
                if (power is { } p) _nvApi.SetPowerLimit(0, p);
            });

            Status = $"GPU applied: core {core:+#;-#;0} MHz, memory {memory:+#;-#;0} MHz"
                     + (power is { } w ? $", power {w}%" : "");
            Diagnostics.Write($"gpu: core={core} mem={memory} power={power?.ToString() ?? "n/a"}");
        }
        catch (Exception ex)
        {
            Status = $"GPU offset failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("gpu offsets", ex);
        }
    }

    private async Task ResetGpuOffsetsAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _nvApi!.ResetClockOffset(0, NvClockDomain.Graphics);
                _nvApi.ResetClockOffset(0, NvClockDomain.Memory);
            });

            GpuCoreOffset = 0;
            GpuMemoryOffset = 0;
            Status = "GPU offsets cleared";
        }
        catch (Exception ex)
        {
            Status = $"GPU reset failed - {Diagnostics.Describe(ex)}";
        }
    }




    // ------------------------------------------------------- hotkey handling

    private void StartEventWatcher()
    {
        try
        {
            var watcher = AcerEventWatcher.Start();

            watcher.EventReceived += (_, e) =>
            {
                // Raised on the watcher thread. Log the raw payload every time:
                // the BIOS declares these property names and they are not
                // documented, so this is the only record of the real layout.
                Diagnostics.Write($"APGeEvent function={e.Function} key={e.KeyNumber}  {e.DescribeRaw()}");

                if (MatchesNitroKey(e)) _ = CycleProfileAsync();
            };

            watcher.Failed += (_, ex) =>
            {
                Diagnostics.WriteException("event watcher", ex);
                _ = Dispatcher.UIThread.InvokeAsync(() =>
                    Status = $"Hotkey watcher stopped - {Diagnostics.Describe(ex)}");
            };

            _eventWatcher = watcher;
            Diagnostics.Write("event watcher started");

            StartKeyboardHook();
        }
        catch (Exception ex)
        {
            // Hotkeys are a convenience; losing them must not break the app.
            Diagnostics.WriteException("event watcher start", ex);
        }
    }

    /// <summary>
    /// Watches the raw keyboard for the Nitro key.
    ///
    /// Needed because on ANV15-41 the key is not a WMI event at all: it reports
    /// scan 0x75 extended with virtual key 0xFF, meaning Windows has no virtual
    /// key for it. The scan code is therefore the only reliable thing to match.
    /// </summary>
    private void StartKeyboardHook()
    {
        if (_settings.NitroKeyScanCode == 0) return;

        try
        {
            if (!KeyboardHook.Start())
            {
                Diagnostics.Write("keyboard hook could not be installed");
                return;
            }

            KeyboardHook.KeyPressed += stroke =>
            {
                if (stroke.ScanCode != _settings.NitroKeyScanCode) return;
                if (stroke.IsExtended != _settings.NitroKeyExtended) return;

                Diagnostics.Write($"nitro key: {stroke}");
                _ = CycleProfileAsync();
            };

            Diagnostics.Write($"keyboard hook watching scan 0x{_settings.NitroKeyScanCode:X2}"
                              + (_settings.NitroKeyExtended ? " extended" : ""));
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("keyboard hook", ex);
        }
    }

    /// <summary>
    /// Whether an event is the configured profile-cycle key.
    ///
    /// Not hard-coded to the documented function 0x07: ANV15-41 never emits it,
    /// and the event its Nitro key does send differs by model. The binding lives
    /// in acerhelper.conf and is discovered with the probe's --learn-nitro.
    /// </summary>
    private bool MatchesNitroKey(AcerHotkeyEvent e)
        => (byte)e.Function == _settings.NitroKeyFunction
           && (_settings.NitroKeyNumber == 0xFF || e.KeyNumber == _settings.NitroKeyNumber);

    /// <summary>Advances to the next supported profile. Bound to the Nitro key.</summary>
    private async Task CycleProfileAsync()
    {
        if (SupportedProfiles.Count == 0) return;

        var index = SupportedProfiles.IndexOf(CurrentProfile);
        var next = SupportedProfiles[(index + 1) % SupportedProfiles.Count];

        await SetProfileAsync(next);
    }

    // ------------------------------------------------------------- display

    private void LoadRefreshRates()
    {
        try
        {
            var current = DisplayControl.GetCurrentMode();
            if (current is null) return;

            DisplayMode = $"{current.Width}x{current.Height}";

            RefreshRates.Clear();
            foreach (var hz in DisplayControl.GetAvailableRefreshRates()) RefreshRates.Add(hz);

            // Assign the backing field directly: going through the setter would
            // immediately re-apply the rate the display is already using.
            _selectedRefreshRate = current.RefreshHz;
            OnPropertyChanged(nameof(SelectedRefreshRate));
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("refresh rates", ex);
        }
    }

    private void ApplyRefreshRate(int hz)
    {
        var error = DisplayControl.SetRefreshRate(hz);
        if (error is null)
        {
            Status = $"Refresh rate set to {hz} Hz";
            Diagnostics.Write($"refresh rate -> {hz} Hz");
        }
        else
        {
            Status = error;
            Diagnostics.Write($"refresh rate {hz} Hz failed: {error}");
        }
    }

    private async Task RefreshOverdriveAsync()
    {
        try
        {
            var (state, raw) = await _hw.InvokeAsync(() =>
                ((bool?)_wmi!.GetLcdOverdrive(), _wmi.GetGamingProfileRaw()));

            OverdriveRaw = $"0x{raw:X16}";
            OverdriveSupported = state is not null;
            OverdriveEnabled = state ?? false;

            if (state is null)
                Diagnostics.Write($"overdrive state unrecognised, raw=0x{raw:X16}");
        }
        catch (Exception ex)
        {
            OverdriveSupported = false;
            Diagnostics.WriteException("overdrive read", ex);
        }
    }

    private async Task ToggleOverdriveAsync()
    {
        try
        {
            var target = !OverdriveEnabled;
            await _hw.InvokeAsync(() => _wmi!.SetLcdOverdrive(target));

            await RefreshOverdriveAsync();
            Status = OverdriveEnabled == target
                ? $"Display overdrive {(target ? "on" : "off")}"
                : "Overdrive write accepted but the reported state did not change";
        }
        catch (Exception ex)
        {
            Status = $"Overdrive toggle failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("overdrive toggle", ex);
        }
    }

    /// <summary>
    /// Applies the profile configured for a power source. Silently does nothing
    /// if that profile is not one the firmware advertises, rather than throwing
    /// on a value the user picked before we knew the capability set.
    /// </summary>
    private async Task ApplyProfileForPowerAsync(PowerSource source, string reason)
    {
        if (!IsHardwareAvailable) return;

        var target = source switch
        {
            PowerSource.AC => AcProfile,
            PowerSource.Battery => BatteryProfile,
            _ => (ThermalProfile?)null,
        };

        if (target is not { } profile) return;

        if (SupportedProfiles.Count > 0 && !SupportedProfiles.Contains(profile))
        {
            Diagnostics.Write($"auto-switch skipped: {profile} not supported by this firmware");
            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = $"Auto-switch skipped - {profile} is not supported");
            return;
        }

        try
        {
            await _hw.InvokeAsync(() => _wmi!.SetThermalProfile(profile));
            Diagnostics.Write($"auto-switch: {profile} ({reason})");
            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = $"Auto-switched to {profile} ({reason})");
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("auto-switch", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = $"Auto-switch failed - {Diagnostics.Describe(ex)}");
        }
    }

    private async Task SetProfileAsync(ThermalProfile profile)
    {
        try
        {
            await _hw.InvokeAsync(() => _wmi!.SetThermalProfile(profile));
            Status = $"Profile set to {profile}";
        }
        catch (Exception ex)
        {
            Status = $"Profile change failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("set profile", ex);
        }
    }

    private async Task ToggleCoolBoostAsync()
    {
        try
        {
            var target = CoolBoostEnabled ? FanMode.Auto : FanMode.Turbo;
            await _hw.InvokeAsync(() => _wmi!.SetFanMode(FanSelect.Both, target));
            Status = target == FanMode.Turbo ? "CoolBoost on" : "CoolBoost off";
        }
        catch (Exception ex)
        {
            Status = $"CoolBoost failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("coolboost", ex);
        }
    }

    private async Task ToggleCustomFanAsync()
    {
        try
        {
            if (CustomFanEnabled)
            {
                await _hw.InvokeAsync(() => { _fanGuard?.Dispose(); _fanGuard = null; });
                CustomFanEnabled = false;
                Status = "Fans returned to Auto";
                return;
            }

            var cpu = (byte)CpuDuty;
            var gpu = (byte)GpuDuty;

            await _hw.InvokeAsync(() =>
            {
                var guard = AcerFanGuard.Engage(_wmi!);
                guard.SetDuty(FanId.Cpu, cpu);
                guard.SetDuty(FanId.Gpu, gpu);
                _fanGuard = guard;
            });

            CustomFanEnabled = true;
            Status = "Custom fan control engaged (watchdog active)";
        }
        catch (Exception ex)
        {
            CustomFanEnabled = false;
            Status = $"Custom fan failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("custom fan", ex);
        }
    }

    private async Task ApplyDutyAsync()
    {
        var cpu = (byte)CpuDuty;
        var gpu = (byte)GpuDuty;

        try
        {
            await _hw.InvokeAsync(() =>
            {
                if (_fanGuard is null) return;
                _fanGuard.SetDuty(FanId.Cpu, cpu);
                _fanGuard.SetDuty(FanId.Gpu, gpu);
            });
        }
        catch (Exception ex)
        {
            Status = $"Duty change failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("set duty", ex);
        }
    }

    private async Task ToggleHealthModeAsync()
    {
        try
        {
            var target = !HealthModeEnabled;

            var actual = await _hw.InvokeAsync(() =>
            {
                _battery!.SetHealthMode(target);

                // The EC applies this asynchronously - verify, do not assume.
                for (var i = 0; i < 5; i++)
                {
                    Thread.Sleep(600);
                    if (_battery.GetHealthStatus().HealthModeEnabled == target) return target;
                }
                return _battery.GetHealthStatus().HealthModeEnabled;
            });

            HealthModeEnabled = actual;
            Status = actual == target
                ? $"Battery charge limit {(target ? "on (80%)" : "off")}"
                : "Battery write returned success but the state did not change";
        }
        catch (Exception ex)
        {
            Status = $"Battery toggle failed - {Diagnostics.Describe(ex)}";
            Diagnostics.WriteException("battery toggle", ex);
        }
    }

    private void RaiseCanExecuteChanged()
    {
        (SetProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleCoolBoostCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleCustomFanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleHealthModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleOverdriveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyGpuOffsetsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResetGpuOffsetsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyProcessorStateCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------ plumbing

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // Stopped first: it owns a thread blocked in a WMI call, and joining it
        // before tearing down COM avoids a call landing on a released proxy.
        _eventWatcher?.Dispose();

        // A live low-level hook outliving the process would wedge input.
        try { KeyboardHook.Stop(); } catch (Exception ex) { Diagnostics.WriteException("hook stop", ex); }

        // Disposal must also happen on the dispatcher thread - releasing the
        // fan guard writes to the EC to restore Auto.
        try
        {
            _hw.InvokeAsync(() =>
            {
                _fanGuard?.Dispose();
                _battery?.Dispose();
                _wmi?.Dispose();
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { Diagnostics.WriteException("shutdown", ex); }

        _nvApi?.Dispose();

        _hw.Dispose();
        _cts.Dispose();
        Diagnostics.Write("---- AcerHelper stopped ----");
    }
}

/// <summary>Minimal ICommand. Avoids an MVVM framework dependency under AOT.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}


