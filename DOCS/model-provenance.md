# Model provenance: R36 and R37

| Revision / work | OpenAI Codex model | Reasoning effort | Evidence |
|---|---|---|---|
| R36 audit, calibration, firmware/emulation, host and release work | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]) | Ultra (Max thinking with multi-agent capability) | User confirmation on 2026-11-09 (replaces the earlier 2026-11-04 confirmation) |
| R36 delegates and later follow-ups | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), user-identified; no separate per-delegate manifest retained here | Ultra for R36; separate delegate settings unrecorded | User confirmation and preserved R36 notes |
| R37 coordinator | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), `gpt-6-astra` | Ultra | Local session `turn_context`: `model=gpt-6-astra`, `effort=ultra` |
| R37 console/process investigation and implementation delegates | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), `gpt-6-astra` | Ultra | Each original delegate's local session `turn_context` |
| R37 architecture and naming/calibration delegates | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), `gpt-6-astra` | Ultra | Each original delegate's local session `turn_context` |
| R37 documentation and independent review delegates | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), `gpt-6-astra` | Ultra | Each original delegate's local session `turn_context` |
| R37 structural audit and correction | Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), `gpt-6-astra` | Ultra | Parent session settings inherited without a model or effort override |

The earlier R37 statement that exact model and effort were unavailable was incorrect. The coordinator and original delegate session records were inspected after the user corrected that claim. R36's user-confirmed attribution supersedes older generic GPT-family wording; historical notes remain preserved with correction banners.

R37 retains the reviewed console/input, subprocess ownership, native HID, naming, and resonance/threshold wording changes. The corrected ZIP restores the approved sibling repositories: `anargons-tablet-tools/` and `gd32emu/`, plus the handoff README. Guides live inside each project's DOCS folder; root CLAUDE.md/CODEX.md and the tablet repository's root history/ preserve the contributor handoff. The intermediate consolidated `app/` layout is not the source-repository contract.

Model identity is attribution, not hardware verification. No real tablet was flashed or calibrated in this session. Historical model findings are preserved under [models](../CODEX.md) and in the [original contributor record](models/AGENTS-R36.md).
