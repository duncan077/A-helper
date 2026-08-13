// SPDX-License-Identifier: GPL-3.0-or-later
//
// Typed access to the AcerGamingFunction ACPI-WMI class.
//
// Calling convention, as declared by the BIOS and confirmed on ANV15-41:
//   Get* methods take UInt32 gmInput  and return UInt64 gmOutput
//   Set* methods take UInt64 gmInput  and return UInt32 gmOutput
// In both directions the LOW BYTE of the result is a status code; 0 == success.
//
// Transport is AcerHelper.Hardware.Interop.WbemMethodChannel rather than
// System.Management, because the latter is reflection-based and breaks under
// PublishAot (MissingMethodException: WbemDefPath). The channel picks each
// input's VARIANT representation from the CIM type the BIOS declares, so the
// UInt32/UInt64 asymmetry above is handled automatically.

using System.Runtime.Versioning;
using System.Security.Principal;
using AcerHelper.Hardware.Interop;

namespace AcerHelper.Hardware;

[SupportedOSPlatform("windows")]
public sealed class AcerGamingWmi : IDisposable
{
    private const string WmiNamespace = @"root\WMI";
    private const string WmiClass = "AcerGamingFunction";

    private readonly WbemMethodChannel _channel;
    private readonly object _gate = new();
    private bool _disposed;

    private AcerGamingWmi(WbemMethodChannel channel) => _channel = channel;

    /// <summary>True when the current process is running elevated.</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Opens the gaming interface. Throws when the class is absent (non-Acer, or
    /// a model whose BIOS does not expose it) or when not elevated.
    /// </summary>
    public static AcerGamingWmi Open()
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException(
                "AcerGamingFunction requires an elevated process.");

