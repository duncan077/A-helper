# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

Native fan, thermal and battery control for Acer Nitro laptops on Windows,
replacing NitroSense. C# / .NET 11, Avalonia UI, published with Native AOT.

Three projects:

| Project | Role |
|---|---|
| `src/AcerHelper.Hardware` | WMI transport and typed hardware API. No UI dependency. |
| `src/AcerHelper.App` | Avalonia desktop UI. |
| `src/AcerHelper.Probe` | Console diagnostics, used to discover capabilities on unknown models. |

## Build

```bash
dotnet build src/AcerHelper.App -c Release
dotnet publish src/AcerHelper.App -c Release -r win-x64      # Native AOT
dotnet publish src/AcerHelper.Probe -c Release -r win-x64
```

Native AOT needs the MSVC toolchain (`link.exe`). Both projects build with
`TreatWarningsAsErrors`; keep them at zero warnings.

When staging a published binary, **delete the old output first**. A failed
publish leaves the previous exe in place, and copying it silently ships a stale
build.

## Hard constraints

These are not style preferences. Violating them produces runtime failures that
look like something else.

### Never reference `System.Management`

It instantiates types by reflection, so the AOT trimmer removes them and the app
dies at runtime with `MissingMethodException: No parameterless constructor
defined for type 'System.Management.WbemDefPath'`. Publishing only warns
(`IL2104` / `IL3053`). No trim descriptor fixes it.

All WMI goes through `Interop/WbemAot.cs` — raw `IWbem*` vtable calls via
`delegate* unmanaged` function pointers and `[LibraryImport]`.

### Never touch a COM pointer off the dispatcher thread

`WbemMethodChannel` holds raw COM interface pointers, which are apartment-bound.
Avalonia's UI thread is STA; thread-pool threads are MTA. Opening on one and
calling from the other fails with `RPC_E_WRONG_THREAD` — and it fails *after* a
successful open, so the window renders and every read silently returns nothing.

Route everything through `AcerHardwareDispatcher`, which owns one dedicated MTA
thread for the channel's whole lifetime, disposal included. `Task.Run` is not a
substitute.

### WMI carries `uint64` as `BSTR`

VARIANT has no unsigned 64-bit type, so WMI represents `CIM_UINT64` as a decimal
string. Passing `VT_I8` is rejected. `PutValue` reads each parameter's declared
CIM type and picks the representation — do not hardcode widths.

### The status byte

Every `AcerGamingFunction` call returns a result whose **low byte is a status
code; 0 means success**. A call that "returns without throwing" has not
necessarily succeeded. `InvokeChecked` enforces this; prefer it over
`InvokeRaw`.

### Firmware writes are asynchronous

The EC reports success before applying a change. After any write that matters,
re-read and verify rather than assuming — see `ToggleHealthModeAsync` and the
probe's `--health-on`.

## Safety rules

`FanMode.Custom` stops the EC regulating the fans, and **persists after the
process exits**. A crash at 0 % duty leaves a machine with unregulated fans.

- Never call `SetFanMode(..., FanMode.Custom)` directly. Use `AcerFanGuard`.
- Never weaken the guard's floor, ceiling, heartbeat or revert paths.
- Battery calibration is a multi-hour full discharge. It must stay behind an
  explicit, informed user action — never a CLI flag or a default.

Probe modes that write must be listed in the `writeFlags` array in
`Probe/Program.cs`, or the closing summary claims nothing changed when it did.

## Testing reality

**The development machine is usually not the target machine.** Acer hardware may
not be present, and everything hardware-related must be verified by the user on
a real Nitro.

- `AcerHelper.Probe.exe --wmi-selftest` exercises the COM transport against
  `Win32_OperatingSystem`. It needs no elevation and no Acer hardware, so it
  isolates interop bugs from hardware questions.
- The app writes `acerhelper.log` beside the executable, with HRESULTs decoded
  to names. Ask for it rather than guessing.
- Do not claim a hardware behaviour is verified unless probe output shows it.
  Say what was compile-verified and what was not.

## Conventions

- Model-specific facts belong in comments with the model and BIOS version that
  produced them (`Verified on ANV15-41 / BIOS V1.51`).
- Capability gating reads the firmware's own bitmaps (`uFunctionList`, misc
  `0x0A`, the sensor bitmap) rather than assuming an enum is fully supported.
  ANV15-41 reports `0x53` for profiles — no Turbo.
- New protocol findings go in `README.md` under the findings section, with the
  raw values observed.
- The UI uses hand-rolled `INotifyPropertyChanged`. Do not add ReactiveUI or
  similar; they are reflection-heavy and fight AOT.
- `AvaloniaUseCompiledBindingsByDefault` is on, so a non-compilable binding is a
  build error rather than a runtime failure on the user's machine. Keep it that
  way, and keep `x:DataType` on views.

## Provenance

The protocol came from the Linux kernel (`acer-wmi.c`, GPL-2.0),
`frederik-h/acer-wmi-battery` (GPL-2.0), and the BIOS's own WMI class
declarations. **No Acer software was disassembled**, and no contribution should
change that — it is what makes the licence position defensible.
