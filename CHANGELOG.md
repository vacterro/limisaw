# LIMISAW 0.0.3 (2026-09-03)

## Fixed

- **A vendor that is installed but refusing now says so, in its own words, on
  the card.** Two defects stacked. The probe discarded the vendor's first stderr
  line ("Not logged in", "timeout") on the way out, so Claude read as "has not
  supplied rate limits yet" — which sounds like nothing has run yet — and
  Antigravity advised installing the CLI that was already on PATH, muted as an
  idle card. And the card could not have shown any of it anyway: the reason was
  drawn only for an account with an empty window list, while every failed
  snapshot ships with unavailable windows filling it, so the sentence existed in
  the model and reached nothing but the tray tooltip. Both halves fixed: the
  vendor's reason survives to the card and hover panel, a refusing CLI is loud
  rather than idle (a vendor that is idle *by design* stays muted), and the card
  height accounts for the line, so a real reason can no longer clip the last row.
  `tests/limits.cs` grew 17 checks (50 -> 67) with a red control against the
  pre-fix behaviour.
- A failing CLI's own first stderr line is now the message the user sees, found
  end-to-end against a real child process, not just through string plumbing.

# LIMISAW 0.0.2 (2026-09-03)

`LIMISAW.exe` drops from 442 KB to **248 KB**, and the icon is no longer stored
twice.

- **The app icon comes from the exe's own win32 icon group.** 0.0.1 embedded
  `heh.ico` a second time as a managed resource, because
  `System.Drawing.Icon` misparses a PNG-compressed frame when it picks a size
  out of a multi-frame file — it takes the `BITMAPINFOHEADER` path and reads past
  the end of the frame. The workaround was to store every frame as a raw DIB,
  which cost 99 KB. The icon is now loaded with the shell's own `LoadImage`
  against this module's icon group (`Marshal.GetHINSTANCE`, not
  `GetModuleHandle(null)` — under any other host the process module is a
  different binary with a different icon), so the frames can be PNG again:
  102 134 -> 4 261 bytes for the artwork, and no second copy inside the assembly.
  It is also the loader Windows uses for the taskbar and Alt-Tab, so the window
  icon cannot disagree with them.
- **`tests/standalone.cs` reads the icon directory, not just the loader's
  answer.** Asking for every size and checking what comes back cannot catch a
  regression here: this artwork is four flat colours, so the shell's reduction of
  a single 128x128 frame is nearly pixel-identical to a real 16x16 one (measured:
  0 differing pixels at 16 and 32, 10 at 24). The harness now also parses
  `heh.ico`'s own directory and fails when a size the shell asks for has no frame
  of its own — which is exactly the single-frame file 0.0.1 inherited. Both red
  controls are recorded: an icon-less build fails at every size, and the pre-fix
  artwork fails with `frames: 128  missing: 16, 24, 32, 48, 64`.

## Fixed

- Three dead `using System.Collections;` left over from the ArrayList-based JSON
  parsing the C# probe replaced.
- `build.ps1` assigned `$args` inside its test loop — PowerShell's automatic
  variable for the caller's own arguments. Renamed to `$testArgs`.

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
- **No folder.** Every palette (16) and both alert WAVs are embedded resources,
  and the app icon is read back out of the exe's own win32 icon group — the one
  Explorer already needs — so there is no second copy of it.
  `tests/standalone.cs` copies the exe alone into an empty directory and proves
  all of it still loads.
- **Customisation still needs no rebuild.** A `Themes\*.json`, `Sounds\` folder
  or `heh.ico` next to the exe overrides the embedded copy — a palette file with
  the same slug *replaces* the built-in one rather than duplicating it.
- **The app icon has real frames again.** It shipped as a single 128x128 frame,
  so the shell resampled it into every 16x16 slot — the exact blur `UI.md`
  forbids. `heh.ico` now carries a point-sampled 16/24/32/48/64/128 frame each,
  PNG-compressed, and the app loads it with the shell's own `LoadImage` instead
  of `new Icon(path, w, h)`: `System.Drawing.Icon` misparses a PNG frame when it
  picks a size out of a multi-frame file (it takes the `BITMAPINFOHEADER` path
  and reads past the end). Going through the OS keeps the icon at 4 KB instead
  of 99 KB of raw DIBs, and it is the same loader that draws the taskbar entry,
  so the window icon cannot disagree with it. `tools/make_ico.cs` regenerates it.
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
