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
/// Where a mailbox transaction actually failed.
///
/// Exists because a bare SmuStatus.Failed collapses four very different causes -
/// a rejected ioctl, a mailbox that never went idle, a mailbox that never
/// answered, and a genuine SMU error code - into one word that says nothing
/// about which. Diagnosing the Rembrandt MP1 path needed the distinction.
/// </summary>
public enum SmuFailure
{
    None,
    NotUsable,
    NoMailboxForFamily,
    MutexTimeout,

    /// <summary>The response register never became non-zero before we started.</summary>
    IdleTimeout,

    /// <summary>ioctl_write_smu_register was rejected by the driver.</summary>
    RegisterWriteRejected,

    /// <summary>ioctl_read_smu_register was rejected by the driver.</summary>
    RegisterReadRejected,

    /// <summary>Command written, but the SMU never produced a response.</summary>
    ResponseTimeout,

    /// <summary>The SMU answered with a non-OK status.</summary>
    SmuError,
}

public sealed record SmuResult(SmuStatus Status, SmuFailure Failure, string Detail)
{
    public bool IsOk => Failure == SmuFailure.None && Status == SmuStatus.Ok;

    public override string ToString() => IsOk
        ? "OK"
        : $"{Failure} ({Status}) - {Detail}";
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
    private SmuResult Send(uint cmdAddr, uint rspAddr, uint argAddr, uint command, uint argument)
    {
        if (_disposed)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NotUsable, "disposed");

