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

/// <summary>Event function IDs.</summary>
public enum AcerEventFunction : byte
{
    Unknown = 0x00,

    /// <summary>Keyboard hotkey; <c>KeyNumber</c> identifies which.</summary>
    Hotkey = 0x01,

    /// <summary>
    /// Not in acer-wmi.c. Observed on ANV15-41 with KeyNumber = 1. Purpose
    /// unconfirmed - a candidate for the Nitro key on models that do not emit
    /// <see cref="GamingTurboKey"/>.
    /// </summary>
    Undocumented02 = 0x02,

    Backlight = 0x04,
    AccelerometerOrKeyboardDock = 0x05,

    /// <summary>
    /// The Nitro / Turbo key, per acer-wmi.c. NOT emitted by ANV15-41 -
    /// nothing on that machine produces this function.
    /// </summary>
    GamingTurboKey = 0x07,

    AcAdapter = 0x08,

    /// <summary>
    /// Not in acer-wmi.c. On ANV15-41 this fires immediately after
    /// <see cref="AcAdapter"/> with the same KeyNumber (0 = unplugged,
    /// 1 = plugged), so it appears to mirror AC state.
    /// </summary>
    Undocumented09 = 0x09,
}

public sealed record AcerHotkeyEvent(
    AcerEventFunction Function,
    byte KeyNumber,
    ushort DeviceState,
    IReadOnlyDictionary<string, object?> Raw)
{
    /// <summary>
    /// Hotkey scancode names, from the acer-wmi.c keymap. Present so an unknown
    /// key can be identified from a log without cross-referencing the kernel.
    /// </summary>
    public string KeyName => Function == AcerEventFunction.Hotkey
        ? KeyNumber switch
        {
            0x01 or 0x03 or 0x04 or 0x86 => "WLAN",
            0x12 => "Bluetooth",
            0x21 => "Backup",
            0x22 => "Arcade",
            0x23 or 0x29 => "P-key",
            0x24 => "Social",
            0x27 => "Help",
            0x41 => "Mute",
            0x42 or 0x4D => "PreviousTrack",
            0x43 or 0x4E => "NextTrack",
            0x44 or 0x4F => "PlayPause",
            0x45 or 0x50 => "Stop",
            0x48 => "VolumeUp",
            0x49 or 0x4A => "VolumeDown",
            0x61 => "SwitchVideoMode",
            0x62 => "BrightnessUp",
            0x63 => "BrightnessDown",
            0x64 => "DisplaySwitch",
            0x81 => "Sleep",
            0x82 or 0x83 or 0x85 => "TouchpadToggle",
            0x84 => "KeyboardBacklightToggle",
            0x87 => "Power",
            _ => $"unmapped(0x{KeyNumber:X2})",
        }
        : "-";

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
    /// Maps the event payload onto function, key number and device state.
    ///
    /// Confirmed on ANV15-41: the BIOS exposes the packed struct from acer-wmi.c
    /// as an 8-byte array named EventDetail, laid out
    ///   [0] function  [1] key_num  [2..3] device_state (LE)  [4..7] reserved
    /// e.g. 01 84 08 00 .. = Hotkey, KeyboardBacklightToggle, state 0x0008.
    ///
    /// The scalar fallback stays for BIOSes that decompose the struct instead.
    /// </summary>
    private static AcerHotkeyEvent Decode(Dictionary<string, object?> properties)
    {
        // Prefer the named property, but accept any byte array - the name is
        // BIOS-declared and may differ on other models.
        var payload = properties.TryGetValue("EventDetail", out var detail) && detail is byte[] named
            ? named
            : properties.Values.OfType<byte[]>().FirstOrDefault(b => b.Length >= 2);

        if (payload is { Length: >= 2 })
        {
            ushort state = payload.Length >= 4
                ? (ushort)(payload[2] | (payload[3] << 8))
                : (ushort)0;

            return new AcerHotkeyEvent((AcerEventFunction)payload[0], payload[1], state, properties);
        }

        var function = FindByte(properties, "function", "EventID", "eventid", "func");
        var keyNumber = FindByte(properties, "key_num", "keynum", "KeyNumber", "key");

        return new AcerHotkeyEvent(
            (AcerEventFunction)(function ?? 0),
            keyNumber ?? 0,
            0,
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
