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
