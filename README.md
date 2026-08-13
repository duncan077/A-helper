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
but capability sets differ per model and per BIOS. Run the probe first, and see
**[MODELS.md](MODELS.md)** for what has actually been observed on which machines.

### Feature matrix

Everything marked ✅ was confirmed on real hardware, not merely compiled.

| Feature | Status | Notes |
|---|---|---|
| Thermal profiles | ✅ | Quiet, Balanced, Performance, Eco — round-tripped |
| Manual fan duty | ✅ | Real RPM response: 40 % → 2767 rpm, 60 % → 3878 rpm |
| Fan curves, per profile | ✅ | Independent CPU and GPU curves, graphical editor |
| CoolBoost | ✅ | `FanMode.Turbo` on both fans |
| Live sensors | ✅ | CPU temp, system temp, both fan RPMs, GPU temp |
| Battery 80 % cap | ✅ | Separate `BatteryControl` device |
| USB-C vs barrel charger | ✅ | From the AC adapter event's key number |
| Nitro key | ✅ | Scan `0x75` extended — a HID key, not a WMI event |
| GPU clock offsets | ✅ | RTX 3050: core ±1000 MHz, memory −1000…+3000 MHz |
| Windows power mode / plan | ✅ | Overlay, plan, max processor state, boost |
| Screen refresh rate | ✅ | Pure Win32, independent of the Acer interface |
| Start with Windows | ✅ | Scheduled task, elevated, no UAC prompt |
| Battery calibration | ⚠️ | Firmware supports it; deliberately not exposed |
| Keyboard backlight | ⚠️ | Single-zone; read works, write untested |
| AMD Curve Optimizer | ⚠️ | Implemented and mailbox-validated, but **not used by the app** |
| GPU power limit | ❌ | Vendor-locked on this laptop; reported as locked |
| Turbo profile | ❌ | Not offered by this firmware (Predator tier) |
| CPU overclocking (ACPI) | ❌ | `OC_1` absent, `OC_2` returns `0xFF` |
| RGB keyboard | ❌ | No RGB hardware on the R6NM variant |
| GPU MUX switching | ❌ | No MUX method exists in the interface at all |
| Display overdrive | ❌ | `SetGamingProfile` is a **no-op** on this model |
| Charger bypass | ❌ | `uFunctionList = 0x03` — not implemented in firmware |

### Application features

- **System tray** with profile submenu, CoolBoost toggle and a live tooltip
  (profile, CPU temperature, power source). Close-to-tray keeps the poll loop
  and any engaged fan guard running.
- **Graphical fan curve editor**, six bands from 40 to 90 °C, separate CPU and
  GPU curves stored per thermal profile. 0 % stops the fan, as the EC itself
  does at idle.
- **Automatic profile switching** on AC / battery and on USB-C, applied only on
  a *transition* so a manual choice is never overridden a second later.
- **Per-profile settings** — Windows power plan, maximum processor state, boost
  mode and fan curves, with separate power plans while on a USB-C charger.
- **Windows power management** — power mode overlay, power plan, turbo boost
  toggle, maximum processor state. No kernel driver, and it survives a reboot.
- **Nitro key** bound through a low-level keyboard hook, since on this model it
  is an ordinary HID key rather than a WMI event.
- **Start with Windows** via a scheduled task with highest privileges, launching
  straight to the tray.
- Settings persist to `acerhelper.conf`; failures land in `acerhelper.log` with
  HRESULTs decoded to names.

### Configuration

`acerhelper.conf` sits beside the executable and is written with a commented
example. Per-profile keys are keyed by **name**, because Acer's profile values
(`Quiet=0, Balanced=1, Performance=4, Eco=6`) do not match the numbering other
tools use:

```ini
AutoSwitchEnabled=True
AcProfile=Performance
BatteryProfile=Eco
usbc_profile=Eco

scheme_Performance=8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
scheme_usbc_Performance=381b4222-f694-41f0-9685-ff5bb260df2e
maxproc_Quiet=70
boost_Quiet=0
fancurve_Performance=40:30,50:40,60:55,70:75,80:90,90:100
fancurve_gpu_Performance=40:30,50:40,60:60,70:80,80:95,90:100
```

Well-known Windows plan GUIDs: Balanced `381b4222-f694-41f0-9685-ff5bb260df2e`,
High Performance `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`, Ultimate Performance
`e9a42b02-d5df-448d-aa00-03f14749eb61`.

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
  writes. **The ACPI implementation cannot explain the format** — see below.

