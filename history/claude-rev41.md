# R41 - gd32emu contribution record

Contributor: Claude Opus 5.5 (Anthropic, `claude-opus-5-5`). Date: 2026-11-08.

R41 rewrote the Windows tablet reader in the platform block of `gd32emu.cs` (`P.HidOpenPen`, new `P.HidDescribe`,
`WinRawSession`, `HidStoreBlock`, `WinHidPaths`). Windows holds the Deco 640's pen collection (000D:0002, report ID 7)
for its own pen input, so `CreateFile` cannot open it (sharing violation); digitizer collections are now read through
Raw Input, other collections through ReadFile as before, and every collection is listed with its result. `HidD_*`
BOOLEAN returns are marshalled as one byte. Linux and macOS readers and the emulator core are unchanged.

`tests/EmulatorTests.cs` adds four checks (Raw Input block split, short-block rejection, Windows collection naming,
open-error naming): 55 passed on Windows with csc. Hardware results and the full record are in the tablet
repository's `history/claude-rev41.md`.
