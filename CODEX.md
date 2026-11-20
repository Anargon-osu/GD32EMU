# Codex findings: current R37 handoff

Contributor: Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]). The coordinator and original R37 delegates were verified as `gpt-6-astra` / `ultra` in local session metadata. R36 is the same model and setting, confirmed by the user (2026-11-09). See [model provenance](DOCS/model-provenance.md).

This is a rolling release with two separate repositories. Read the complete [R37 revision record](../anargons-tablet-tools/history/codex-rev37.md) and original contributor rules in AGENTS.md before making changes. Root history/ in the tablet repository is the durable per-revision handoff; it must not be replaced by assumptions from chat or by the intermediate consolidated app/ packaging layout.

R37 changes console prompt rendering, owned helper lifetimes, read-only native HID/resonance diagnostics, naming and calibration wording. Physical tablet behavior remains unverified. Earlier findings and limits are preserved below.

---

> Historical record preserved from the R36 audit checkout. Paths, packaging and repository descriptions below describe that earlier checkout; current instructions are in the root AGENTS.md and DOCS guides. See [model provenance](DOCS/model-provenance.md) for corrected R36 attribution.

# codex findings

Contributor: Codex (GPT 6 Astra Ultra [Max thinking with Multiagentic Capability]), OpenAI. Tablet project delivery rev 36 audit
candidate, 2026-11-03. Changes were coordinated with the sibling tablet tool.

- Each `Machine` now owns a flash copy. Parallel checks cannot mutate each other's input; persistence callers use
  the machine's resulting flash explicitly. Established by source review and flash isolation regressions.
- CLI faults and invalid images return nonzero. Real instruction, memory-map, flash failure and reset regressions
  replace device-list output as the test. Device profiles are limited to the implemented 64 KB flash / 8 KB SRAM.
- Process helpers preserve exit status and drain both output streams. Startup backup support adds full-path,
  atomic-file and background-job helpers to the platform layer; Java test shims explicitly cannot run a flasher.
- Native HID reader lifecycle is reviewed separately from firmware report routing. Native code compilation cannot
  establish real Windows/Linux/macOS tablet behavior.

No instruction timing was calibrated against a physical chip. Erase/program latency and analog behavior remain
model limitations. The twin regression results and hardware prerequisites are recorded in the sibling tool's
`AUDIT.md`. No real tablet was flashed during this audit.

.NET SDK 10.0.403 published both projects for Windows x64, Linux x64/arm64 and macOS x64/arm64. The .NET 10 emulator
also passed its 31 regression/session checks. Cross-published Linux/macOS binaries were inspected for the correct
OS/architecture, but were not executed on those systems here.
