# AcerHelper

Native fan, thermal and battery control for Acer Nitro laptops on Windows,
without NitroSense.

Built because NitroSense is effectively abandoned and frequently broken, and
because the underlying firmware interface is far more capable than the vendor
software exposes.

**No Acer software was disassembled to build this.** The protocol was derived
from the Linux kernel driver `drivers/platform/x86/acer-wmi.c` (GPL-2.0) and
from the BIOS's own WMI class declarations, which Windows publishes through the
ACPI-WMI mapper. See [Provenance](#provenance).

---

## Status

Verified end to end on:

| | |
|---|---|
| Model | Acer Nitro V 15 **ANV15-41** (`Sportage_RBH`) |
| BIOS | V1.51 (2025-11-23) |
| CPU / GPU | AMD Ryzen APU + NVIDIA RTX 3050 6 GB Laptop (Optimus, no MUX) |
| Battery | AP21D8M, 15.4 V / 3733 mAh / 57.5 Wh |
| OS | Windows 11 |

Other Nitro and Predator models very likely work — the interface is shared —
but capability sets differ per model. Run the probe first.

### Feature matrix

| Feature | Status | Notes |
|---|---|---|
| Thermal profiles | ✅ verified | Quiet, Balanced, Performance, Eco |
| Manual fan duty | ✅ verified | Real RPM response; 40 % → 2767 rpm, 60 % → 3878 rpm |
| CoolBoost | ✅ verified | `FanMode.Turbo` on both fans |
| Live sensors | ✅ verified | CPU temp, system temp, both fan RPMs |
| Battery 80 % cap | ✅ verified | Separate `BatteryControl` device |
| Battery calibration | ⚠️ available | Deliberately gated — multi-hour discharge cycle |
| Keyboard backlight | ⚠️ partial | Single-zone; read works, write untested |
| Turbo profile | ❌ | Not offered by this firmware (Predator tier) |
| CPU overclocking | ❌ | `OC_1` absent, `OC_2` returns `0xFF` |
| RGB keyboard | ❌ | No RGB hardware on the R6NM variant |
| GPU MUX switching | ❌ | No MUX method exists in the interface at all |
| **Charger bypass** | ❌ | **Not implemented in firmware** — see below |

### Application features

- System tray icon with profile submenu, CoolBoost toggle and a live tooltip
  (current profile, CPU temperature, power source)
- Close-to-tray, so the poll loop and any engaged fan guard keep running
- **Automatic profile switching on AC / battery**, applied only on a power
  source *transition* — a manual profile choice is never overridden a second
  later
- **Fn / Nitro key handling** via the `APGeEvent` WMI event class — the Nitro key
  cycles thermal profiles
- **Screen refresh rate** switching at the current resolution (pure Win32, works
  regardless of the Acer interface)
- **Display overdrive** toggle — encoding unverified on ANV15-41, see below
- Settings persist to `acerhelper.conf` beside the executable
- Failures are logged to `acerhelper.log` with HRESULTs decoded to names

---

## Protocol

### `AcerGamingFunction` — `7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56`

Namespace `root\WMI`. Calling convention as declared by the BIOS:

- `Get*` — `UInt32 gmInput` in, `UInt64 gmOutput` out
- `Set*` — `UInt64 gmInput` in, `UInt32 gmOutput` out
- In both directions the **low byte of the result is a status code; 0 == success**

| ID | Method | Input encoding |
|----|--------|----------------|
| 5 | `GetGamingSysInfo` | `0x0000` supported sensors (bits 39:24) · `0x0001 \| id<<8` reading (bits 23:8) |
| 14 / 15 | `Set/GetGamingFanBehavior` | fan bitmap `15:0` (CPU = bit 0, GPU = bit 3) |
| 16 / 17 | `Set/GetGamingFanSpeed` | fan id `7:0` (CPU `0x01`, GPU `0x04`), percent `15:8` |
| 22 / 23 | `Set/GetGamingMiscSetting` | index `7:0`, value `15:8` |

Fan behaviour uses **different bit positions for get and set** — a detail worth
repeating because it is easy to get wrong:

| | CPU | GPU |
|---|---|---|
| set mode | bits 17:16 | bits 23:22 |
| get mode | bits 9:8 | bits 15:14 |

Fan modes: `1` Auto, `2` Turbo (CoolBoost), `3` Custom.
Thermal profiles: `0` Quiet, `1` Balanced, `4` Performance, `5` Turbo, `6` Eco.
Sensor IDs: `0x01` CPU temp, `0x02` CPU fan rpm, `0x03` system temp,
`0x06` GPU fan rpm, `0x0A` GPU temp.

On ANV15-41 the supported-profile bitmap (misc `0x0A`) reads **`0x53`** =
bits 0, 1, 4, 6 — Turbo (bit 5) is absent. Gate your UI on this bitmap rather
than on the enum.

### `BatteryControl` — `79772EC5-04B1-4BFD-843C-61E7F77B6CC9`

A **separate ACPI device**. The charge limit is not a gaming misc-setting index,
which is why polling `AcerGamingFunction` never observes it change.

| ID | Method |
|----|--------|
| 19 | `GetBattInfoInterface` |
| 20 | `GetBatteryHealthControlStatus` |
| 21 | `SetBatteryHealthControl` |
| 22 | `GetBatteryFunctionData` |
| 23 | `SetBatteryFunctionData` |

> **Windows differs from Linux here.** The Linux driver passes one packed struct
> per call. The Windows ACPI-WMI mapper *decomposes* that struct into
> individually named parameters (`uBatteryNo`, `uFunctionQuery`, `uReserved`, …).
> Passing a packed buffer to the first parameter fails with
> `WBEM_E_INVALID_PARAMETER`. Each field must be set by name.

`uFunctionList` from method 20 is a capability bitmap. On ANV15-41 it reads
**`0x03`** — health mode (80 % cap) and calibration, nothing else.

**There is no charger-bypass / AC-passthrough function in this firmware.** Bits
2–7 are unset. Every existing implementation reads only bits 0 and 1; this
project decodes all eight specifically so an undocumented mode would surface.
On this machine, none does.

### Findings not present in `acer-wmi.c`

Contributed here for anyone extending the upstream driver:

- **Methods 18/19** (`Set/GetGamingFanTable`) exist in the BIOS but the getter
  returns status `1` on ANV15-41 — the table interface is not usable, so fan
  curves must be implemented in software via Custom mode plus periodic duty
  writes.
- **Methods 24/25** (`Set/GetCPUOverclockingProfile`) are declared but reject
  input on this tier.
- **`BatteryControl` methods 22/23** are absent from `acer-wmi-battery` entirely.
  Parameter names (`uBACStartTime`, `uBACStopTime`, `uBACSwitch`) indicate a
  scheduled charging window. The correct `uReservedIn` width is still unknown —
  every mask 0–7 was rejected.
- **Misc indices `0x01`, `0x02`, `0x06`, `0x08`, `0x09`** all respond with
  status 0 and are undocumented upstream. Baseline on ANV15-41:
  `0x01=0`, `0x02=1`, `0x06=1`, `0x08=1`, `0x09=1`. Not the battery limit —
  that was ruled out by differential polling.
- **Battery info index 15** returns `3733`, matching the AP21D8M design capacity
  in mAh. Indices 8 (`3001`) and 9 (`17323`) are still unidentified.
- **CoolBoost is `FanMode.Turbo` written to both fans**, not a misc setting.
- **Misc index `0x06` is boot animation / sound** (per Linuwu-Sense), which
  identifies one of the previously unknown indices above.
- **Display overdrive is not a misc setting.** It goes through
  `Set/GetGamingProfile` (methods 1/3). Linuwu-Sense compares the whole returned
  word against `0x1000001000000` (on) and `0x1000000` (off); ANV15-41 returns
  **`0x00FF000001000000`**, matching neither — the `0xFF` in bits 55:48 looks
  like a capability mask rather than state. This project reads bit 48 alone and
  reports the raw word, because the encoding on this model is **unverified**.
  `AcerHelper.Probe.exe --overdrive-on` prints the XOR of before and after,
  which identifies the real state bit.
- **USB charging** is on the `APGeAction` class (`WMID_GUID3`), not the gaming
  class — per Linuwu-Sense, via its get/set function methods. Not implemented
  here yet.

### Event interface

`APGeEvent` (`676AA15E-6A47-4D9F-A2CC-1E6D18D14026`) delivers hotkeys. Function
IDs from `acer-wmi.c`: `0x01` hotkey, `0x04` backlight, `0x05` accelerometer or
keyboard dock, `0x07` gaming/Turbo key, `0x08` AC adapter.

Events are received with the **semi-synchronous** `ExecNotificationQuery` rather
than `ExecNotificationQueryAsync`. The async form requires *implementing*
`IWbemObjectSink` so WMI can call back into managed code — possible under AOT
via `[UnmanagedCallersOnly]` vtables, but a lot of surface for one event class.
The semi-synchronous enumerator blocks in `Next()` instead, so nothing calls
into us. That blocked thread is why the watcher owns a separate thread and
connection: blocking the shared dispatcher would stall sensor polling.

The BIOS declares the event's property names and they are documented nowhere, so
every property is read generically and logged. Run
`AcerHelper.Probe.exe --events` and press keys to see the real layout.

---

## Building

Requires the .NET 11 SDK and the MSVC toolchain (Native AOT needs `link.exe`).

```bash
dotnet publish src/AcerHelper.App -c Release -r win-x64
```

Ship `AcerHelper.App.exe` together with `av_libglesv2.dll`,
`libHarfBuzzSharp.dll` and `libSkiaSharp.dll` — Avalonia's renderer needs them.
Roughly 36 MB total. No .NET runtime and no VC++ redistributable required.

The console probe is useful on unknown models:

```bash
dotnet publish src/AcerHelper.Probe -c Release -r win-x64
```

| Flag | Effect |
|---|---|
| *(none)* | read-only capability and sensor dump |
| `--sweep` | read every misc-setting index |
| `--watch` | live differential poll — identifies what a hotkey or vendor app writes |
| `--battery` | battery health status, read-only |
| `--test-profile` | reversible profile round-trip |
| `--test-fans` | guarded manual fan test |
| `--health-on` / `--health-off` | toggle the 80 % charge cap |

All of it needs elevation.

---

## GPU and CPU tuning

### NVIDIA clock offsets

Implemented in `Nvidia/NvApi.cs` against `nvapi64.dll` directly.

**NvAPIWrapper.Net cannot be used under Native AOT.** It resolves every entry
point through `NvAPI_QueryInterface` + `Marshal.GetDelegateForFunctionPointer`,
and AOT cannot generate marshalling stubs for delegates it never sees
statically. Publishing warns `IL2104` / `IL3053`, and at runtime it throws
`NotSupportedException: 'NvAPI_Initialize' is missing delegate marshalling
data`. So entry points are resolved by ordinal and called through
`delegate* unmanaged[Cdecl]`.

Verified on an RTX 4080:

```
Graphics   offset 0 MHz   range -1000..1000 MHz   editable
Memory     offset 0 MHz   range -1000..3000 MHz   editable
```

Offsets apply to pstate 0 and are **not persisted by the driver** — a reboot
clears them, which makes them safe to experiment with. Ranges are read from the
driver, so an out-of-range value is refused rather than applied.

One gotcha: `NV_GPU_PERF_PSTATES20_INFO_V2` is **7416** bytes, not 7316 — V2
appends an over-voltage block (`numVoltages` plus four voltage entries) after
the pstate table. Getting that wrong returns
`NVAPI_INCOMPATIBLE_STRUCT_VERSION` (-9). The code tries V2 then falls back to
V1, since older drivers reject V2 outright rather than negotiating.

### AMD Curve Optimizer undervolting

`Amd/CpuInfo.cs` identifies the CPU via `X86Base.CpuId` (an intrinsic: no
driver, no elevation, AOT-safe) and maps family/model to an SMU codename.

`Amd/RyzenSmu.cs` performs the undervolt through **PawnIO**, a signed kernel
driver that runs sandboxed bytecode modules in ring 0. Mailbox addresses and
command IDs follow G-Helper's `app/Pawn/RyzenSmu.cs`, which credits RyzenAdj and
UXTU.

**Requires PawnIO installed** — <https://pawnio.eu>. The `RyzenSMU.bin` module is
LGPL-2.1 and ships with PawnIO, so it is located on disk rather than
redistributed here. Search paths are printed by `--cpu`.

Mailboxes and commands, by family:

| Family | MP1 cmd/rsp/arg | PSMU cmd/rsp/arg |
|---|---|---|
| Renoir | `3B10528` / `3B10564` / `3B10998` | `3B10A20` / `3B10A80` / `3B10A88` |
| Mobile *(Cezanne, Rembrandt, Phoenix, HawkPoint)* | `3B10528` / `3B10578` / `3B10998` | `3B10A20` / `3B10A80` / `3B10A88` |
| Raphael *(and Matisse/Vermeer)* | `3B10530` / `3B1057C` / `3B109C4` | `3B10524` / `3B10570` / `3B10A40` |

| Operation | Renoir | Mobile | Raphael |
|---|---|---|---|
| Curve Optimizer, all cores | MP1 `0x55` | MP1 `0x4C` | PSMU `0x07` |
| Curve Optimizer, iGPU | MP1 `0x64` | PSMU `0xB7` | — |
| Temperature limit | MP1 `0x19` | MP1 `0x19` | MP1 `0x3F` |

Offsets are 16-bit two's complement. PCI config access is serialised on the
conventional `Global\Access_PCI` mutex, as the RyzenSMU module requires.

**Safety.** The feature stays disabled unless a read-only probe succeeds first:
the module must load, `ioctl_get_code_name` must resolve, and
`ioctl_get_smu_version` must return non-zero. Families without a verified
command table report `Unsupported` rather than guessing. Offsets are clamped to
−50…+10, and the mailbox is checked for an unfinished prior transaction before
any write.

**The SMU accepting an offset does not mean it is stable.** Curve Optimizer
instability usually appears only under sustained load. SMU settings are volatile,
so a reboot — or the Reset button — clears them.

Note the asymmetry with ASUS: G-Helper gets PPT/SPL/SPPT from ASUS's own ACPI
interface and uses PawnIO only for undervolt and temperature limits. The
ANV15-41 has **no** ACPI power-limit interface — `OC_1` absent, `OC_2` = `0xFF` —
so on this machine every CPU knob goes through the SMU, with no fallback.

```bash
AcerHelper.Probe.exe --cpu          # identification and PawnIO discovery
AcerHelper.Probe.exe --smu          # validation probe, read-only
AcerHelper.Probe.exe --smu-co=-20   # apply an all-core offset
AcerHelper.Probe.exe --smu-reset    # clear it
```

---

## Implementation notes

Two problems cost real time; both are documented in the source.

**`System.Management` cannot be used under Native AOT.** It instantiates types
by reflection, so the trimmer removes them and you get
`MissingMethodException: No parameterless constructor defined for type
'System.Management.WbemDefPath'` at runtime. Publishing warns via `IL2104` /
`IL3053`, and no trim descriptor fixes it. `Interop/WbemAot.cs` replaces it with
raw `IWbem*` vtable calls through `delegate* unmanaged` function pointers and
`[LibraryImport]` — no reflection, no built-in COM marshalling.

**Raw COM pointers are apartment-bound.** Avalonia's UI thread is STA; thread
pool threads are MTA. Opening the channel on one and calling it from the other
fails with `RPC_E_WRONG_THREAD` — and it fails *after* a successful open, so the
window renders and every read silently returns nothing.
`AcerHardwareDispatcher` owns one dedicated MTA thread for the channel's entire
lifetime, disposal included.

One more, worth knowing when reading probe output: WMI has no VARIANT for
`uint64`, so it carries those as `BSTR` decimal strings. Passing `VT_I8` to a
`CIM_UINT64` parameter is rejected.

### Safety

`FanMode.Custom` stops the EC regulating the fans, and **the setting persists
after the controlling process exits**. A crash at 0 % duty leaves a machine with
unregulated fans. `AcerFanGuard` therefore clamps duty to a floor, and reverts
to Auto on a stale heartbeat, on a temperature ceiling, and on dispose, process
exit, Ctrl+C or an unhandled exception.

Battery calibration is supported by the firmware but is not exposed behind any
CLI flag — it is a multi-hour full discharge cycle and belongs behind an
explicit, informed user action.

---

## Contributing

Other Nitro and Predator models very likely work, but capability sets differ per
model and BIOS version. **[PROBING.md](PROBING.md)** walks through asking your
own machine what it supports — read-only first — and how to report findings so
they are useful to the next person.

If you are changing the hardware layer, read **[CLAUDE.md](CLAUDE.md)** first.
It documents the Native AOT and COM-apartment constraints, which cause runtime
failures that look like unrelated bugs.

## Provenance

Reverse engineering for interoperability is expressly permitted under EU
Software Directive 2009/24/EC Art. 6 and US DMCA § 1201(f). This project did not
need to rely on that: the interface was already public.

- Protocol semantics: [`acer-wmi.c`](https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/drivers/platform/x86/acer-wmi.c) (GPL-2.0)
- Battery interface: [frederik-h/acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery) (GPL-2.0)
- Method names, parameter names and types: the BIOS's own WMI class
  declarations, read from `root\WMI` on the target machine

No NitroSense or PredatorSense binary was disassembled, decompiled or examined.

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

## Related projects

- [Linuwu-Sense](https://github.com/0x7375646F/Linuwu-Sense) — Linux kernel module
- [Div-Acer-Manager](https://github.com/PXDiv/Div-Acer-Manager-Fan-Controls) — Linux GUI
- [AeroForge](https://github.com/noahcabral/aeroforge-nitrosense-alternative) — Windows, Tauri
- [G-Helper](https://github.com/seerge/g-helper) — the ASUS equivalent, and the inspiration
