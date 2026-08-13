// SPDX-License-Identifier: GPL-3.0-or-later
//
// AMD SMU access for Curve Optimizer undervolting and temperature limits.
//
// Mailbox addresses and command IDs follow G-Helper's app/Pawn/RyzenSmu.cs
// (GPL-3.0), which in turn credits RyzenAdj and UXTU. Register access goes
// through the PawnIO RyzenSMU module's ioctl_read/write_smu_register.
//
// SAFETY
//
// This writes to the System Management Unit. A wrong mailbox address or command
// can hang the machine, so:
//
//   * The feature stays disabled unless a read-only probe succeeds first
//     (module loads, code name resolves, SMU version reads back).
//   * Only CPU families with a verified command table are enabled; anything
//     else reports Unsupported rather than guessing.
//   * Offsets are clamped, and rejected outright if the mailbox reports busy.
//
// SMU settings are volatile: a reboot clears them. That is the main reason an
// aggressive Curve Optimizer value is recoverable rather than permanent.

using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Amd;

/// <summary>Status returned by the SMU mailbox.</summary>
public enum SmuStatus : uint
{
    Busy = 0x00,
    Ok = 0x01,
    CmdRejectedBusy = 0xFC,
    CmdRejectedPrereq = 0xFD,
    UnknownCmd = 0xFE,
    Failed = 0xFF,
}

/// <summary>
/// SMU command routing family. Distinct from <see cref="RyzenCodename"/>:
/// several codenames share one mailbox and command set.
/// </summary>
public enum SmuFamily
{
    Unsupported,
    Renoir,
    Mobile,      // Cezanne, Rembrandt, Phoenix, HawkPoint, Mendocino
    Raphael,     // and Matisse-style desktop
}

[SupportedOSPlatform("windows")]
public sealed class RyzenSmu : IDisposable
{
    private const string ModuleName = "RyzenSMU.bin";
    private const int MailboxTimeoutMs = 200;

    // The RyzenSMU module warns that PCI config access must be serialised
    // against other tools. This is the conventional name used for that.
    private const string PciMutexName = @"Global\Access_PCI";

    private readonly PawnIoModule _module;
    private readonly Mutex? _pciMutex;
    private bool _disposed;

    public CpuIdentity Cpu { get; }
    public SmuFamily Family { get; }
    public uint SmuVersion { get; }

    /// <summary>True only when a read-only probe confirmed a working mailbox.</summary>
    public bool IsUsable => Family != SmuFamily.Unsupported && SmuVersion != 0;

    private RyzenSmu(PawnIoModule module, Mutex? pciMutex, CpuIdentity cpu,
                     SmuFamily family, uint smuVersion)
    {
        _module = module;
        _pciMutex = pciMutex;
        Cpu = cpu;
        Family = family;
        SmuVersion = smuVersion;
    }

    /// <summary>
    /// Loads the module and validates the mailbox with read-only calls. Returns
    /// null when anything is missing - the caller must treat that as "feature
    /// unavailable", never as "try anyway".
    /// </summary>
    public static RyzenSmu? TryOpen(out PawnIoStatus status)
    {
        status = PawnIoStatus.Ok;

        var cpu = CpuInfo.Identify();
        if (!cpu.IsAmd)
        {
            status = PawnIoStatus.ModuleNotFound;
            return null;
        }

        var module = PawnIoModule.Load(ModuleName, out status);
        if (module is null) return null;

        // Read-only validation. If either call fails the SMU is not talking to
        // us and nothing further may be attempted.
        var codeNameOut = new ulong[1];
        var versionOut = new ulong[1];

        if (!module.Execute("ioctl_get_code_name", null, codeNameOut) ||
            !module.Execute("ioctl_get_smu_version", null, versionOut) ||
            versionOut[0] == 0)
        {
            module.Dispose();
            status = PawnIoStatus.ModuleLoadFailed;
            return null;
        }

        Mutex? pciMutex = null;
        try { pciMutex = new Mutex(false, PciMutexName); }
        catch { /* another tool may own it with a restrictive ACL; proceed unsynchronised */ }

        return new RyzenSmu(module, pciMutex, cpu, MapFamily(cpu.Codename), (uint)versionOut[0]);
    }

    private static SmuFamily MapFamily(RyzenCodename codename) => codename switch
    {
        RyzenCodename.Renoir => SmuFamily.Renoir,

        RyzenCodename.Cezanne or RyzenCodename.Rembrandt or RyzenCodename.Phoenix
            or RyzenCodename.HawkPoint => SmuFamily.Mobile,

        RyzenCodename.Raphael or RyzenCodename.Matisse or RyzenCodename.Vermeer
            or RyzenCodename.Granite => SmuFamily.Raphael,

        _ => SmuFamily.Unsupported,
    };

    // ------------------------------------------------------------- mailboxes

    private (uint Cmd, uint Rsp, uint Arg) Mp1() => Family switch
    {
        SmuFamily.Renoir => (0x03B10528, 0x03B10564, 0x03B10998),
        SmuFamily.Mobile => (0x03B10528, 0x03B10578, 0x03B10998),
        SmuFamily.Raphael => (0x03B10530, 0x03B1057C, 0x03B109C4),
        _ => (0u, 0u, 0u),
    };

    private (uint Cmd, uint Rsp, uint Arg) Psmu() => Family switch
    {
        SmuFamily.Renoir or SmuFamily.Mobile => (0x03B10A20, 0x03B10A80, 0x03B10A88),
        SmuFamily.Raphael => (0x03B10524, 0x03B10570, 0x03B10A40),
        _ => (0u, 0u, 0u),
    };

