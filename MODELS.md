# Supported models

AcerHelper drives an interface that Acer ships across the Nitro and Predator
range, so most of those machines will work to some degree. But **capability sets
differ per model and per BIOS version** — the firmware advertises what it
supports, and this project gates on those bitmaps rather than assuming.

This file records what has actually been observed. Only the first entry has been
verified by the maintainer; everything else is a user report.

Adding yours is a five-minute job — see [Reporting a model](#reporting-a-model).

---

## Legend

| | |
|---|---|
| ✅ | Confirmed working on hardware |
| ❌ | Confirmed absent or rejected by the firmware |
| ⚠️ | Present but partial, or untested |
| — | Not reported |

"Absent" is a real result, not a gap. A model that reports no charge limit does
not have one, and no software will add it.

---

## Verified

### Acer Nitro V 15 — ANV15-41

Verified end to end by the maintainer.

| | |
|---|---|
| Board | `Sportage_RBH` |
| BIOS | V1.51 (2025-11-23) |
| CPU | AMD Ryzen 5 7535HS (family `0x19`, model `0x44`, Rembrandt) |
| GPU | NVIDIA RTX 3050 6 GB Laptop (Optimus, no MUX) |
| Battery | AP21D8M, 15.4 V / 3733 mAh / 57.5 Wh |
| OS | Windows 11 |

**Firmware fingerprint** — the three bitmaps that characterise a model:

| Value | Reading |
|---|---|
| Supported profiles (misc `0x0A`) | `0x53` — Quiet, Balanced, Performance, Eco. No Turbo. |
| Sensor bitmap (`GetGamingSysInfo 0x0000`) | `0x0227` — CPU temp, CPU fan, system temp, GPU fan, GPU temp |
| Battery `uFunctionList` | `0x03` — health mode and calibration only |
| SMU version | `0x04454200` |

| Feature | | Notes |
|---|---|---|
| Thermal profiles | ✅ | All four round-trip |
| Manual fan duty | ✅ | 40 % → 2767 rpm, 60 % → 3878 rpm |
| Fan mode `Custom` | ✅ | Fans can be stopped entirely (0 %) |
| CoolBoost | ✅ | `FanMode.Turbo` on both fans |
| Sensors | ✅ | GPU reports 0 °C while the dGPU is asleep |
| Battery 80 % cap | ✅ | |
| Battery calibration | ⚠️ | Firmware supports it; not exposed by the app |
| USB-C detection | ✅ | AC adapter event key `0x04` |
| Nitro key | ✅ | HID key, scan `0x75` extended, vk `0xFF` |
| GPU clock offsets | ✅ | Core ±1000 MHz, memory −1000…+3000 MHz |
| GPU power limit | ❌ | Vendor-locked |
| Keyboard backlight | ⚠️ | Single-zone; read works, write untested |
| Display overdrive | ❌ | `SetGamingProfile` is a no-op |
| Charger bypass | ❌ | Not in `uFunctionList` |
| CPU overclock (ACPI) | ❌ | `OC_1` absent, `OC_2` reads `0xFF` |
| RGB keyboard | ❌ | No RGB hardware on the R6NM variant |
| GPU MUX | ❌ | No MUX method in the interface |
| Fan table (methods 18/19) | ❌ | Getter rejected; ACPI is an SMI shim |

Undocumented misc-setting indices that respond: `0x01=0`, `0x02=1`, `0x06=1`
(boot animation/sound, per Linuwu-Sense), `0x08=1`, `0x09=1`.

WMI interfaces live in **`SSD8`**, not the DSDT: `AcerGamingFunction` → `WMBH`,
`BatteryControl` → `WMBE`, `APGeAction` → `WMAA`.

---

## Reported

No community reports yet. Yours would be the first.

<!--
Add a row here, newest last. Keep it to one line; put anything unusual in a
short subsection below the table.

| Model | BIOS | Profiles | Fans | Battery cap | USB-C | Reporter |
|---|---|---|---|---|---|---|
| AN515-58 | V1.20 | ✅ 0x53 | ✅ | ✅ | — | @someone (#12) |
-->

| Model | BIOS | Profiles | Fans | Battery cap | USB-C | Reporter |
|---|---|---|---|---|---|---|
| | | | | | | |

---

## Reporting a model

**You do not need to fork the project or write any code.** One read-only command
and a
[model support request](https://github.com/duncan077/A-helper/issues/new?template=model-support.yml)
is enough:

```bash
AcerHelper.Probe.exe --sweep --battery
```

The steps below add detail if you want to go further, but that first command on
its own is a useful report.

Everything here is read-only. Nothing below changes your machine.

**1. Capabilities and sensors**

```bash
AcerHelper.Probe.exe --sweep --battery > report.txt
```

**2. Hotkeys and charger type** — press the Nitro key, then swap chargers:

```bash
AcerHelper.Probe.exe --events
```

**3. GPU, if you have an NVIDIA one**

```bash
AcerHelper.Probe.exe --gpu
```

Then open an issue with the output, plus your exact model and BIOS version from
the top of the report. Raw values are far more useful than conclusions — `0x53`
tells the next person something; "profiles work" does not.

If a feature is **missing** on your model, say so explicitly. A confirmed
absence is as valuable as a confirmed capability, and it stops the next person
hunting for something that is not there.

### Writing tests, if you are willing

These change your machine but are reversible, and each verifies by reading back:

```bash
AcerHelper.Probe.exe --test-profile     # cycles profiles, restores the original
AcerHelper.Probe.exe --test-fans        # manual duty under a watchdog
AcerHelper.Probe.exe --health-on        # 80 % charge cap; --health-off reverts
```

`--test-fans` hands the fans to software under `AcerFanGuard`, which reverts to
Auto on a stale heartbeat, a temperature ceiling, or Ctrl+C. Watch it the first
time anyway.

### If the interface is missing entirely

If the probe reports that `AcerGamingFunction` was not found, your BIOS does not
expose it and there is nothing for this project to drive. That is still worth
reporting — it marks the boundary of the range.

See [PROBING.md](PROBING.md) for the full method, including how to identify
undocumented settings and extract the firmware's own ACPI tables.