### Why the fan table format cannot be recovered from firmware

`AcerHelper.Probe.exe --dump-acpi` extracts the ACPI tables; on ANV15-41 the
gaming interface lives in `SSD8`, not the DSDT, and `_WDG` maps the GUIDs to:

| Interface | Control method |
|---|---|
| `AcerGamingFunction` | `WMBH` |
| `BatteryControl` | `WMBE` |
| `APGeAction` | `WMAA` |
| `APGeEvent` | notify `0xBC` on `WMID` |

`WMBH` dispatches on the WMI method id, and every case is a two-line shim:

```asl
Case (0x12)                    // method 18, SetGamingFanTable
{
    WSMI (Arg1, Arg2)
    BHSK = WMIB
    Return (BHSK)
}

Method (WSMI, 2, NotSerialized)
{
    MTID = Arg0                // method id
    WMIB = Arg1                // argument buffer
    WSSP = 0xD0                // raise SMI 0xD0
}
```

ACPI stores the method id and the caller's buffer in shared memory and raises
**SMI 0xD0**. Everything meaningful happens in the SMM handler, which lives in
SMRAM — locked at boot and unreadable from the OS. So the firmware tables reveal
the *transport* and nothing about the fan table *payload*, and no amount of ACPI
decompilation will.

Two observations suggest the table is not the mechanism anyway: `GetGamingFanTable`
is rejected by that SMM handler, and `--watch` shows the CPU fan mode moving
`Auto -> Custom` while NitroSense applies a curve — the same Custom-mode-plus-duty
route this project and every other open-source implementation uses.

### `_WDG` corroboration of the event layout

The DSDT's own event constructors confirm the `APGeEvent` payload decoded here
empirically:

```asl
Method (HKEV, 2) { WMID.FEBC[0] = Arg0; WMID.FEBC[1] = Arg1; Notify (WMID, 0xBC) }
Method (HMEV, 1) { WMID.FEBC[2] = (Local0 & 0xFF); WMID.FEBC[3] = (Local1 & 0xFF) }
```

Byte 0 function, byte 1 key number, bytes 2–3 little-endian `device_state` —
exactly as observed, now confirmed against firmware.
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
- **Display overdrive is not a misc setting** — it goes through
  `Set/GetGamingProfile` (methods 1/3). Linuwu-Sense compares the whole returned
  word against `0x1000001000000` (on) and `0x1000000` (off). ANV15-41 returns
  **`0x00FF000001000000`**, matching neither, and **a write is a no-op**: after
  `SetGamingProfile(0x1000000000010)` the value is bit-for-bit unchanged.
  So overdrive is **not reachable this way on this model**, the `0xFF` in bits
  55:48 is a capability mask, and `GetLcdOverdrive()` returns null (unsupported)
  for anything that is not an exact match.
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

The payload layout is confirmed on ANV15-41. The BIOS exposes the packed struct
as an 8-byte array named `EventDetail`:

```
[0] function   [1] key_num   [2..3] device_state (LE)   [4..7] reserved
01 84 08 00 .. = Hotkey, KeyboardBacklightToggle, state 0x0008
```

Findings that contradict or extend `acer-wmi.c`:

- **Function `0x07` is never emitted.** Nothing on ANV15-41 produces the
  documented gaming/Turbo key event. The Nitro key is not a WMI event at all —
  it is an ordinary HID key reporting **scan `0x75` extended with virtual key
  `0xFF`**, meaning Windows has no virtual key for it, so the scan code is the
  only reliable field to match. It is bound through a low-level keyboard hook,
  configurable via `NitroKeyScanCode` / `NitroKeyExtended`, and discoverable
  with `AcerHelper.Probe.exe --learn-nitro`, which watches both WMI and the
  keyboard.
- **The AC adapter event's key number identifies the charger type**, which
  `acer-wmi.c` never decodes: `0x00` unplugged, `0x01` barrel adapter,
  **`0x04` USB-C PD charger**. This is what drives USB-C profile switching, and
  it needs no configuration.
- **Function `0x02`** is undocumented. Observed with `key_num = 1`.
- **Function `0x09`** is undocumented and fires immediately after `0x08`
  (AC adapter) with the same key number — `0` unplugged, `1` plugged — so it
  appears to mirror AC state.
- `device_state` genuinely varies: `0x0008` for the keyboard-backlight key,
  `0x0000` then `0x0001` across two touchpad-toggle presses.

