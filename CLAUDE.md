> Historical record preserved from the R36 audit checkout. Paths, packaging and repository descriptions below describe that earlier checkout; current instructions are in the root AGENTS.md and DOCS guides. See [model provenance](DOCS/model-provenance.md) for corrected R36 attribution.

# CLAUDE.md - Claude's findings and notes for gd32emu

This file is kept by **Claude, an AI model made by Anthropic**, working with **Anargon**, the maintainer. It is one of
the per-model files described in `AGENTS.md`: **every model and contributor should read it**, whichever model they
are. The tablet-specific findings (firmware, bootloader, pen) are in the anargons-tablet-tools repository's `CLAUDE.md`.

## Findings
**These discoveries were made by Claude, an AI model made by Anthropic**, while building and using this emulator for
the Deco 640 project. Each says how it was established.

- **The Deco 640's chip is a GigaDevice GD32F350C8T6, not an STM32.** Its firmware reads the GD32F3x0 flash-size
  register (`0x1FFFF7E0`) and uses the GD32 USBFS core at `0x50000000`, and contains no ST strings, ST USB vendor ID or
  ST-only registers. This is why the emulator is deliberately GD32-only. *(code analysis)*
- **Running the whole flash from reset needs the vector-table register.** The tablet's bootloader starts at
  `0x08000000` and hands over to firmware with its own vector table at `0x08003800`; without modelling VTOR the
  firmware's interrupts go to the bootloader's handlers. *(emulator, against the stock dump)*
- **The flash controller must refuse to program a non-erased word.** Real GD32 flash can only turn 1s into 0s; an
  emulator that silently overwrites hides exactly the bugs that brick a chip. Modelled with PGERR, page erase, and
  WPERR for write-protected pages. *(datasheet behaviour; used to verify anargons-tablet-tools's self-reflashing code)*
- **Splitting the emulator from the tablet changed nothing.** After moving the tablet into a `Board` subclass, every
  check gave byte-for-byte identical results. *(emulator)*
- **Cycle counting is deterministic.** Pacing to 72 MHz or running flat out gives identical results, so measurements
  are reproducible. *(emulator)*
- **GPIO output is now modelled** (rev 33): BOP and BC writes update the output register, and `Peek` reads
  peripherals, so `--watch 0x48000815` shows the LED (PC13). The Windows tablet reader opens every HID collection of
  the tablet (rev 33). *(emulator)*

- **R41 (Claude Opus 5.5): the Windows tablet reader uses Raw Input for digitizer collections.** Windows holds the
  Deco 640's pen collection (000D:0002, report ID 7) for its own pen input, so `CreateFile` fails there with a sharing
  violation, and the mouse/keyboard collections with access denied. `P.HidOpenPen` now reads usage page 000D through
  `RegisterRawInputDevices` (`RIDEV_INPUTSINK`, message-only window, `WinRawSession`) and everything else through
  ReadFile, and `P.HidDescribe` lists every collection with its result. *(hardware: ~400 ID-7 reports/s on
  Anargon's tablet; the full record is in anargons-tablet-tools `history/claude-rev41.md`)*

## Open / limits
- **Flash timing is not modelled.** Erase and program complete instantly in the emulator. The real erase (~60 ms per
  page) and program (~0.1 ms per word) times are datasheet-typical figures, not measured; anargons-tablet-tools's brick-window
  estimate depends on them. Modelling them would let that window be measured in the emulator.
- **Analog behaviour comes from the board.** ADC values, pen signals and noise are supplied by the `Board`, so their
  accuracy is the board's, not the emulator's.

## History
- Revision numbers are the anargons-tablet-tools project's (Anargon's numbering); the full history is in anargons-tablet-tools's `history/`.
- Started as the Thumb-2 interpreter inside anargons-tablet-tools's Python digital twin: boot and virtual USB host in rev 14
  (`deco640_usbsim.py`), real-time scan with timers, interrupts and ADC from rev 15, a flash-controller model as early
  as rev 5 (used to prove the `.data` restorer that recovered the tablet). Converted to C# with the patcher (rev 24),
  split out as a general-purpose GD32-only emulator with `Board` (rev 29), and moved to its own repository (rev 32).
- Bootloader harness (`--bl-boot`, `--bl-state`, `--bl-erase`, `--persist`, `--boot-wp`) added in anargons-tablet-tools rev 28 - 32
  to verify its flash counter, Bootloader Counter and reset-vector self-heal without risking a tablet.
