> Historical record preserved from the R36 audit checkout. Paths, packaging and repository descriptions below describe that earlier checkout; current instructions are in the root AGENTS.md and DOCS guides. See [model provenance](../model-provenance.md) for corrected R36 attribution.

# AGENTS.md - read this first (every AI model and every contributor)

This repository is **gd32emu**, a general-purpose emulator for GigaDevice GD32F3x0 microcontrollers. It is maintained
by **Anargon** and has been developed with AI models. Its main user is **Anargon's Deco 640 Tools** (the `anargons-deco640-tools` tablet-firmware builder), a
separate repository that compiles `gd32emu.cs` into its own executable (anargons-deco640-tools's `EMULATOR.md` lists the parts it
relies on).

You are probably working in a fork or on a branch for a pull request, together with a person: **your partner** (the
contributor you are working for).

## 1. Before you change anything: read every model file
Each AI model that has worked here keeps its own notes file at the top of the repository, named after the model
family (`CLAUDE.md`, and for example `GEMINI.md`, `GPT.md` or `CODEX.md` if those models contribute). Each file holds
that model's discoveries, how they were verified, and what is still open. **Read all of them**, not only the one with
your name; the findings are not repeated here. If two disagree, the more recent wins - say so in your PR.

Current model files: **`CLAUDE.md`** and **`CODEX.md`**.

## 2. Models that have worked on this project
| anargons-deco640-tools revisions | Model | Made by | Notes file | What it did |
|---|---|---|---|---|
| 1 - 11 | Claude Sonnet 5.5 (max effort) and Claude Opus 4.8 | Anthropic | `CLAUDE.md` | a flash-controller model in emulation, used to prove the `.data` restorer that recovered the bricked tablet |
| 12 - 23 | Claude Opus 4.8 (Max effort) | Anthropic | `CLAUDE.md` | the Python Thumb-2 interpreter: boot, virtual USB host, then real-time timers, interrupts, ADC and USB frames |
| 24 - 30 | Claude Opus 4.8 (Max effort) | Anthropic | `CLAUDE.md` | conversion to C#; flash controller with PGERR/WPERR, system reset; split out as a GD32-only emulator with `Board` |
| 31 - 32 | Claude Opus 4.8; follow-ups Claude Opus 5.5 | Anthropic | `CLAUDE.md` | bootloader harness used to verify the reset-vector self-heal; moved to its own repository |
| 36 candidate | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]) | OpenAI | `CODEX.md` | flash isolation, failure statuses, strict input, real regressions, implemented device profiles and native host lifecycle review |

Revision numbers are the anargons-deco640-tools project's; the full per-revision record is in anargons-deco640-tools's `history/` folder.

**When you contribute:** add a row and create or extend your own notes file. If you are unsure of your exact model
version, say so instead of guessing.

## 3. Rules for every change (Anargon's zero-trust model)
1. **Model the GD32, not the STM32.** Keep the device list GigaDevice-only, the memory map strict, and ST-fingerprinted
   images refused. A register-compatible guess is not a model.
2. **Model what the hardware does, including the bad parts.** Flash programs only erased words (else PGERR),
   write-protection fails (WPERR), faults are raised rather than ignored. Projects rely on the emulator to show them
   their mistakes.
3. **Verify before claiming.** Say whether a behaviour was checked against the datasheet, a real chip, or only inferred.
4. **Do not break your users.** anargons-deco640-tools depends on the classes listed in its `EMULATOR.md`. If you change them, say so
   in the PR; ideally run anargons-deco640-tools's checks (`AnargonsDeco640Tools twin --selftest`, all five presets) against your branch.
5. **Keep the shared C# subset** (C# 5: no generics, LINQ, `out`/`ref` or unsigned types), compilable with Windows'
   `csc.exe` and translatable to Java with anargons-deco640-tools's `tools/cs2java.py`.
6. **Record what you learn in your own model file**, with how it was verified, and mention it in your PR.

## 4. Where to start
| You want to... | Read |
|---|---|
| use or build the emulator | `README.md` |
| understand how it is built | `DESIGN.md` |
| know what has been found and what is open | every model file (`CLAUDE.md`, ...) |
