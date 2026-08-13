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