    // ------------------------------------------------------- register access

    private bool ReadRegister(uint address, out uint value)
    {
        var output = new ulong[1];
        var ok = _module.Execute("ioctl_read_smu_register", [address], output);
        value = ok ? (uint)output[0] : 0;
        return ok;
    }

    private bool WriteRegister(uint address, uint value)
        => _module.Execute("ioctl_write_smu_register", [address, value], null);

    /// <summary>
    /// Runs one mailbox transaction: clear response, write args, write command,
    /// poll for a non-zero response.
    /// </summary>
    private SmuStatus Send(uint cmdAddr, uint rspAddr, uint argAddr, uint command, uint argument)
    {
        if (_disposed || cmdAddr == 0) return SmuStatus.Failed;

        var held = false;
        try
        {
            try { held = _pciMutex?.WaitOne(5000) ?? true; }
            catch (AbandonedMutexException) { held = true; }   // previous owner died

            if (!held) return SmuStatus.CmdRejectedBusy;

            // Refuse to start if a previous transaction never completed.
            if (!WaitForIdle(rspAddr)) return SmuStatus.CmdRejectedBusy;

            if (!WriteRegister(rspAddr, 0)) return SmuStatus.Failed;

            for (var i = 0u; i < 6; i++)
                if (!WriteRegister(argAddr + (i * 4), i == 0 ? argument : 0))
                    return SmuStatus.Failed;

            if (!WriteRegister(cmdAddr, command)) return SmuStatus.Failed;

            var deadline = Environment.TickCount64 + MailboxTimeoutMs;
            var spins = 0;
            uint status = 0;

            while (Environment.TickCount64 < deadline)
            {
                if (!ReadRegister(rspAddr, out status)) return SmuStatus.Failed;
                if (status != 0) break;

                spins++;
                if (spins > 256) Thread.Sleep(1);
                else if (spins > 32) Thread.Yield();
            }

            return status == 0 ? SmuStatus.Failed : (SmuStatus)status;
        }
        finally
        {
            if (held) { try { _pciMutex?.ReleaseMutex(); } catch { /* not owned */ } }
        }
    }

    private bool WaitForIdle(uint rspAddr)
    {
        var deadline = Environment.TickCount64 + MailboxTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadRegister(rspAddr, out var value)) return false;
            if (value != 0) return true;
            Thread.Yield();
        }
        return false;
    }

    private SmuStatus SendMp1(uint command, uint argument)
    {
        var (c, r, a) = Mp1();
        return Send(c, r, a, command, argument);
    }

    private SmuStatus SendPsmu(uint command, uint argument)
    {
        var (c, r, a) = Psmu();
        return Send(c, r, a, command, argument);
    }

    // ------------------------------------------------------ public operations

    /// <summary>Widest offset this code will program, in Curve Optimizer counts.</summary>
    public const int MinCurveOffset = -50;
    public const int MaxCurveOffset = 10;

    /// <summary>Curve Optimizer offsets are a 16-bit two's complement magnitude.</summary>
    private static uint EncodeCurve(int value) => (uint)(value & 0xFFFF);

    /// <summary>
    /// Applies an all-core Curve Optimizer offset. Negative undervolts.
    /// </summary>
    /// <remarks>
    /// Instability from an aggressive value typically appears only under load,
    /// not immediately. Callers should treat a successful return as "accepted",
    /// not as "stable".
    /// </remarks>
    public SmuStatus SetCurveOptimizerAll(int offset)
    {
        if (!IsUsable) return SmuStatus.Failed;

        offset = Math.Clamp(offset, MinCurveOffset, MaxCurveOffset);
        var v = EncodeCurve(offset);

        return Family switch
        {
            SmuFamily.Renoir => SendMp1(0x55, v),
            SmuFamily.Mobile => SendMp1(0x4C, v),
            SmuFamily.Raphael => SendPsmu(0x07, v),
            _ => SmuStatus.Failed,
        };
    }

    /// <summary>Applies a Curve Optimizer offset to the integrated GPU.</summary>
    public SmuStatus SetCurveOptimizerGfx(int offset)
    {
        if (!IsUsable) return SmuStatus.Failed;

        offset = Math.Clamp(offset, MinCurveOffset, MaxCurveOffset);
        var v = EncodeCurve(offset);

        return Family switch
        {
            SmuFamily.Renoir => SendMp1(0x64, v),
            SmuFamily.Mobile => SendPsmu(0xB7, v),
            _ => SmuStatus.Failed,
        };
    }

    /// <summary>Sets the CPU temperature limit in degrees Celsius.</summary>
    public SmuStatus SetTemperatureLimit(int celsius)
    {
        if (!IsUsable) return SmuStatus.Failed;

        var v = (uint)Math.Clamp(celsius, 60, 100);

        return Family switch
        {
            SmuFamily.Renoir or SmuFamily.Mobile => SendMp1(0x19, v),
            SmuFamily.Raphael => SendMp1(0x3F, v),
            _ => SmuStatus.Failed,
        };
    }

    /// <summary>
    /// Clears any applied undervolt by writing a zero offset. Also happens
    /// automatically at reboot, since SMU settings are volatile.
    /// </summary>
    public void ResetCurveOptimizer()
    {
        if (!IsUsable) return;

        SetCurveOptimizerAll(0);
        SetCurveOptimizerGfx(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _module.Dispose();
        _pciMutex?.Dispose();
    }
}