        if (cmdAddr == 0)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NoMailboxForFamily,
                                 $"no mailbox defined for {Family}");

        var held = false;
        try
        {
            try { held = _pciMutex?.WaitOne(5000) ?? true; }
            catch (AbandonedMutexException) { held = true; }   // previous owner died

            if (!held)
                return new SmuResult(SmuStatus.CmdRejectedBusy, SmuFailure.MutexTimeout,
                                     "another process holds Global\\Access_PCI");

            // A response register still reading zero means either a previous
            // transaction never completed, or this is not a live mailbox.
            if (!WaitForIdle(rspAddr, out var idleValue, out var readOk))
            {
                return readOk
                    ? new SmuResult(SmuStatus.CmdRejectedBusy, SmuFailure.IdleTimeout,
                        $"rsp 0x{rspAddr:X7} stayed 0 - mailbox may be wrong for this model")
                    : new SmuResult(SmuStatus.Failed, SmuFailure.RegisterReadRejected,
                        $"driver rejected a read of 0x{rspAddr:X7}");
            }

            if (!WriteRegister(rspAddr, 0))
                return new SmuResult(SmuStatus.Failed, SmuFailure.RegisterWriteRejected,
                                     $"driver rejected a write to rsp 0x{rspAddr:X7}");

            for (var i = 0u; i < 6; i++)
                if (!WriteRegister(argAddr + (i * 4), i == 0 ? argument : 0))
                    return new SmuResult(SmuStatus.Failed, SmuFailure.RegisterWriteRejected,
                                         $"driver rejected a write to arg 0x{argAddr + (i * 4):X7}");

            if (!WriteRegister(cmdAddr, command))
                return new SmuResult(SmuStatus.Failed, SmuFailure.RegisterWriteRejected,
                                     $"driver rejected a write to cmd 0x{cmdAddr:X7}");

            var deadline = Environment.TickCount64 + MailboxTimeoutMs;
            var spins = 0;
            uint status = 0;

            while (Environment.TickCount64 < deadline)
            {
                if (!ReadRegister(rspAddr, out status))
                    return new SmuResult(SmuStatus.Failed, SmuFailure.RegisterReadRejected,
                                         $"driver rejected a read of 0x{rspAddr:X7} while polling");

                if (status != 0) break;

                spins++;
                if (spins > 256) Thread.Sleep(1);
                else if (spins > 32) Thread.Yield();
            }

            if (status == 0)
                return new SmuResult(SmuStatus.Failed, SmuFailure.ResponseTimeout,
                    $"cmd 0x{command:X2} written to 0x{cmdAddr:X7} (idle was 0x{idleValue:X8}), "
                    + $"no response within {MailboxTimeoutMs} ms");

            return status == (uint)SmuStatus.Ok
                ? new SmuResult(SmuStatus.Ok, SmuFailure.None, "accepted")
                : new SmuResult((SmuStatus)status, SmuFailure.SmuError,
                                $"SMU returned 0x{status:X2} for cmd 0x{command:X2}");
        }
        finally
        {
            if (held) { try { _pciMutex?.ReleaseMutex(); } catch { /* not owned */ } }
        }
    }

    private bool WaitForIdle(uint rspAddr, out uint lastValue, out bool readSucceeded)
    {
        lastValue = 0;
        readSucceeded = true;

        var deadline = Environment.TickCount64 + MailboxTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadRegister(rspAddr, out lastValue))
            {
                readSucceeded = false;
                return false;
            }

            if (lastValue != 0) return true;
            Thread.Yield();
        }

        return false;
    }

    private SmuResult SendMp1(uint command, uint argument)
    {
        var (c, r, a) = Mp1();
        return Send(c, r, a, command, argument);
    }

    private SmuResult SendPsmu(uint command, uint argument)
    {
        var (c, r, a) = Psmu();
        return Send(c, r, a, command, argument);
    }

    /// <summary>
    /// Raw values of both mailboxes' registers, for diagnosing which one is live
    /// on an untested model. Pure reads.
    /// </summary>
    public IReadOnlyList<(string Mailbox, string Register, uint Address, uint? Value)> ReadMailboxRegisters()
    {
        var result = new List<(string, string, uint, uint?)>();

        foreach (var (name, addrs) in new[] { ("MP1", Mp1()), ("PSMU", Psmu()) })
        {
            foreach (var (reg, addr) in new[]
                     {
                         ("cmd", addrs.Cmd), ("rsp", addrs.Rsp), ("arg", addrs.Arg),
                     })
            {
                if (addr == 0) { result.Add((name, reg, 0, null)); continue; }
                result.Add((name, reg, addr, ReadRegister(addr, out var v) ? v : null));
            }
        }

        return result;
    }

    // ------------------------------------------------------ public operations

    /// <summary>Widest offset this code will program, in Curve Optimizer counts.</summary>
    public const int MinCurveOffset = -50;
    public const int MaxCurveOffset = 10;

    /// <summary>
    /// Curve Optimizer offsets use a 20-bit form: a negative offset is encoded
    /// as 0x100000 minus its magnitude.
    /// </summary>
    /// <remarks>
    /// NOT 16-bit two's complement. Encoding -10 as 0xFFF6 instead of 0xFFFF6
    /// made a Rembrandt SMU answer 0xFF (Failed) rather than 0xFE (UnknownCmd) -
    /// the command was recognised, the operand was not valid. Matches RyzenAdj's
    /// documented "use 0x100000-N for negative values".
    /// </remarks>
    private static uint EncodeCurve(int steps)
        => steps < 0 ? (uint)(0x100000 - -steps) : (uint)steps;

    /// <summary>
    /// Applies an all-core Curve Optimizer offset. Negative undervolts.
    /// </summary>
    /// <remarks>
    /// Instability from an aggressive value typically appears only under load,
    /// not immediately. Callers should treat a successful return as "accepted",
    /// not as "stable".
    /// </remarks>
    public SmuResult SetCurveOptimizerAll(int offset)
    {
        if (!IsUsable)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NotUsable, "SMU not validated");

        offset = Math.Clamp(offset, MinCurveOffset, MaxCurveOffset);
        var v = EncodeCurve(offset);

        return Family switch
        {
            SmuFamily.Renoir => SendMp1(0x55, v),

            // RyzenAdj/UXTU document MP1 0x4C here, but the RyzenSMU module's own
            // table routes Rembrandt-class parts to the PSMU mailbox. Try MP1
            // first and fall back rather than assuming either is universal.
            SmuFamily.Mobile => FirstWorking(() => SendMp1(0x4C, v), () => SendPsmu(0x4C, v)),

            SmuFamily.Raphael => SendPsmu(0x07, v),
            _ => new SmuResult(SmuStatus.Failed, SmuFailure.NoMailboxForFamily, $"{Family}"),
        };
    }

    /// <summary>Applies a Curve Optimizer offset to the integrated GPU.</summary>
    public SmuResult SetCurveOptimizerGfx(int offset)
    {
        if (!IsUsable)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NotUsable, "SMU not validated");

        offset = Math.Clamp(offset, MinCurveOffset, MaxCurveOffset);
        var v = EncodeCurve(offset);

        return Family switch
        {
            SmuFamily.Renoir => SendMp1(0x64, v),
            SmuFamily.Mobile => SendPsmu(0xB7, v),
            _ => new SmuResult(SmuStatus.Failed, SmuFailure.NoMailboxForFamily, $"{Family}"),
        };
    }

    /// <summary>
    /// Sets the sustained power limit (STAPM) in watts.
    /// </summary>
    /// <remarks>
    /// On ASUS machines G-Helper takes this route through ASUS ACPI instead.
    /// ANV15-41 has no ACPI power-limit interface at all - OC_1 is absent and
    /// OC_2 reads 0xFF - so the SMU is the only path here, with no fallback.
    /// </remarks>
    public SmuResult SetSustainedPowerLimit(int watts) => SetPowerLimit(watts, 0x1A, 0x14, 0x4F);

    /// <summary>Sets the fast package power limit (PPT fast) in watts.</summary>
    public SmuResult SetFastPowerLimit(int watts) => SetPowerLimit(watts, 0x1B, 0x15, 0x3E);

    /// <summary>Sets the slow package power limit (PPT slow) in watts.</summary>
    public SmuResult SetSlowPowerLimit(int watts) => SetPowerLimit(watts, 0x1C, 0x16, 0x5F);

    /// <summary>Lowest and highest wattage this code will program.</summary>
    public const int MinPowerLimitWatts = 5;
    public const int MaxPowerLimitWatts = 120;

    /// <summary>
    /// Power limits share one shape: milliwatts to an MP1 command that differs
    /// per family. Renoir additionally mirrors to PSMU, which this does not do -
    /// no Renoir hardware has been available to verify against.
    /// </summary>
    private SmuResult SetPowerLimit(int watts, uint renoirCmd, uint mobileCmd, uint raphaelCmd)
    {
        if (!IsUsable)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NotUsable, "SMU not validated");

        var milliwatts = (uint)Math.Clamp(watts, MinPowerLimitWatts, MaxPowerLimitWatts) * 1000;

        return Family switch
        {
            SmuFamily.Renoir => SendMp1(renoirCmd, milliwatts),
            SmuFamily.Mobile => SendMp1(mobileCmd, milliwatts),
            SmuFamily.Raphael => SendMp1(raphaelCmd, milliwatts),
            _ => new SmuResult(SmuStatus.Failed, SmuFailure.NoMailboxForFamily, $"{Family}"),
        };
    }

    /// <summary>Sets the CPU temperature limit in degrees Celsius.</summary>
    public SmuResult SetTemperatureLimit(int celsius)
    {
        if (!IsUsable)
            return new SmuResult(SmuStatus.Failed, SmuFailure.NotUsable, "SMU not validated");

        var v = (uint)Math.Clamp(celsius, 60, 100);

        return Family switch
        {
            SmuFamily.Renoir or SmuFamily.Mobile => SendMp1(0x19, v),
            SmuFamily.Raphael => SendMp1(0x3F, v),
            _ => new SmuResult(SmuStatus.Failed, SmuFailure.NoMailboxForFamily, $"{Family}"),
        };
    }

    /// <summary>
    /// Runs alternatives until one is accepted. Only a mailbox that never
    /// answered is worth retrying elsewhere - an explicit SMU error means the
    /// mailbox works and the command was genuinely refused.
    /// </summary>
    private static SmuResult FirstWorking(params Func<SmuResult>[] attempts)
    {
        SmuResult last = new(SmuStatus.Failed, SmuFailure.NotUsable, "no attempts");

        foreach (var attempt in attempts)
        {
            last = attempt();
            if (last.IsOk) return last;
            if (last.Failure is SmuFailure.SmuError or SmuFailure.RegisterWriteRejected) return last;
        }

        return last;
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