Observed hotkeys: `0x62` BrightnessUp, `0x61` SwitchVideoMode, `0x84`
KeyboardBacklightToggle, `0x82` TouchpadToggle.

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

### GPU power limit

`ClientPowerPolicies{GetInfo,GetStatus,SetStatus}`. Limits are thousandths of a
percent, and within an entry the three sit **12 bytes apart** — `pstate@0`,
`min@12`, `def@24`, `max@36`. Verified on an RTX 4080: 100 % current, 46…140 %
range, 100 % default.

**Laptop boards commonly lock it.** When min, default and max are identical the
GPU reports as locked and no slider is offered, which is the case on the
ANV15-41's RTX 3050.

### AMD Curve Optimizer undervolting — implemented, not used

> The app does **not** drive this. It requires a kernel driver, the GPU power
> target is vendor-locked, and the CPU has no ACPI power interface, so it bought
> nothing on this hardware — Windows power management replaced it. The code and
> the probe's `--smu` flags remain because the findings are worth keeping for
> models where the trade is better.

`Amd/CpuInfo.cs` identifies the CPU via `X86Base.CpuId` (an intrinsic: no
driver, no elevation, AOT-safe) and maps family/model to an SMU codename.

`Amd/RyzenSmu.cs` performs the undervolt through **PawnIO**, a signed kernel
driver that runs sandboxed bytecode modules in ring 0. Mailbox addresses and
command IDs follow G-Helper's `app/Pawn/RyzenSmu.cs`, which credits RyzenAdj and
UXTU.

**Verified working** on a Ryzen 5 7535HS (family `0x19`, model `0x44` →
Rembrandt): the validation probe passes and reports SMU version `0x04454200`.

**Requires PawnIO installed** — <https://pawnio.eu>. It installs to
`C:\Program Files\PawnIO`, which is *not* on any default DLL search path, so
`PawnIoLocator` finds it via the registry and installs a `DllImportResolver`.

**`RyzenSMU.bin` is a separate download** from
[PawnIO_Modules releases](https://github.com/namazso/PawnIO_Modules/releases) —
the PawnIO installer does not appear to ship it. It is LGPL-2.1 and signed, so
it is located on disk rather than redistributed here; putting it beside the
executable works. `--cpu` prints every path searched.

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

Four traps cost real time; all are documented in the source.

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

**`NvAPIWrapper.Net` cannot be used under Native AOT either** — same failure
class. It resolves every entry point through `NvAPI_QueryInterface` and
`Marshal.GetDelegateForFunctionPointer`, and AOT cannot generate marshalling
stubs for delegates it never sees statically. It throws
`NotSupportedException: 'NvAPI_Initialize' is missing delegate marshalling data`.
`Nvidia/NvApi.cs` resolves ordinals and calls through
`delegate* unmanaged[Cdecl]` instead.

**PawnIO is not on any DLL search path.** `PawnIOLib.dll` installs to
`C:\Program Files\PawnIO`, so a plain `[LibraryImport("PawnIOLib")]` throws
`DllNotFoundException` even where PawnIO is correctly installed.
`PawnIoLocator` finds it via the registry and registers a `DllImportResolver`
from a static constructor — not a `[ModuleInitializer]`, which `CA2255` rightly
flags for a library.

One more, worth knowing when reading probe output: WMI has no VARIANT for
`uint64`, so it carries those as `BSTR` decimal strings. Passing `VT_I8` to a
`CIM_UINT64` parameter is rejected.

### Windows power management

`WindowsPower.cs` wraps `powrprof` for the power-mode overlay (the Windows 11
slider), power plans, maximum processor state (`PROCTHROTTLEMAX`) and boost mode
(`PERFBOOSTMODE`). A written processor value is **inert until the plan is
re-activated**, so every setter re-applies the active plan rather than trusting
the write. These settings are per-plan, so switching plans re-reads them.

### Start with Windows

A scheduled task, not a `Run` registry key. The manifest requests
`requireAdministrator`, and Windows will not silently elevate a `Run` entry — it
prompts on every logon. `schtasks /RL HIGHEST` starts elevated without
prompting, and the task passes `--minimised` so a boot launch goes to the tray.
Task existence is the source of truth, so deleting it in Task Scheduler is
reflected in the UI.

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
own machine what it supports — read-only first — and
**[MODELS.md](MODELS.md)** records what has been confirmed so far. Adding your
machine is a five-minute job, and a confirmed *absence* is as useful as a
confirmed capability.

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
