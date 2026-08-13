// SPDX-License-Identifier: GPL-3.0-or-later
//
// CPU identification for undervolting feasibility.
//
// Reads CPUID directly through X86Base.CpuId - a JIT/AOT intrinsic, so no
// driver, no elevation and no reflection. This establishes WHETHER SMU
// undervolting is possible on a machine before anything touches ring 0.
//
// Actually applying an undervolt needs SMU mailbox access, which needs a kernel
// driver (PawnIO). Nothing in this file writes anything.

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Text;

namespace AcerHelper.Hardware.Amd;

/// <summary>AMD SMU generation, which determines the mailbox addresses.</summary>
public enum RyzenCodename
{
    Unknown,
    Raven,          // Ryzen 2000/3000 APU
    Picasso,
    Renoir,         // 4000 APU
    Cezanne,        // 5000 APU
    Rembrandt,      // 6000 / 7035 APU  (Zen 3+)
    Phoenix,        // 7040 / 8040 APU  (Zen 4)
    HawkPoint,
    StrixPoint,     // Ryzen AI 300
    Matisse,        // 3000 desktop
    Vermeer,        // 5000 desktop
    Raphael,        // 7000 desktop
    Granite,        // 9000 desktop
}

public sealed record CpuIdentity(
    string Vendor,
    string BrandString,
    int Family,
    int Model,
    int Stepping,
    RyzenCodename Codename)
{
    public bool IsAmd => Vendor == "AuthenticAMD";

    /// <summary>
    /// Whether Curve Optimizer style undervolting is plausible on this part.
    /// Plausible is not the same as verified - it still needs a working SMU
    /// mailbox, which cannot be confirmed without ring 0 access.
    /// </summary>
    public bool SupportsCurveOptimizer => Codename is
        RyzenCodename.Cezanne or RyzenCodename.Rembrandt or RyzenCodename.Phoenix or
        RyzenCodename.HawkPoint or RyzenCodename.StrixPoint or
        RyzenCodename.Vermeer or RyzenCodename.Raphael or RyzenCodename.Granite;
}

[SupportedOSPlatform("windows")]
public static class CpuInfo
{
    public static CpuIdentity Identify()
    {
        if (!X86Base.IsSupported)
            return new CpuIdentity("unknown", "unknown", 0, 0, 0, RyzenCodename.Unknown);

        // Leaf 0: vendor string in EBX, EDX, ECX order.
        var (_, ebx0, ecx0, edx0) = X86Base.CpuId(0, 0);
        var vendor = string.Concat(Word(ebx0), Word(edx0), Word(ecx0));

        // Leaf 1: family / model / stepping.
        var (eax1, _, _, _) = X86Base.CpuId(1, 0);

        var stepping = eax1 & 0xF;
        var baseModel = (eax1 >> 4) & 0xF;
        var baseFamily = (eax1 >> 8) & 0xF;
        var extModel = (eax1 >> 16) & 0xF;
        var extFamily = (eax1 >> 20) & 0xFF;

        var family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
        var model = baseFamily is 0xF or 0x6 ? (extModel << 4) | baseModel : baseModel;

        return new CpuIdentity(
            Vendor: vendor,
            BrandString: ReadBrandString(),
            Family: family,
            Model: model,
            Stepping: stepping,
            Codename: MapCodename(vendor, family, model));
    }

    private static string Word(int reg)
    {
        Span<byte> bytes = stackalloc byte[4];
        MemoryMarshal.Write(bytes, in reg);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string ReadBrandString()
    {
        // Leaves 0x80000002..0x80000004 hold the 48-character brand string.
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if ((uint)maxExt < 0x80000004) return "unknown";

        var sb = new StringBuilder(48);
        for (var leaf = 0x80000002; leaf <= 0x80000004; leaf++)
        {
            var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
            sb.Append(Word(a)).Append(Word(b)).Append(Word(c)).Append(Word(d));
        }

        return sb.ToString().Trim('\0', ' ');
    }

    /// <summary>
    /// Maps family/model onto an SMU generation. Model numbers are from the
    /// AMD PPRs and match the tables used by Ryzen SMU tooling.
    /// </summary>
    private static RyzenCodename MapCodename(string vendor, int family, int model)
    {
        if (vendor != "AuthenticAMD") return RyzenCodename.Unknown;

        return (family, model) switch
        {
            (0x17, 0x11) => RyzenCodename.Raven,
            (0x17, 0x18) => RyzenCodename.Picasso,
            (0x17, 0x60) => RyzenCodename.Renoir,
            (0x17, 0x68) => RyzenCodename.Renoir,          // Lucienne
            (0x17, 0x71) => RyzenCodename.Matisse,
            (0x19, 0x21) => RyzenCodename.Vermeer,
            (0x19, 0x50) => RyzenCodename.Cezanne,
            (0x19, 0x44) => RyzenCodename.Rembrandt,       // 6000 series and 7035 refresh
            (0x19, 0x61) => RyzenCodename.Raphael,
            (0x19, 0x74) => RyzenCodename.Phoenix,
            (0x19, 0x75) => RyzenCodename.Phoenix,
            (0x19, 0x78) => RyzenCodename.HawkPoint,
            (0x1A, 0x24) => RyzenCodename.StrixPoint,
            (0x1A, 0x44) => RyzenCodename.Granite,
            _ => RyzenCodename.Unknown,
        };
    }

    /// <summary>
    /// Whether PawnIO is present. SMU access needs it; without it no undervolt
    /// can be applied regardless of what the CPU supports.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="PawnIoLocator"/>. An earlier version looked only
    /// in System32, which is not where PawnIO installs, so it reported false on
    /// machines that had it - directly contradicting the locator's own result.
    /// </remarks>
    public static bool IsPawnIoInstalled() => PawnIoLocator.InstallDirectory is not null;
}
