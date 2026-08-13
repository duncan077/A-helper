# Probing your notebook

How to find out what your Acer supports, and how to contribute that back.

Everything here works on Nitro and Predator models generally — the WMI interface
is shared across the range — but **capability sets differ per model and per BIOS
version**. Nothing is assumed; the firmware is asked directly.

Every step below is read-only unless explicitly marked otherwise.

---

## Before you start

- Run everything **as Administrator**. WMI method invocation fails with
  `E_ACCESSDENIED` otherwise.
- Note your exact model and BIOS version — findings are meaningless without them:

```bash
powershell -c "Get-CimInstance Win32_ComputerSystem | Select-Object Model; Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion"
```

---

## Step 1 — Does your machine expose the interface?

```bash
AcerHelper.Probe.exe
```

Read-only. It resolves the WMI class, then reports which capabilities actually
answer.

**If it says the gaming interface was not found**, your BIOS does not expose
`AcerGamingFunction` and there is nothing for this project to drive. Confirm
with:

```bash
powershell -ExecutionPolicy Bypass -File tools\Probe-AcerGamingWmi.ps1
```

That lists every Acer WMI GUID and, for the gaming class, every method with its
BIOS-declared parameter types.

### Reading the output

| Line | Meaning |
|---|---|
| `Supported profiles : ...` | Read from the firmware's own bitmap, not assumed |
| `status=0` | Success. Any other value is a firmware rejection |
| `GPU temperature : 0 C (dGPU appears powered down)` | Normal under Optimus — not a fault |
| `CPU manual duty : 40%` | The duty *register*, not current behaviour. It keeps its last value even in Auto mode |

A missing capability is a real answer. If `GetGamingFanSpeed` returns a non-zero
status, your model does not support manual duty, and no software can add it.

---

## Step 2 — Sweep the settings indices

```bash
AcerHelper.Probe.exe --sweep
```

Reports every misc-setting index that responds. Documented indices are `0x05`
and `0x07` (overclocking), `0x0A` (supported profiles) and `0x0B` (thermal
profile). **Anything else your machine answers is undocumented** and worth
reporting.

Reference baseline, ANV15-41 / BIOS V1.51:

```
0x01=0  0x02=1  0x06=1  0x07=255  0x08=1  0x09=1  0x0A=83  0x0B=1
```

---

## Step 3 — Identify what the unknown indices do

This is the technique that avoids disassembling anything.

```bash
AcerHelper.Probe.exe --watch
```

It polls every readable index plus the thermal profile and both fan modes, and
prints only what changes. Leave it running, then **change exactly one thing** —
in NitroSense, in BIOS setup, or by pressing a hotkey. Whatever moves is that
feature:

```
[01:53:57] CPU fan mode Auto -> Turbo
[01:53:57] GPU fan mode Auto -> Turbo
```

That output is how CoolBoost was identified: it is `FanMode.Turbo` written to
both fans, not a settings index at all.

One change at a time is what makes this work. Two at once tells you nothing.

**If nothing moves, the feature is not on this device.** That is a real result —
it is how the battery charge limit was traced to a separate WMI class. Do not
keep polling a device that does not own the setting.

---

## Step 4 — Battery

```bash
AcerHelper.Probe.exe --battery
```

Read-only. Reports `uFunctionList`, the firmware's capability bitmap, decoded
bit by bit.

- bit 0 — health mode, the ~80 % charge cap
- bit 1 — calibration
- bits 2–7 — **undocumented.** Every other implementation ignores these

If your machine sets any bit above 1, please report it. On ANV15-41 the value is
`0x03`, meaning this firmware has no charge-bypass or AC-passthrough function.

For the full class listing including the scheduled-charging methods that no
Linux driver implements:

```bash
powershell -ExecutionPolicy Bypass -File tools\Probe-AcerBattery.ps1
```

---

## Step 5 — Look for classes nobody knows about

```bash
powershell -ExecutionPolicy Bypass -File tools\Dump-AllAcpiWmi.ps1
```

Enumerates every BIOS-provided WMI class, not only the ones the Linux driver
knows. This is how `BatteryControl` was found.

Expect a large file — the `guid` qualifier appears on every ETW provider in
Windows, so most entries are noise. Look for class names that are
Acer-specific, or that match battery / charge / power / thermal.

---

## Step 6 — everything else

These need no Acer hardware and no elevation, so they work anywhere:

```bash
AcerHelper.Probe.exe --gpu          # NVIDIA clock offsets and power limit
AcerHelper.Probe.exe --cpu          # CPU identification and PawnIO discovery
```

