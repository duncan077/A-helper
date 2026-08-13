// SPDX-License-Identifier: GPL-3.0-or-later
//
// Acer battery health control - WMI class BatteryControl,
// GUID 79772EC5-04B1-4BFD-843C-61E7F77B6CC9.
//
// This is a SEPARATE ACPI device from AcerGamingFunction. The charge limit is
// not a gaming misc-setting index, which is why polling AcerGamingFunction
// never observes it change.
//
// Protocol from frederik-h/acer-wmi-battery (GPL-2.0), adapted for Windows:
// the Linux driver passes one packed struct per call, but the Windows ACPI-WMI
// mapper decomposes each struct into individually named parameters. Field names
// and array widths below are taken from the BIOS-declared class on ANV15-41.
//
// Verified on ANV15-41 / BIOS V1.51: uFunctionList = 0x03, i.e. health mode and
// calibration only. There is no charge-bypass / AC-passthrough function in this
// firmware - bits 2..7 are unset.

using System.Runtime.Versioning;
using AcerHelper.Hardware.Interop;

namespace AcerHelper.Hardware;

/// <summary>Battery health functions, as bits in uFunctionList / uFunctionMask.</summary>
[Flags]
public enum BatteryFunction : byte
{
    None = 0,

    /// <summary>Caps charging at roughly 80% to reduce wear.</summary>
    HealthMode = 1 << 0,

    /// <summary>Full discharge/recharge cycle to re-learn capacity. Disruptive.</summary>
    CalibrationMode = 1 << 1,
}

/// <summary>Decoded result of GetBatteryHealthControlStatus.</summary>
public sealed record BatteryHealthStatus(
    byte FunctionList,
    bool HealthModeSupported,
    bool HealthModeEnabled,
    bool CalibrationSupported,
    bool CalibrationEnabled,
    byte[] RawFunctionStatus)
{
    /// <summary>
    /// True when the firmware advertises functions beyond health and
    /// calibration. Always false on ANV15-41; kept because other models may
    /// differ and a bypass mode would surface here.
    /// </summary>
    public bool HasUndocumentedFunctions => (FunctionList & 0xFC) != 0;

    public byte UndocumentedBits => (byte)(FunctionList & 0xFC);
}

[SupportedOSPlatform("windows")]
public sealed class AcerBatteryWmi : IDisposable
{
    private const string WmiNamespace = @"root\WMI";
    private const string WmiClass = "BatteryControl";

    // The OEM software always uses battery index 1, and the firmware is known
    // to misbehave with other values. Machines with multiple packs are not
    // supported by this interface.
    private const byte BatteryIndex = 1;

    private const string GetStatus = "GetBatteryHealthControlStatus";
    private const string SetControl = "SetBatteryHealthControl";
    private const string GetInfo = "GetBattInfoInterface";

    private readonly WbemMethodChannel _channel;
    private readonly object _gate = new();
    private bool _disposed;

    private AcerBatteryWmi(WbemMethodChannel channel) => _channel = channel;

    public static AcerBatteryWmi Open()
    {
        if (!AcerGamingWmi.IsElevated)
            throw new UnauthorizedAccessException("BatteryControl requires an elevated process.");

        return new AcerBatteryWmi(WbemMethodChannel.Open(WmiNamespace, WmiClass));
    }

    public static AcerBatteryWmi? TryOpen()
    {
        try { return Open(); }
        catch { return null; }
    }

    /// <summary>Queries which health functions exist and whether each is on.</summary>
    public BatteryHealthStatus GetHealthStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Dictionary<string, object?> r;
        lock (_gate)
        {
            r = _channel.InvokeNamed(
                GetStatus,
                new Dictionary<string, object>
                {
                    ["uBatteryNo"] = BatteryIndex,
                    ["uFunctionQuery"] = (byte)1,
                    ["uReserved"] = new byte[2],
                },
                ["uFunctionList", "uFunctionStatus", "uReturn"]);
        }

        var list = (byte)(r["uFunctionList"] as ulong? ?? 0UL);
        var status = r["uFunctionStatus"] as byte[] ?? [];

        bool Enabled(int bit) => bit < status.Length && status[bit] > 0;

        return new BatteryHealthStatus(
            FunctionList: list,
            HealthModeSupported: (list & (byte)BatteryFunction.HealthMode) != 0,
            HealthModeEnabled: (list & (byte)BatteryFunction.HealthMode) != 0 && Enabled(0),
            CalibrationSupported: (list & (byte)BatteryFunction.CalibrationMode) != 0,
            CalibrationEnabled: (list & (byte)BatteryFunction.CalibrationMode) != 0 && Enabled(1),
            RawFunctionStatus: status);
    }

    /// <summary>
    /// Enables or disables the ~80% charge cap.
    /// </summary>
    /// <remarks>
    /// The firmware reports success before the EC has applied the change, so
    /// callers should re-read <see cref="GetHealthStatus"/> rather than assume.
    /// </remarks>
    public void SetHealthMode(bool enabled) => SetFunction(BatteryFunction.HealthMode, enabled);

    /// <summary>
    /// Starts or stops battery calibration.
    /// </summary>
    /// <remarks>
    /// Calibration fully discharges then recharges the pack, takes hours, and
    /// keeps the machine tied to AC. Never invoke it without an explicit,
    /// informed user action.
    /// </remarks>
    public void SetCalibrationMode(bool enabled) => SetFunction(BatteryFunction.CalibrationMode, enabled);

    private void SetFunction(BatteryFunction function, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var status = GetHealthStatus();
        var supported = function switch
        {
            BatteryFunction.HealthMode => status.HealthModeSupported,
            BatteryFunction.CalibrationMode => status.CalibrationSupported,
            _ => false,
        };

        if (!supported)
            throw new NotSupportedException(
                $"{function} is not offered by this firmware " +
                $"(uFunctionList = 0x{status.FunctionList:X2}).");

        Dictionary<string, object?> r;
        lock (_gate)
        {
            r = _channel.InvokeNamed(
                SetControl,
                new Dictionary<string, object>
                {
                    ["uBatteryNo"] = BatteryIndex,
                    ["uFunctionMask"] = (byte)function,
                    ["uFunctionStatus"] = (byte)(enabled ? 1 : 0),
                    ["uReservedIn"] = new byte[5],
                },
                ["uReturn", "uReservedOut"]);
        }

        var ret = r["uReturn"] as ulong? ?? 0UL;
        if (ret != 0)
            throw new InvalidOperationException(
                $"{SetControl}({function}, {enabled}) returned 0x{ret:X4}.");
    }

    /// <summary>
    /// Reads one battery information slot. Index 15 is design capacity in mAh
    /// on ANV15-41 (3733, matching the AP21D8M pack). Other indices are not yet
    /// identified, so this returns the raw value.
    /// </summary>
    public uint? GetBatteryInfo(uint index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            Dictionary<string, object?> r;
            lock (_gate)
            {
                r = _channel.InvokeNamed(
                    GetInfo,
                    new Dictionary<string, object>
                    {
                        ["uBatteryInfoIndex"] = index,
                        ["uBatteryNo"] = (uint)BatteryIndex,
                    },
                    ["uReturn"]);
            }

            return r["uReturn"] is ulong v ? (uint)v : null;
        }
        catch (ComCallException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}
