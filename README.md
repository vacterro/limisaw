<img width="1600" height="560" alt="LIMISAW_HEADER1" src="https://github.com/user-attachments/assets/ff5571e3-f4d1-44e5-a017-5a3430e67360" />

# LIMISAW

**One file. No runtime. Every AI agent limit in your tray.**

LIMISAW is a Windows tray monitor for how much quota you have left across
**Codex, Claude Code, Antigravity and Zcode** — every account each of them
exposes. It asks each vendor for the number the vendor already knows, using that
vendor's own read-only call, so **reading your quota never spends any of it**. It
does not parse `auth.json`, does not send prompts, and installs nothing on its
own.

**Download `LIMISAW.exe` from [Releases](https://github.com/vacterro/limisaw/releases) — it is the whole install.** Latest: **v0.0.8**. Every palette, both alert sounds and the icon are inside it. Drop it in an empty folder and run it.

```
LIMISAW.exe          <- from Releases, that's the whole install
LIMISAW.ini          <- written on first launch, next to the exe
```

---

## What it reads

| Vendor | Source | Windows |
| --- | --- | --- |
| **Codex** | `codex app-server` JSON-RPC `account/rateLimits/read`, one call per `CODEX_HOME` | 5-hour, weekly, monthly (Free plan), plus any reserve pool the plan carries |
| **Claude Code** | `claude -p "/usage"` (0 turns, $0.00), plus a status-line cache, Claude Desktop's own usage sampler and Claude Code's refusal journal as fallbacks | 5-hour, weekly, per-model weeklies |
| **Antigravity** | `agy -p "/usage" --output-format json`, plus the IDE's 429 journal | weekly **and** 5-hour **per model pool** (Gemini / Claude & GPT) |
| **Zcode** | `GET /api/monitor/usage/quota/limit` — the same monitor endpoint the app itself uses, on `api.z.ai` or `open.bigmodel.cn` | 5-hour, weekly (GLM Coding Plan) |

**Reading costs nothing.** Every one of those is the vendor's own "tell me, do
not do" call — a read method, or a slash command the CLI answers locally, or a
monitor endpoint. Measured, not assumed: Claude reports `num_turns: 0` and
`$0.00`, and six consecutive Zcode reads left the counters identical. A test
asserts it against the source, so nobody can later "improve" a probe into asking
a model how much quota is left.

**Banked resets.** Codex grants one-off credits that refill a spent window on
demand. The card shows what you have and when it expires —
`banked: Full reset (Weekly + 5 hr)  expires in 29d` — and a `Use reset` button
spends one, but only after a dialog that names the exact command
(`account/rateLimitResetCredit/consume`) and says it cannot be undone. It is the
only thing in LIMISAW that changes anything at a vendor, and it never happens
implicitly.

**Discovered, not hardcoded.** Every account each vendor exposes becomes a card,
a tray reading and a menu row — a new login needs no code change. `~/.codex`,
`~/.codex-account2`, `~/.codex-whatever`: all of them, automatically. A plan with
a reserve pool gets its own rows for it, labelled with the vendor's name for that
pool.

**Pool-aware gating.** A spent long window zeroes the shorter ones *in its own
quota pool only*, so an exhausted Claude/GPT weekly never fakes a dead Gemini
5-hour window, and a spent Codex weekly never fakes a dead `gpt-reserve` one. The
card says `locked by <window>`. Antigravity's `disabled` 5-hour bucket is kept as
a real window — gated to 0% when its pool is spent, `--` when it is not — instead
of reading as 100% free.

**A failed sweep does not blank a card.** A vendor CLI that fails once is not a
vendor without quota. The last good numbers stay, dimmed and tagged
`last good HH:MM:SS`, with one line saying *why* they are stale — in the vendor's
own words, so "Not logged in" reads as something you can fix.

**An elapsed window is full.** When a window's own reset time passes, it reads
100% immediately — the clock already proved it; no probe needed.

---

## Zcode needs one line of permission

Zcode is the only vendor here with no CLI: it is an Electron desktop app and puts
nothing on PATH. Its quota needs an API key, and **a key sitting in another
application's config file is not LIMISAW's to take**. So there are exactly two
ways in:

```powershell
$env:ZAI_API_KEY = "..."        # works immediately, no switch
```

```ini
; LIMISAW.ini — lets LIMISAW read the key out of Zcode's own config
ZcodeReadConfig=1
```

With neither, the Zcode card stays idle and says which two options exist —
nothing is read. With either, LIMISAW makes **one GET** to one of two constant
hosts, refuses redirects so the key cannot be forwarded elsewhere, reads exactly
one field out of one file, and redacts the key out of any message that could
reach a card or a log.

---

## The tray

- **Four layouts** — one number, two stacked numbers (worst short over worst
  long), one bar per reading, one cell per reading in a 1x1/2x2/3x3 grid — times
  **four fill granularities** (halves, quarters, eighths, exact per pixel).
- **You choose what it shows.** A dozen windows across four vendors do not fit a
  16-pixel icon, so the `Tray` tab lists every discovered reading with its live
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

- **On refill**: a balloon and/or a chime when a window resets (ships
  `success_powerup.wav`).
- **Low alert at N%**: a balloon and/or a chime the **first** time a window drops
  to the threshold (ships `pop_cartoon_pop.wav`). Once per window per reset cycle,
  so it cannot nag: the first sweep after launch only records what is already low,
  a window that stays low does not re-alert every refresh, and a *rolling* window
  whose reset time drifts as you spend is not mistaken for a new cycle. The
  threshold is the **event's**, so either channel keeps it live.
- A **volume** for all of them (Windows has no per-sound volume, so the WAV's
  samples are scaled into a cached copy), a folder picker for your own WAV
  library, a `WAV` button per event and a `Play` button that previews at the
  real volume even when that alert is off.
- Volume and the threshold are **drag sliders** — press the rail to jump, drag to
  scrub. Volume ships at 5%.

## The window

Four tabs (`Accounts`, `Tray`, `Settings`, `CLIs`; keys `1`-`4`) carry every
setting the tray menu has, in three labelled groups: `TRAY ICON`, `ALERTS`, `APP`.

- **Everything explains itself.** Hover any control and the footer says what it
  does — the footer rather than a floating tooltip, because a tooltip over the
  preview would hide the thing you are judging.
- **A control that cannot matter is visibly dead.** Fill granularity greys out
  while the icon draws a bare number; the number readout greys out for bars and
  cells; the volume greys out with both chimes off; an alert's WAV picker only
  appears while its chime is on, and the low threshold greys out only when both
  low channels are off. Each one says why.
- **The preview is deliberately fake**, with its own quota slider: judge any
  layout at 5% and at 90% without waiting for the account to get there. It renders
  through the real tray path, so it cannot disagree with the icon.
- **Drag to reorder** — tray readings on the `Tray` tab, account cards on
  `Accounts`.
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

`tests\` is 27 harnesses / ~5900 assertions: the quota rules and the
costs-nothing-to-read contract (`limits.cs`), the settings panel's own rules
(`settings_ux.cs`), the one-file claim (`standalone.cs`), tray rendering pixel
purity, window layout, alert timing, carry-forward, gating, tooltips and the tray
item picker, plus the per-source read budgets — the Codex app-server session pool,
the Antigravity and Claude journal scans, the INI read/write paths, and the paint
paths' native-resource lifetime (`gdi_paint.cs`).

`tools\make_ico.cs` regenerates `heh.ico` with one point-sampled frame per size
the shell asks for, when the artwork changes.

---

**Requires** Windows 10/11 x64 and whichever vendors you actually use. LIMISAW
deliberately does not log you into anything — those are your credentials, not a
dependency.

**License** — MIT, see `LICENSE`.
