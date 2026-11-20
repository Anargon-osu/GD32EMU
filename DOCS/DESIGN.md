# gd32emu - the GD32F3x0 emulator (design)

The chip core uses `Board` hooks; the Deco 640 board lives in `../anargons-tablet-tools/atf_twin.cs`. The app's [interface guide](../../anargons-tablet-tools/DOCS/EMULATOR.md) lists the shared contracts. The platform layer `P` also contains native host utilities/readers used by that tool, separate from the simulated chip. See [verification](../../anargons-tablet-tools/DOCS/verification.md) for execution evidence and model limits.

## What it models

- **CPU**: the Cortex-M4F / Thumb-2 instruction subset that GD32F3x0 firmware actually uses (integer core plus the single-precision FPU operations the firmware emits). Execution is a plain interpreter: fetch, decode, step. Faults (unsupported instruction, bad address, unaligned peripheral access) raise a `Fault` reported by the harness. This is a firmware-driven subset, not complete Cortex-M4 instruction coverage.
- **Memory map (64 KB flash / 8 KB SRAM)**: flash at `0x08000000`-`0x0800FFFF`, SRAM at `0x20000000`-`0x20001FFF`, and the peripheral blocks at their GD32 addresses only. Touching an address that only an STM32 part would have is refused - one of the three layers that keep this a GD32-only emulator (see README). `Devices.ALL` only lists implemented memory configurations; 128 KB / 16 KB CB variants are refused.
- **Reset and the vector table**: on reset the CPU loads the initial stack pointer from `[0x08000000]` and the reset address from `[0x08000004]`, then runs from there. Software reset records SWRSTF, resets the vector-table base, locks the flash controller and preserves SRAM. The vector-table offset register (VTOR) is modelled, so firmware that relocates its own vector table behaves correctly. This is what lets the emulator run the tablet's **whole stock flash from reset**, bootloader first.
- **Flash controller (FMC)**: unlock with the key sequence, page **erase**, and **word program into erased words only** (programming a non-erased word raises PGERR, matching hardware). `Machine.bootWp` models option-byte write protection below `0x08003800` (a protected write raises WPERR). This is what makes the emulator able to test the self-reflashing features, including a write-protected chip that must fail safely. The switch protects that whole area; it does not model every possible option-byte protection layout. Selective page-0 failures in the tablet's cleanup tests are injected by a test board.
- **Peripherals**: RCU (clocks), the NVIC (interrupt priorities and dispatch), timers (reload, edges, interrupts), ADC (results fed by the board), GPIO, and the USBFS **device** core driven by a scripted host so enumeration and endpoint traffic can be checked.

## Time

Emulated time is counted in CPU cycles (1 cycle = 1/72 MHz = 13888.889 ps). `--speed uncapped` runs as fast as the PC allows; `--speed 72mhz` paces that model clock against wall time. Pacing does not calibrate instruction/peripheral timing against a physical chip. Flash erase/program currently complete without real hardware latency, and analog signals/noise come from the board model. Timing and hover measurements are model estimates.

## Boards

A `Board` subclass is the only link to the outside world: it supplies ADC results, drives timer/GPIO edges from a model of the physical device, and exposes PC-side hooks. The emulator core never mentions a specific product. The Deco 640 board lives in `../anargons-tablet-tools/atf_twin.cs`; chip changes and board changes remain separate components.

Each `Machine` owns a copy of its flash input. A board may mutate that machine's flash through the model; it must not assume those changes appear in the caller's input array. Persistence explicitly saves `machine.flash`. This keeps parallel scenarios and independent test cases isolated.

## Tablet bootloader harness

These switches are implemented by **`AnargonsTabletTools twin IMAGE ...`** in `atf_twin.cs`, not by standalone `gd32emu`. That board-specific harness drives the tablet's **own bootloader code** through this core:

- `--bl-boot`: power on from reset and run until the firmware is reached (`PC >= 0x08003800`) or a fault - i.e. follow the real reset path (vector -> reset handler -> trampoline -> boot code), including any reset-vector redirect the firmware installed.
- `--bl-state FROM,TO`: set the stored boot state, then call the bootloader's own `set_boot_state(TO)` - used to prove the bootloader counter counts only when the state leaves DFU (2 -> anything else).
- `--bl-erase ADDR`: run the bootloader's own page-erase on a page.
- `--persist FILE`: keep the bootloader area (`0x08000000`-`0x080037FF`) between runs, so a sequence of boots behaves like one physical chip; `--boot-wp` write-protects it.

Together these let the builder reproduce, in software, an interrupted install, a self-heal on the next boot, an idempotent re-boot, a write-protected chip, and a full return-to-stock - without risking a real tablet.

Bootloader/scenario failures return a failing exit status. Persistence saves the machine's owned flash; tests can retain interrupted state deliberately. A successful simulation does not prove physical flash timing or eliminate the reset-page power-loss window.

## Native host session lifecycle

`P` supplies console/files, process execution and optional native tablet access. `RunInResult` preserves exit status and drains stdout/stderr together, each stream with its own partial line; a prompt is shown last, once the helper's output goes quiet. Quiet background helpers get a hidden console and closed stdin. Interactive helpers (`echo`) run in the caller's own console with its input (`CreateNoWindow` off), so a typed confirmation reads the user's keyboard; a hidden console there leaves the prompt unanswerable on Windows. The tablet tool uses this for startup full dumps, pre-flash backups, the typed update confirmation and separate dry-run previews. The chip simulator itself performs no physical dump/flash operation.

Native reader sessions use a generation identifier so late reads/callbacks cannot enter a newer session's packet buffer. `HidOpenPen` closes the old session first. `HidClose` requests cancellation, invalidates the generation and waits within a shared one-second deadline. Reader threads own their resource cleanup; reopening is refused if an old reader has not stopped. Windows uses cancellation for pending reads, Linux reads nonblocking descriptors, and macOS keeps callback/run-loop resources per session. The application closes its calibration source in `finally`. These paths have synthetic lifecycle tests; physical driver/permission and disconnect/reconnect behavior still requires native hardware tests.

## Testing

- `build.cmd` compiles the CLI and `tests/EmulatorTests.cs` with Windows' system C# 5 compiler, then executes the harness. `build.sh` publishes a self-contained application with the .NET 10 SDK and runs `dotnet run --project tests/EmulatorTests.csproj -c Release`.
- The harness executes synthetic instructions, memory bounds, flash failures, reset, strict input/CLI failures and simulated reader-session cases. `gd32emu --list-devices` only lists supported profiles; there is no standalone `--selftest` switch. Invalid input, unsupported devices and CPU faults return nonzero.
- The tablet project's `tests/run-regressions.py` and `tests/run-tests.py` add stock-firmware builds, calibration routing, cleanup and package verification. Its `twin --selftest` checks deterministic numeric primitives.

Each separate repository has its own build workflow; the tablet workflow prepares the configured emulator source before its checks. Cross-publishing produces the requested executable format; it does not establish execution or USB behavior on that OS. Exact package evidence and the remaining hardware/fixture/hosted-CI work are described in [verification](../../anargons-tablet-tools/DOCS/verification.md).
