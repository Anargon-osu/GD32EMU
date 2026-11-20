# R37 - gd32emu contribution record

Contributor: Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), OpenAI; coordinator and original delegate local session records confirm `gpt-6-astra` with `effort=ultra`. R36 was the same, as confirmed by the user. This project follows the tablet project's rolling revision numbering.

## Changes

The native platform layer now owns subprocess lifetimes. Both runners register children and reject launches after shutdown begins. On Windows, a kill-on-close Job Object owns the parent before any child is created, covering child and grandchild cleanup even during forced parent termination. Ctrl+C and ProcessExit trigger managed cleanup; Unix SIGKILL cannot provide the same managed guarantee. Quiet helpers receive closed stdin, preserving menu input; interactive helpers retain confirmation input.

HID diagnostics count all received report IDs while only ID 7 enters the existing telemetry buffer. Counts are reset for each new session and returned as independent snapshots. Generation guards discard late reports. Windows HID continues to use system APIs without WinUSB or an external HID package. No chip instruction, peripheral or flash behavior was changed for these R37 host fixes.

## Verification and limits

Windows native fake-process tests passed normal exit, forced parent termination, generated Ctrl+C and refusal of a subsequent command after cancellation; child and grandchild termination were checked. Synthetic host tests covered ID 2 counting without telemetry, ID 7 buffering, snapshot isolation and stale/stopped sessions. The emulator's 51 instruction/memory/flash/reset/reader checks passed. No real tablet or native Linux/macOS device was used.

The first R37 packaging attempt incorrectly consolidated gd32emu into the tablet repository. The corrected handoff restores this separate repository, its root source/build scripts, AGENTS.md, model files, DOCS and history. Keep generic chip/native-host changes here and tablet-specific firmware/board changes in the separate tablet repository. Never use the discarded app/ wrapper as the next revision's source structure.

Read root CLAUDE.md/CODEX.md and AGENTS.md before changes. Keep this root history directory as the durable revision record. The complete cross-project R37 record is in the sibling tablet repository's history/codex-rev37.md; each future contribution should add its own record here too. Do not introduce semantic-version renumbering.
