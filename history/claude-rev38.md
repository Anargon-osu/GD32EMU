# R38 - gd32emu contribution record

Contributor: Claude (Anthropic); exact model version not recorded in this repository. Date: 2026-11-08. The complete
cross-project record is the tablet repository's `history/claude-rev38.md`.

## Changes (platform layer only, `//@@PLATFORM-BEGIN` .. `END`)

- `P.ToolStart` builds the helper start settings used by `P.RunInResult`. Interactive helpers (`echo`) now run with
  `CreateNoWindow = false` and inherited stdin: they share the caller's console and keyboard like a manual run.
  Before, every helper had `CreateNoWindow = true`; on Windows a console child then gets its own hidden console, and
  xppen-tools' typed UPDATE confirmation could not be answered. Quiet helpers keep the hidden console and closed
  stdin, so they cannot take menu input. Child ownership, the Windows job and Ctrl+C cleanup are unchanged.
- `P.LinePump` keeps one partial line per stream (stdout 0, stderr 1); it no longer glues stderr into a stdout prompt.
  A prompt is shown after `QUIET` (150) ms without output, at the latest `LATEST` (1000) ms after it appeared, so log
  lines flushed after a prompt print above it. `P.PromptTicker` drives that while an interactive helper runs.
  `P.CharPump` echoes whole read chunks under the pump's lock; its two-argument constructor still means stdout.
- No chip, instruction, peripheral, flash or HID reader behaviour changed. `twin --selftest` passes; the Java
  translation skips this layer as before.

## Verification and limits

The tablet repository's host regressions cover the start settings, the prompt layout and an interactive helper reading
its caller's input (Linux, Mono and .NET 8). Not run on Windows in this revision: one Windows check of the real update
confirmation remains (see the tablet record).
