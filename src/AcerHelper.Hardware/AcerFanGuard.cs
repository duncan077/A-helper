// SPDX-License-Identifier: GPL-3.0-or-later
//
// Safety wrapper around FanMode.Custom.
//
// Putting a fan into Custom mode stops the EC regulating it. From that moment
// the duty cycle is whatever was last written - including 0% - and it PERSISTS
// after the controlling process exits. A crash while in Custom mode leaves the
// machine with unregulated fans.
//
// This class exists so that never happens silently. It:
//   * refuses duty below a floor
//   * reverts to Auto if the caller stops sending heartbeats
//   * reverts to Auto if any temperature crosses a ceiling
//   * reverts to Auto on Dispose, process exit, Ctrl+C and unhandled exceptions
//
// Fail-safe, not fail-quiet: every automatic revert raises SafetyTripped.

using System.Runtime.Versioning;

namespace AcerHelper.Hardware;

public sealed record AcerFanGuardOptions
{
    /// <summary>
    /// Lowest NON-ZERO duty the guard will program. Between 1 and this value a
    /// fan may be commanded to spin but not have enough drive to actually turn,
    /// so those values are lifted to the floor.
    /// </summary>
    /// <remarks>
    /// Zero is explicitly allowed and means "fan off". The EC does this itself -
    /// an idle ANV15-41 in Quiet reports 0 rpm - so forcing a floor on every
    /// value would make a custom curve noisier than stock at idle, which is the
    /// opposite of what a quiet profile is for. The temperature ceiling still
    /// guards against a curve that leaves the fans off for too long.
    /// </remarks>
    public byte MinimumDutyPercent { get; init; } = 25;

    /// <summary>Whether a duty of 0 (fans stopped) may be programmed.</summary>
    public bool AllowFansOff { get; init; } = true;

    /// <summary>Any monitored temperature at or above this reverts to Auto.</summary>
    public int MaxTemperatureC { get; init; } = 92;

    /// <summary>Revert to Auto if no heartbeat arrives within this window.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the watchdog checks temperature and heartbeat age.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Fans placed under manual control.</summary>
    public FanSelect Fans { get; init; } = FanSelect.Both;
}

public sealed class SafetyTrippedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

[SupportedOSPlatform("windows")]
public sealed class AcerFanGuard : IDisposable
{
    private readonly AcerGamingWmi _wmi;
    private readonly AcerFanGuardOptions _options;
    private readonly Timer _watchdog;
    private readonly object _gate = new();

    private readonly EventHandler _processExit;
    private readonly ConsoleCancelEventHandler _cancelKey;
    private readonly UnhandledExceptionEventHandler _unhandled;

    private long _lastHeartbeatTicks;
    private bool _engaged;
    private bool _disposed;

    /// <summary>Raised when the guard reverts to Auto on its own.</summary>
    public event EventHandler<SafetyTrippedEventArgs>? SafetyTripped;

    public bool IsEngaged { get { lock (_gate) return _engaged; } }

    private AcerFanGuard(AcerGamingWmi wmi, AcerFanGuardOptions options)
    {
        _wmi = wmi;
        _options = options;
        _lastHeartbeatTicks = Environment.TickCount64;

        _processExit = (_, _) => Release("process exit");
        _cancelKey = (_, _) => Release("console cancel");
        _unhandled = (_, _) => Release("unhandled exception");

        AppDomain.CurrentDomain.ProcessExit += _processExit;
        AppDomain.CurrentDomain.UnhandledException += _unhandled;
        Console.CancelKeyPress += _cancelKey;

        _watchdog = new Timer(Tick, null, options.PollInterval, options.PollInterval);
    }

    /// <summary>
    /// Switches the selected fans to Custom mode and starts the watchdog.
    /// Call <see cref="Heartbeat"/> at least once per
    /// <see cref="AcerFanGuardOptions.HeartbeatTimeout"/> or control reverts.
    /// </summary>
    public static AcerFanGuard Engage(AcerGamingWmi wmi, AcerFanGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        options ??= new AcerFanGuardOptions();

        var guard = new AcerFanGuard(wmi, options);
        try
        {
            wmi.SetFanMode(options.Fans, FanMode.Custom);
            lock (guard._gate) guard._engaged = true;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
        return guard;
    }

    /// <summary>Programs a duty cycle, clamped to the configured floor.</summary>
    public void SetDuty(FanId fan, byte percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_engaged)
                throw new InvalidOperationException(
                    "Guard is not engaged - fans have reverted to Auto.");

            // 0 passes through as "off"; anything else is lifted clear of the
            // stall band. See AcerFanGuardOptions.MinimumDutyPercent.
            var clamped = percent == 0 && _options.AllowFansOff
                ? (byte)0
                : Math.Clamp(percent, _options.MinimumDutyPercent, (byte)100);

            _wmi.SetFanDuty(fan, clamped);
            _lastHeartbeatTicks = Environment.TickCount64;
        }
    }

    /// <summary>Signals that the controller is still alive.</summary>
    public void Heartbeat() => Interlocked.Exchange(ref _lastHeartbeatTicks, Environment.TickCount64);

    private void Tick(object? _)
    {
        if (_disposed) return;

        try
        {
            var age = Environment.TickCount64 - Interlocked.Read(ref _lastHeartbeatTicks);
            if (age > _options.HeartbeatTimeout.TotalMilliseconds)
            {
                Release($"heartbeat stale ({age} ms)");
                return;
            }

            var readings = _wmi.ReadAllSensors();
            var hottest = Math.Max(
                readings.CpuTemperatureC ?? 0,
                Math.Max(readings.ExternalTemperature2C ?? 0, readings.GpuTemperatureC ?? 0));

            if (hottest >= _options.MaxTemperatureC)
                Release($"temperature ceiling reached ({hottest} C)");
        }
        catch (Exception ex)
        {
            // If we cannot verify the machine is safe, we do not stay in control.
            Release($"watchdog error: {ex.Message}");
        }
    }

    /// <summary>Returns the fans to EC control. Safe to call repeatedly.</summary>
    public void Release(string reason = "released")
    {
        bool wasEngaged;
        lock (_gate)
        {
            wasEngaged = _engaged;
            if (wasEngaged)
            {
                try { _wmi.SetFanMode(_options.Fans, FanMode.Auto); }
                catch { /* nothing useful left to do; never mask the original reason */ }
                _engaged = false;
            }
        }

        if (wasEngaged && reason != "released")
            SafetyTripped?.Invoke(this, new SafetyTrippedEventArgs(reason));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchdog.Dispose();
        Release();

        AppDomain.CurrentDomain.ProcessExit -= _processExit;
        AppDomain.CurrentDomain.UnhandledException -= _unhandled;
        try { Console.CancelKeyPress -= _cancelKey; } catch { /* no console */ }
    }
}
