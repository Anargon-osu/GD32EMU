# gd32emu

A GD32F3x0 chip emulator and standalone command-line runner. The separate sibling `../anargons-tablet-tools/` repository compiles its chip core and supplies the Deco 640 board model. Keep chip and native-platform changes in this repository and tablet-specific changes in the tablet repository.

## Build

From this repository root, run its own build script:

- `build.cmd` uses the system .NET Framework C# compiler and creates `gd32emu.exe`.
- `bash build.sh` uses the .NET 10 SDK and creates a self-contained `gd32emu` executable.

Self-contained GitHub release builds include .NET 10. Optional small Windows builds use the OS-installed .NET Framework. Source builds/tests need development tools. Linux still needs the native libraries of a supported .NET distribution.

## Run

```text
gd32emu IMAGE [--device GD32F350C8] [--vtor 0x08000000] [--steps N | --ms N] [--trace N]
gd32emu --list-devices
gd32emu --help
```

Use `.\gd32emu.exe` in PowerShell or `./gd32emu` on Linux/macOS. Input is strict Intel HEX or raw flash data. Malformed input, unsupported devices and CPU faults return nonzero.

The model implements the firmware-used Thumb-2/Cortex-M4F subset, the GD32F3x0 memory map, RCU, flash controller, timers, NVIC, ADC, GPIO and a USBFS device core driven by a scripted host. Profiles currently implement 64 KB flash and 8 KB SRAM; 128 KB / 16 KB variants are refused. This is not complete instruction or peripheral coverage.

Time is counted in simulated 72 MHz cycles. `--speed uncapped` runs as fast as the host allows; `--speed 72mhz` paces that clock against wall time. Neither mode measures physical erase/program latency. Each `Machine` owns its input flash copy; callers persist `machine.flash` explicitly.

The device list contains GD32 parts. STM32 device names, recognized ST image fingerprints and accesses outside the GD32 map are refused. These checks do not identify every possible binary's origin.

## Library use and testing

Compile `gd32emu.cs` without `gd32emu_cli.cs` to embed the core in another executable. A `Board` subclass supplies ADC values, GPIO/timer events and host hooks. See [DESIGN.md](DOCS/DESIGN.md) and the app's [interface guide](../anargons-tablet-tools/DOCS/EMULATOR.md).

Both component build scripts compile and run `tests/EmulatorTests.cs`, covering instructions, memory, flash failures, reset, strict input and simulated reader sessions. `--list-devices` is only a smoke check; standalone gd32emu has no `--selftest` switch. The app's regressions add fixture-backed firmware and calibration scenarios.

See this repository's [contributor instructions](AGENTS.md) and the tablet project's [verification scope](../anargons-tablet-tools/DOCS/verification.md) before changing shared behavior. Native tablet access, analog behavior and physical timing require their own hardware evidence.

## Related repositories

| Repository | What it holds |
|---|---|
| [`Anargon-osu/anargons-tablet-tools`](https://github.com/Anargon-osu/anargons-tablet-tools) | Anargon's Tablet Tools: the firmware builder that runs on this emulator |
| [`Anargon-osu/deco640-resources`](https://github.com/Anargon-osu/deco640-resources) | stock firmware of the Deco 640 and its hardware recovery guide |
| [`NiceAesth/xppen-tools-builds`](https://github.com/NiceAesth/xppen-tools-builds) | xppen-tools: backing up, flashing and update mode |