        return new AcerGamingWmi(WbemMethodChannel.Open(WmiNamespace, WmiClass));
    }

    /// <summary>Attempts to open the interface, returning null instead of throwing.</summary>
    public static AcerGamingWmi? TryOpen()
    {
        try { return Open(); }
        catch { return null; }
    }

    /// <summary>
    /// Exercises the COM transport against a class every Windows machine has,
    /// with no elevation and no Acer hardware required. Used to prove the AOT
    /// interop path works independently of whether this is an Acer.
    /// </summary>
    public static void InteropSelfTest()
    {
        using var channel = WbemMethodChannel.Open(@"root\CIMV2", "Win32_OperatingSystem");
    }

    // ------------------------------------------------------------- raw calls

    /// <summary>Invokes a method by name without checking the status byte.</summary>
    public ulong InvokeRaw(AcerMethod method, ulong input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate) return _channel.Invoke(method.ToString(), input);
    }

    private ulong InvokeChecked(AcerMethod method, ulong input)
    {
        var raw = InvokeRaw(method, input);
        var status = (byte)(raw & 0xFF);
        if (status != 0) throw new AcerWmiException(method, status, raw);
        return raw;
    }

    private static ulong Field(ulong value, int lowBit, int highBit)
    {
        var width = highBit - lowBit + 1;
        var mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        return (value >> lowBit) & mask;
    }

    // ------------------------------------------------------ thermal profiles

    public ThermalProfile GetThermalProfile()
    {
        var raw = InvokeChecked(AcerMethod.GetGamingMiscSetting, (byte)MiscSetting.PlatformProfile);
        return (ThermalProfile)(byte)Field(raw, 8, 15);
    }

    public void SetThermalProfile(ThermalProfile profile)
    {
        ulong input = (byte)MiscSetting.PlatformProfile | ((ulong)(byte)profile << 8);
        InvokeChecked(AcerMethod.SetGamingMiscSetting, input);
    }

    /// <summary>
    /// Reads the supported-profile bitmap from misc setting 0x0A. ANV15-41
    /// returns 0x53 => Quiet, Balanced, Performance, Eco (no Turbo).
    /// </summary>
    /// <remarks>
    /// The kernel flags this index as unreliable on some models. Round-trip each
    /// profile before trusting it on unknown hardware.
    /// </remarks>
    public IReadOnlyList<ThermalProfile> GetSupportedProfiles()
    {
        var raw = InvokeChecked(AcerMethod.GetGamingMiscSetting, (byte)MiscSetting.SupportedProfiles);
        var bitmap = (byte)Field(raw, 8, 15);

        var result = new List<ThermalProfile>();
        foreach (var p in Enum.GetValues<ThermalProfile>())
            if ((bitmap & (1 << (byte)p)) != 0) result.Add(p);

        return result;
    }

    public byte GetMiscSetting(byte index)
        => (byte)Field(InvokeChecked(AcerMethod.GetGamingMiscSetting, index), 8, 15);

    /// <summary>Reads a misc setting without throwing; null when unsupported.</summary>
    public byte? TryGetMiscSetting(byte index)
    {
        try { return GetMiscSetting(index); }
        catch (AcerWmiException) { return null; }
        catch (ComCallException) { return null; }
    }

    // -------------------------------------------------------------- sensors

    /// <summary>Bitmap of sensors this machine implements (bit N => sensor id N+1).</summary>
    public ushort GetSupportedSensorBitmap()
    {
        var raw = InvokeChecked(AcerMethod.GetGamingSysInfo, (ushort)SysInfoCommand.SupportedSensors);
        return (ushort)Field(raw, 24, 39);
    }

    public bool IsSensorSupported(SensorId sensor)
        => (GetSupportedSensorBitmap() & (1 << ((byte)sensor - 1))) != 0;

    public int? ReadSensor(SensorId sensor)
    {
        ulong input = (ushort)SysInfoCommand.SensorReading | ((ulong)(byte)sensor << 8);
        try { return (int)Field(InvokeChecked(AcerMethod.GetGamingSysInfo, input), 8, 23); }
        catch (AcerWmiException) { return null; }
    }

    /// <summary>Reads every sensor the machine advertises in one pass.</summary>
    public SensorReadings ReadAllSensors()
    {
        var bitmap = GetSupportedSensorBitmap();
        int? Read(SensorId s) => (bitmap & (1 << ((byte)s - 1))) != 0 ? ReadSensor(s) : null;

        return new SensorReadings(
            CpuTemperatureC: Read(SensorId.CpuTemperature),
            CpuFanRpm: Read(SensorId.CpuFanSpeed),
            ExternalTemperature2C: Read(SensorId.ExternalTemperature2),
            GpuFanRpm: Read(SensorId.GpuFanSpeed),
            GpuTemperatureC: Read(SensorId.GpuTemperature));
    }

    // ---------------------------------------------------------- fan control

    public FanMode GetFanMode(FanSelect fan)
    {
        var raw = InvokeChecked(AcerMethod.GetGamingFanBehavior, (ushort)fan);

        // Getters use different bit positions than setters - see acer-wmi.c.
        return fan.HasFlag(FanSelect.Cpu)
            ? (FanMode)(byte)Field(raw, 8, 9)
            : (FanMode)(byte)Field(raw, 14, 15);
    }

    /// <summary>
    /// Sets fan behaviour. Selecting <see cref="FanMode.Custom"/> hands thermal
    /// responsibility to the host - prefer <see cref="AcerFanGuard"/> over
    /// calling this directly.
    /// </summary>
    public void SetFanMode(FanSelect fan, FanMode mode)
    {
        ulong input = (ushort)fan;
        if (fan.HasFlag(FanSelect.Cpu)) input |= (ulong)(byte)mode << 16;
        if (fan.HasFlag(FanSelect.Gpu)) input |= (ulong)(byte)mode << 22;

        InvokeChecked(AcerMethod.SetGamingFanBehavior, input);
    }

    /// <summary>Reads the programmed manual duty. Reports 0 while the fan is in Auto.</summary>
    public byte GetFanDuty(FanId fan)
        => (byte)Field(InvokeChecked(AcerMethod.GetGamingFanSpeed, (byte)fan), 8, 15);

    /// <summary>
    /// Sets manual duty (0-100). Only meaningful once the fan is in
    /// <see cref="FanMode.Custom"/>.
    /// </summary>
    public void SetFanDuty(FanId fan, byte percent)
    {
        if (percent > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Duty must be 0-100.");

        // Status codes on this method, per acer-wmi.c:
        //   0x01 => no such device, 0x02 => invalid value.
        ulong input = (byte)fan | ((ulong)percent << 8);
        InvokeChecked(AcerMethod.SetGamingFanSpeed, input);
    }

    // ------------------------------------------------------- display overdrive

    // Overdrive ("LCD override") is driven through Set/GetGamingProfile, NOT a
    // misc-setting index. Encoding from Linuwu-Sense (GPL-2.0).
    private const ulong OverdriveSetOn = 0x1000000000010;
    private const ulong OverdriveSetOff = 0x10;
    private const ulong OverdriveStateBit = 1UL << 48;

    /// <summary>Raw GetGamingProfile result. Exposed because the encoding varies by model.</summary>
    public ulong GetGamingProfileRaw() => InvokeRaw(AcerMethod.GetGamingProfile, 0);

    /// <summary>
    /// Reads display overdrive state, or null when the value does not match a
    /// known encoding.
    /// </summary>
    /// <remarks>
    /// Linuwu-Sense compares the whole word against 0x1000001000000 (on) and
    /// 0x1000000 (off). ANV15-41 returns 0x00FF000001000000, which matches
    /// neither - the 0xFF in bits 55:48 looks like a capability mask rather than
    /// state. This reads bit 48 alone, which is consistent with both encodings,
    /// but it is UNVERIFIED on this model: confirm with a set/read round-trip
    /// before trusting it.
    /// </remarks>
    public bool? GetLcdOverdrive()
    {
        var raw = GetGamingProfileRaw();
        if ((raw & 0xFF) != 0) return null;              // non-zero status
        if (raw == 0) return null;                        // nothing meaningful

        return (raw & OverdriveStateBit) != 0;
    }

    /// <summary>Enables or disables display overdrive.</summary>
    /// <remarks>Unverified on ANV15-41 - always confirm by reading back.</remarks>
    public void SetLcdOverdrive(bool enabled)
        => InvokeChecked(AcerMethod.SetGamingProfile, enabled ? OverdriveSetOn : OverdriveSetOff);

    // ------------------------------------------------------------- keyboard

    /// <summary>Keyboard backlight level. ANV15-41 has single-zone white backlight.</summary>
    public byte? TryGetKeyboardBacklight()
    {
        try { return (byte)Field(InvokeChecked(AcerMethod.GetGamingKBBacklight, 0), 8, 15); }
        catch (AcerWmiException) { return null; }
        catch (ComCallException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}
