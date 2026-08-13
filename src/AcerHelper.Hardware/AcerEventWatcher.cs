// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fn / hotkey reception via the APGeEvent WMI event class,
// GUID 676AA15E-6A47-4D9F-A2CC-1E6D18D14026.
//
// Runs on its own MTA thread with its own WMI connection. The delivery call
// blocks until an event arrives, so sharing AcerHardwareDispatcher would stall
// sensor polling for as long as the user did not press a key.
//
// The BIOS declares the event's property names, and they are not documented
// anywhere. Rather than guess, every property is read generically and exposed on
// AcerHotkeyEvent.Raw; the decoder below maps what it recognises and leaves the
// rest visible so unknown layouts can be identified from a log.

using System.Runtime.Versioning;
using AcerHelper.Hardware.Interop;

namespace AcerHelper.Hardware;

/// <summary>Event function IDs, per acer-wmi.c.</summary>
public enum AcerEventFunction : byte
{
    Unknown = 0x00,
    Hotkey = 0x01,
    Backlight = 0x04,
    AccelerometerOrKeyboardDock = 0x05,

    /// <summary>The Nitro / Turbo key.</summary>
    GamingTurboKey = 0x07,

    AcAdapter = 0x08,
}

public sealed record AcerHotkeyEvent(
    AcerEventFunction Function,
    byte KeyNumber,
    IReadOnlyDictionary<string, object?> Raw)
{
    /// <summary>Renders the raw payload for logging when the layout is unrecognised.</summary>
    public string DescribeRaw() => string.Join("  ", Raw.Select(kv => kv.Value switch
    {
        byte[] b => $"{kv.Key}=[{Convert.ToHexString(b)}]",
        null => $"{kv.Key}=<null>",
        _ => $"{kv.Key}={kv.Value}",
    }));
}

[SupportedOSPlatform("windows")]
public sealed class AcerEventWatcher : IDisposable
{
    private const string WmiNamespace = @"root\WMI";
    private const string Query = "SELECT * FROM APGeEvent";

    // Long enough that idle wake-ups are rare, short enough that Dispose is not
    // left waiting on a blocked Next() for an uncomfortable time.
    private const int PollTimeoutMs = 1000;

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private bool _disposed;

    /// <summary>Raised on the watcher thread. Marshal to the UI thread yourself.</summary>
    public event EventHandler<AcerHotkeyEvent>? EventReceived;

    /// <summary>Raised when the watcher stops because of an error.</summary>
    public event EventHandler<Exception>? Failed;

    private AcerEventWatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AcerEventWatcher",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public static AcerEventWatcher Start()
    {
        var watcher = new AcerEventWatcher();
        watcher._thread.Start();
        return watcher;
    }

    private void Loop()
    {
        WbemNotificationChannel? channel = null;

        try
        {
            channel = WbemNotificationChannel.Open(WmiNamespace, Query);

            while (!_cts.IsCancellationRequested)
            {
                var properties = channel.WaitForEvent(PollTimeoutMs);
                if (properties is null) continue;      // timeout, not an error

                var decoded = Decode(properties);
                try { EventReceived?.Invoke(this, decoded); }
                catch { /* a handler fault must not kill the watcher */ }
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) Failed?.Invoke(this, ex);
        }
        finally
        {
            channel?.Dispose();
        }
    }

    /// <summary>
    /// Maps the event payload onto function and key number.
    ///
    /// acer-wmi.c describes the payload as a packed struct whose first two bytes
    /// are function and key_num. Windows may surface that as a byte array or as
    /// separate named properties depending on the BIOS's declaration, so both
    /// shapes are handled and anything unrecognised still reaches Raw.
    /// </summary>
    private static AcerHotkeyEvent Decode(Dictionary<string, object?> properties)
    {
        // Shape 1: a byte array carrying the packed struct.
        foreach (var value in properties.Values)
        {
            if (value is byte[] { Length: >= 2 } bytes)
                return new AcerHotkeyEvent((AcerEventFunction)bytes[0], bytes[1], properties);
        }

        // Shape 2: separate scalar fields.
        var function = FindByte(properties, "function", "EventID", "eventid", "func");
        var keyNumber = FindByte(properties, "key_num", "keynum", "KeyNumber", "key");

        return new AcerHotkeyEvent(
            (AcerEventFunction)(function ?? 0),
            keyNumber ?? 0,
            properties);
    }

    private static byte? FindByte(Dictionary<string, object?> properties, params string[] candidates)
    {
        foreach (var name in candidates)
            if (properties.TryGetValue(name, out var v) && v is ulong u)
                return (byte)u;

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0 + 2));
        _cts.Dispose();
    }
}
