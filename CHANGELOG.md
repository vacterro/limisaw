# LIMISAW 0.0.1 (2026-09-03)

First release as its own program. LIMISAW was a tray monitor inside the SAITULS
toolkit; it is now standalone, and the point of this version is that
**`LIMISAW.exe` is the entire install**.

## Standalone

- **No Python.** The quota engine was a 15-file Python package
  (`Scripts/limisaw_limits/`, ~2900 lines) that the exe shelled out to. It is now
  in-process C# (`Probe.cs`, `ProbeClaude.cs`, `ProbeAntigravity.cs`): the Codex
  app-server JSON-RPC client, the Claude `/usage` parser with all four of its
  sources, the Antigravity pool mapping and 429 journal reader, the window model
  with elapsed-reset and pool-gating passes, and the CLI descriptions. Every rule
  the Python stack enforced is pinned in `tests/limits.cs` (50 assertions).
  - A wedged probe can no longer strand the refresh: there is no child process of
    our own to wait on, only the vendor CLIs, each under its own hard deadline.
  - `python not found` is gone as a failure mode.
- **No folder.** Every palette (16), both alert WAVs and the app icon are
  embedded as resources. `tests/standalone.cs` copies the exe alone into an empty
  directory and proves all of it still loads.
- **Customisation still needs no rebuild.** A `Themes\*.json`, `Sounds\` folder
  or `heh.ico` next to the exe overrides the embedded copy — a palette file with
  the same slug *replaces* the built-in one rather than duplicating it.
- **The app icon has real frames again.** It shipped as a single 128x128 frame,
  so the shell resampled it into every 16x16 slot — the exact blur `UI.md`
  forbids. `heh.ico` now carries whole-pixel 16/24/32/48/64/128 frames, written
  as 32bpp DIBs because `System.Drawing.Icon` misparses a PNG frame in a
  multi-frame file (it takes the `BITMAPINFOHEADER` path and reads past the end)
   — which is exactly the lookup the window and tray icon do.
  `tools/make_ico.cs` regenerates it.
- **One build command.** `pwsh .\build.ps1` compiles with the .NET Framework
  compiler that ships inside Windows; `-Tests` builds and runs all 13 harnesses.

## Carried over from SAITULS 0.1.3

Everything LIMISAW already did, unchanged: three vendors with per-account
discovery, pool-aware gating with `locked by <window>`, Antigravity's disabled
5-hour bucket kept as a real window, carry-forward of the last good numbers,
four tray layouts x four fill granularities, the user-ordered tray item picker
with its 1-9 cap, the themed hover panel and context menu, the four-tab window,
Used/Left with inverted fill, configurable per-event alerts with volume-scaled
WAVs and the once-per-cycle low-quota rule, the countdown readout, 16 Wintage
palettes, autostart and single-instance activation.

## Not carried over

- `SAITULS.exe`, its Explorer context menus, `Registry/`, `Installers/`,
  `i18n/`, `Bin/` and Problip stay in the SAITULS repo — none of them are part of
  reading quota.
- `tests/test_regs.py` was a SAITULS integrity suite (14 `.REG` files, 33 locale
  bundles, PowerShell parse checks). The LIMISAW invariants it also asserted are
  now in `tests/limits.cs` and `tests/standalone.cs`.