`--gpu` reports whether each clock domain is `editable` or `LOCKED`, and whether
the power limit is `adjustable` or locked. Laptop boards frequently lock the
power target — that is a real answer, not a failure.

```bash
AcerHelper.Probe.exe --events       # live APGeEvent stream
AcerHelper.Probe.exe --learn-nitro  # identify the profile-cycle key
```

`--learn-nitro` watches **both** WMI events and the raw keyboard. On ANV15-41 the
Nitro key is not a WMI event at all — it is an ordinary HID key reporting scan
`0x75` extended — so if nothing appears on the WMI side, look at the keystroke
line instead. It prints the config lines to paste.

```bash
AcerHelper.Probe.exe --dump-acpi    # extract the firmware's own ACPI tables
```

This is how the interface is understood at source. It writes every table to an
`acpi\` folder and prints which one carries each WMI GUID and the control method
behind it. Note it reads tables **from the registry** as well as the firmware
API, because `GetSystemFirmwareTable` returns only the first table for a
duplicated signature and hides the rest — on ANV15-41 the gaming interface lives
in `SSD8`, which the API never returns.

Decompile with [ACPICA's `iasl`](https://github.com/acpica/acpica/releases):

```bash
iasl -d acpi\SSD8.aml
```

SMU flags, only meaningful with PawnIO installed:

```bash
AcerHelper.Probe.exe --smu          # validation probe, read-only
AcerHelper.Probe.exe --find-pawnio  # locate PawnIO if the usual paths miss
```

---

## Writing tests (these change your machine)

Only after the read-only steps. All are reversible.

```bash
AcerHelper.Probe.exe --test-profile     # cycles profiles, restores the original
AcerHelper.Probe.exe --test-fans        # manual duty under a watchdog
AcerHelper.Probe.exe --health-on        # enables the 80% charge cap
AcerHelper.Probe.exe --health-off       # disables it
AcerHelper.Probe.exe --gpu-core=-100    # GPU offset in MHz, cleared by a reboot
AcerHelper.Probe.exe --overdrive-on     # display overdrive; a no-op on ANV15-41
AcerHelper.Probe.exe --smu-co=-10       # Curve Optimizer offset, needs PawnIO
AcerHelper.Probe.exe --smu-reset        # clear it
```

`--test-fans` puts the fans under software control. A watchdog reverts to Auto
if the process stops responding, if any sensor crosses 85 °C, or on Ctrl+C, and
duty is clamped to a 30 % floor so the fans cannot stall. **Watch it the first
time anyway.**

`--test-profile` verifies by reading back, so a profile the firmware silently
ignores shows as `MISMATCH` rather than passing.

Battery calibration is deliberately not exposed. It is a multi-hour full
discharge cycle and belongs behind a deliberate decision, not a flag.

---

## Just want your model supported?

You do not have to read the rest of this file, fork anything, or write code. One
read-only command is enough:

```bash
AcerHelper.Probe.exe --sweep --battery
```

Paste the output into a
**[model support request](https://github.com/duncan077/A-helper/issues/new?template=model-support.yml)**.
The form asks for your model and BIOS version and nothing else you would have to
look up.

If the probe says the gaming interface was not found, file it anyway — a model
that does *not* work is worth recording, because it marks where the supported
range ends.

The rest of this file is for people who want to identify undocumented settings
themselves.

---

## Reporting findings

Open an issue with:

1. **Model and BIOS version** — exactly as reported by the command at the top
2. **Probe output** — `AcerHelper.Probe.exe --sweep --battery > report.txt`
3. **What differs** from the ANV15-41 baseline in `README.md`
4. For a newly identified index: what you changed, and the `--watch` line showing
   the index move

Raw values are more useful than conclusions. `0x53` tells the next person
something; "profiles work" does not.

Then add a row to **[MODELS.md](MODELS.md)**, which records what has been
confirmed on which machines. A confirmed *absence* belongs there too — it stops
the next person hunting for something their model does not have.

---

## Adding support in code

Model-specific behaviour should almost never be a model check. The firmware
advertises its own capabilities, and reading those works on hardware nobody has
tested:

- thermal profiles — misc index `0x0A` bitmap
- sensors — `GetGamingSysInfo(0x0000)`, bits 39:24
- battery functions — `uFunctionList` from method 20

`AcerGamingWmi.GetSupportedProfiles()` is the pattern to follow. Gate the UI on
what the machine reports, not on the enum.

If you genuinely need a model quirk, keep it in one place and comment it with
the model and BIOS version that justified it.

Please read `CLAUDE.md` before changing the hardware layer — it documents the
AOT and COM-apartment constraints, which cause failures that look like
unrelated bugs.
