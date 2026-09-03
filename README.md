# LIMISAW

**One file. No runtime. Every AI agent limit in your tray.**

LIMISAW is a Windows tray monitor for how much quota you have left across
**Codex, Claude Code and Antigravity** — every account each of them exposes. It
asks each vendor's own CLI, so the numbers come from the vendor's server, not
from a file whose meaning someone guessed. It never reads `auth.json`, never
touches a token, never installs anything on its own.

`LIMISAW.exe` is **248 KB and complete**: every palette, both alert sounds and
the icon are inside it. Drop it in an empty folder and run it.

```
LIMISAW.exe          <- that's the whole install
LIMISAW.ini          <- written on first launch, next to the exe
```

---

## What it reads

| Vendor | Source | Windows |
| --- | --- | --- |
| **Codex** | `codex app-server` JSON-RPC, one call per `CODEX_HOME` | 5-hour, weekly, monthly (Free plan) |
| **Claude Code** | `claude -p "/usage"` (0 turns, $0.00), plus a status-line cache, Claude Desktop's own usage sampler and Claude Code's refusal journal as fallbacks | 5-hour, weekly, per-model weeklies |
| **Antigravity** | `agy -p "/usage" --output-format json`, plus the IDE's 429 journal | weekly **and** 5-hour **per model pool** (Gemini / Claude & GPT) |

**Discovered, not hardcoded.** Every account each CLI exposes becomes a card, a
tray reading and a menu row — a new login needs no code change. `~/.codex`,
`~/.codex-account2`, `~/.codex-whatever`: all of them, automatically.

**Pool-aware gating.** A spent long window zeroes the shorter ones *in its own
quota pool only*, so an exhausted Claude/GPT weekly never fakes a dead Gemini
5-hour window. The card says `locked by <window>`. Antigravity's `disabled`
5-hour bucket is kept as a real window — gated to 0% when its pool is spent,
`--` when it is not — instead of reading as 100% free.

**A failed sweep does not blank a card.** A vendor CLI that fails once is not a
vendor without quota. The last good numbers stay, dimmed and tagged
`last good HH:MM:SS`, with one line saying *why* they are stale.

**An elapsed window is full.** When a window's own reset time passes, it reads
100% immediately — the clock already proved it; no probe needed.

---

## The tray

- **Four layouts** — one number, two stacked numbers (worst short over worst
  long), one bar per reading, one cell per reading in a 1x1/2x2/3x3 grid — times
  **four fill granularities** (halves, quarters, eighths, exact per pixel).
- **You choose what it shows.** Ten-plus windows across three vendors do not fit
  a 16-pixel icon, so the `Tray` tab lists every discovered reading with its live
  value and lets you reorder it (**drag a row** or use the arrows), hide it, and
  cap how many reach the icon (1-9). Rows past the cap are dimmed, not hidden. A
  brand-new reading is visible by default — silently hiding a fresh login would
  look like the vendor broke.
- **Hover for a themed panel**: one row per reading with its own gauge, grouped
  by account, readings outside the tray selection dimmed. The shell's tooltip is
  a single 63-character line, which cannot carry ten readings.
- **Countdown**: `Tray shows: Off / % / Time`. `Time` puts the wait until that
  window's own reset in the icon — `12m`, `3h`, `2d`. An unknown or past stamp is
  `--`, never `0m`.
- **Pixel art, always.** The tray icon is drawn on a 16x16 grid and scaled by
  whole pixels only; the app icon carries a real frame for every size the shell
  asks for, loaded through the shell's own loader. Nothing is ever handed to
  Windows to smooth.
- **Single left click** opens the window, right click opens the menu — painted in
  the active theme, not system white.

## Alerts

Each alert is **two switches** — the balloon and the sound — because muting one
and keeping the other is a real preference:

- **On reset**: a balloon and/or a chime when a window refills (ships
  `success_powerup.wav`).
- **When left <= N%**: fires the **first** time a window drops to the threshold
  (ships `pop_cartoon_pop.wav`). Once per window per reset cycle, so it cannot
  nag: the first sweep after launch only records what is already low, and a
  window that stays low does not re-alert every refresh.
- A **volume** for all of them (Windows has no per-sound volume, so the WAV's
  samples are scaled into a cached copy), a folder picker for your own WAV
  library, a `WAV` button per event and a `Play` button that previews at the
  real volume even when that alert is off.
- Volume and the threshold are **drag sliders** — press the rail to jump, drag to
  scrub. Volume ships at 5%.

## The window

Four tabs (`Accounts`, `Tray`, `Settings`, `CLIs`; keys `1`-`4`) carry every
setting the tray menu has: layout, fill, refresh interval, Used/Left, alerts,
autostart, themes, and a button that opens `LIMISAW.ini`.

- **Used/Left**: one button (or `U`) flips every percentage between remaining and
  spent — **and the bars fill the other way too**, so an exhausted account reads
  as a full bar at `100%` used instead of an empty one. Colours always warn on
  what is *left*.
- **Themes**: 16 Wintage palettes are built in; `T` cycles them.
- `F5` refresh, `U` used/left, `T` theme, `1`-`4` tabs, `Esc` hide. The window
  **grows to fit its content** — no scrollbar hiding rows behind a gesture.
- **Start with Windows** launches it silently in the tray; a second launch
  activates the existing instance instead of adding a second icon.
- **Install CLIs** shows each vendor's own published install command, its
  publisher and its target path, and runs it only after an explicit confirmation,
  in a visible console. Piping a remote script into a shell is never implicit.

---

## Customising without a rebuild

A file next to the exe **wins** over the embedded copy:

- `Themes\mine.json` adds a palette; `Themes\goldendefault.json` replaces the
  built-in one.
- `Sounds\` next to the exe becomes the sound library (the shipped WAVs still
  resolve by name).
- `heh.ico` next to the exe replaces the app icon.
- `LIMISAW.ini` holds every setting in plain text.

## Building

Windows ships the compiler this needs. Nothing to install:

```powershell
pwsh .\build.ps1            # -> LIMISAW.exe
pwsh .\build.ps1 -Tests     # build + run the whole suite
```

`tests\` is 13 harnesses / ~5300 assertions: the quota rules (`limits.cs`), the
one-file claim (`standalone.cs`), tray rendering pixel purity, window layout,
alert timing, carry-forward, gating, tooltips and the tray item picker.

`tools\make_ico.cs` regenerates `heh.ico` with one point-sampled frame per size
the shell asks for, when the artwork changes.

---

**Requires** Windows 10/11 x64 and whichever vendor CLIs you actually use.
LIMISAW deliberately does not log you into anything — those are your
credentials, not a dependency.

**License** — MIT, see `LICENSE`.
