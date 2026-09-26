<img width="1600" height="560" alt="LIMISAW_HEADER1" src="assets/branding/LIMISAW_HEADER1.png" />

# LIMISAW

**LIMISAW = LIMIT SAW.**

**One file. No runtime. Every AI agent limit in your tray.**

[![Latest release](https://img.shields.io/github/v/release/vacterro/limisaw?label=release&color=2ea44f)](https://github.com/vacterro/limisaw/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Platform: Windows 10/11 x64](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078d4)
![Runtime: none](https://img.shields.io/badge/runtime-none-brightgreen)

**English** · [Eesti](README.ee.md) · [Eesti (lihtne)](README.ded.md) · [日本語](README.ja.md)

LIMISAW is a Windows tray monitor for how much quota you have left across
**Codex, Claude Code, Antigravity and Zcode** — every account each of them
exposes. It asks each vendor for the number the vendor already knows, using that
vendor's own read-only call, so **reading your quota never spends any of it**. It
does not parse `auth.json`, does not send prompts, and installs nothing on its
own.

**Download `LIMISAW.exe` from [Releases](https://github.com/vacterro/limisaw/releases) — it is the whole install.** Latest: **v0.0.9**. Every palette, both alert sounds and the icon are inside it. Drop it in an empty folder and run it.

```
LIMISAW.exe          <- from Releases, that's the whole install
LIMISAW.ini          <- written on first launch, next to the exe
```

---

## What it reads

| Vendor | Source | Windows |
| --- | --- | --- |
| **Codex** | `codex app-server` JSON-RPC `account/rateLimits/read`, one call per `CODEX_HOME` | 5-hour, weekly, monthly (Free plan), plus any reserve pool the plan carries |
| **Claude Code** | `claude -p "/usage"` (0 turns, $0.00) once per `CLAUDE_CONFIG_DIR`, plus a status-line cache, Claude Desktop's own usage sampler and Claude Code's refusal journal as fallbacks | 5-hour, weekly, per-model weeklies |
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
`~/.codex-account2`, `~/.codex-whatever`: all of them, automatically. Claude Code
works the same way, because a Claude account IS its config directory: `~/.claude`,
`~/.claude-work`, any `~/.claude-*`, and whatever `CLAUDE_CONFIG_DIR` names (`;`
separates several). Two subscriptions are two cards, each probed with its own
`CLAUDE_CONFIG_DIR` and identified by the directory, never by the label — two
homes may both be called "Claude".

**Adding the second one is a button**, not folklore: Connections → Claude Code →
`Details` → `Add account` makes `~/.claude-account2` and starts Claude's own
`auth login` inside it. The account you already use is untouched — which is the
point, because signing in again the obvious way REPLACES it and the first card
vanishes. Abandon the login and you get no card and no litter: the empty
directory is reused by the next click. A plan with a reserve pool gets its own rows
for it, labelled with the vendor's name for that pool.

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

## Zcode: install it, or just point LIMISAW at your key

Zcode is the only vendor here with no CLI — it is an Electron desktop app and
puts nothing on PATH. So its quota needs an API key.

LIMISAW keeps two ways in — **one of them right in Settings, no file editing**.
Open the `Settings` tab, find the `Zcode` row (the same shape as every other
switch), and turn `Config access` on. LIMISAW reads the key once out of Zcode's
own `%USERPROFILE%\.zcode\v2\config.json`, only the single chosen provider's
field, over the same local file read — and one `GET` later the Zcode card is
live. Turn it off again and LIMISAW stops looking in the config.

That is discoverable because Settings is where users already go; hand-editing
`LIMISAW.ini` remains an allowed advanced/manual path.

### The five things you might actually see there

- `using ZAI_API_KEY` — an environment key you supplied is in charge; the Settings toggle is irrelevant.
- `detected · config access off` — LIMISAW sees Zcode's config but you have not allowed the file read.
- `config access on` — LIMISAW is allowed and no probed account has a Zcode window yet.
- `config allowed · no Z.ai / BigModel plan key` — LIMISAW may read the config but the file has no Z.ai/BigModel credential inside it (for example, an account authenticated another way that LIMISAW deliberately cannot use).
- `not detected` — install Zcode, enable config access in Settings, or set `ZAI_API_KEY`.

---

## The tray

- **Six layouts, one truth** — `Number` (the single reading you pin), `Two`
  (worst short window over worst long one), `Gauge` (one thin horizontal meter
  of the picked reading, no digits), `Bars` (a narrow vertical column per
  selected reading, bottom-up), `Rows` (one horizontal mini-bar per selected
  reading, top to bottom), `Cells` (one block per reading in a 1x1/2x2/3x3
  grid, filled from the opposite edge) — times **two fill granularities**
  (`1/8` steps, or `Exact` per pixel). Old `TrayFill=2/4` values keep working:
  they load as `1/8` without rewriting your ini.
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

Four tabs (`Accounts`, `Tray`, `Settings`, `Connections`; keys `1`-`4`) carry every
setting the tray menu has, in four compact blocks: `TRAY`, `ACCOUNTS`, `ALERTS`,
`APP`. **The window is resizable** — drag any edge or corner — and the **body
scrolls** (wheel, `PgDn`/`PgUp`, `Home`/`End`, a themed scrollbar) whenever a
tab's content is taller than the window, so switching tabs or refreshing data
never moves your window. Position **and size** persist; a size saved on a big
monitor reopens safely inside a small laptop's working area, and the compact
default (about 420x620) fits a 1366x768 desktop outright.

- **Everything explains itself.** Hover any control and the footer says what it
  does immediately; hold still and the same sentence appears as a themed popup
  near the pointer after a short dwell — inside the window, in the active
  palette, never a white system tooltip.
- **A control that cannot matter is visibly dead.** Fill granularity greys out
  while the icon draws a bare number; the number readout greys out for bars and
  cells; the volume greys out with both chimes off; an alert's WAV picker only
  appears while its chime is on, and the low threshold greys out only when both
  low channels are off. Each one says why.
- **The Settings page stays compact.** The account list is one row with a
  `Visibility...` menu (every discovered account, shown or hidden — same hide
  state as the cards' `x`), and the theme wall is one selector: `[<] name [>]
  All...` with every palette reachable. Above ~620px of width the blocks flow
  into two columns; below the minimum width controls wrap instead of
  disappearing. Two display filters stay in place: `Hide spent` hides accounts
  whose every window reads 0%, `Only 5h` keeps accounts whose 5h window is
  usable right now — both **display-only**: the sweep probes every account
  regardless, so a hidden or filtered account comes back on its own the moment
  quota returns — a monitor that stops monitoring is not a monitor.
- **The preview is deliberately fake**, with its own quota slider in one
  compact row: judge any layout at 5% and at 90% without waiting for the
  account to get there. It renders through the real tray path, so it cannot
  disagree with the icon.
- **Drag to reorder** — tray readings on the `Tray` tab, account cards on
  `Accounts` (the full logical order is what drags commit against, scroll
  changes only what you see).
- **Right-drag moves the window** (the left button owns every control), and
  `Alt+A` pins it **always on top** — persisted, so a monitor floated over your
  work area stays floated after a restart.
- **Used/Left**: one button (or `U`) flips every percentage between remaining and
  spent — **and the bars fill the other way too**, so an exhausted account reads
  as a full bar at `100%` used instead of an empty one. Colours always warn on
  what is *left*.
- **Themes**: 16 Wintage palettes are built in; `T` cycles them.
- `F5` refresh, wheel scroll, `PgDn`/`PgUp`, drag edges resize, right-hold
  move, `Esc` hide. No horizontal scrollbar — the minimum width is the width
  everything reflows inside.
- **Start with Windows** launches it silently in the tray; a second launch
  activates the existing instance instead of adding a second icon.
- **Connections** shows every supported vendor (Codex, Claude Code, Antigravity, ZCode) — even on a clean machine — with its structured connection state, recommended action and troubleshooting. A successful periodic quota read now converges the card to **Connected** (or **Connected · quota unavailable** when the vendor authenticates but exposes no window), and an explicit verification failure survives a repaint until a newer result supersedes it. `Install CLIs` actions remain reachable inside vendor cards, each with confirmation in a visible console; ZCode has `Allow & connect` after permission. Piping a remote script into a shell is never implicit.

---

## Customising

A file next to the exe **wins** over the embedded copy:

- `LIMISAW.ini` holds every setting in plain text. **Tip:** `ZcodeReadConfig=1`
  is the advanced/manual twin of the `Settings` toggle — hand editing still
  works, Settings persists the durable truth.
- `Themes\mine.json` adds a palette; `Themes\goldendefault.json` replaces the
  built-in one.
- `Sounds\` next to the exe becomes the sound library (the shipped WAVs still
  resolve by name).
- `LIMISAW.ico` next to the exe replaces the app icon; legacy `heh.ico` still
  works as a fallback.
- The shipped icon is embedded in the exe itself — PNG-compressed frames for
  `16/24/32/48/64/128/256` — and is the executable, taskbar and Alt-Tab picture. The
  dynamic quota icon in the tray stays the quota itself and is not the same thing.
- **Tray truth:** the single-number icon reads the chosen window until it is
  unavailable, then falls back to the lowest *selected* one instead of rendering
  "`--`". "`--`" means no readable eligible tray metric — a reading you hid from
  the tray never leaks back into it, and emptying the selection shows "`--`"
  rather than resurrecting a hidden metric. A readable explicit pin stays
  authoritative even when hidden, because the pin itself is the explicit choice.
  **Bars/Rows/Cells** support every reading up to the legal cap the same way the
  grid already did; `--` and real `0%` look different, one selected `Bars`
  reading stays a narrow centred column instead of owning all 14 interior
  columns, and every reading keeps its own distinct slot at any count 1..9.

<img width="802" height="366" alt="2026-09-19_161819" src="https://github.com/user-attachments/assets/a2b746a8-16fa-42c7-a4bb-1be96434a6f4" />
<img width="438" height="617" alt="2026-09-19_162125" src="https://github.com/user-attachments/assets/8709f356-35da-453f-811a-f151953fe72a" />
<img width="438" height="617" alt="2026-09-19_162131" src="https://github.com/user-attachments/assets/6674c470-079e-4ed1-80b7-775dc8b6867e" />
<img width="438" height="728" alt="2026-09-19_162138" src="https://github.com/user-attachments/assets/2dbe8da9-dcde-4f89-b41a-e838a1bac934" />
<img width="438" height="728" alt="2026-09-19_162141" src="https://github.com/user-attachments/assets/5d76d9f6-2293-4fa0-9298-030f4eda0e8e" />


## The files

| File | What it is |
| --- | --- |
| `LIMISAW.cs` | the whole app: window, tabs, tray painting, alerts, ini binding |
| `Probe.cs` | the sweep: source discovery, per-provider budgets, carry-forward |
| `ProbeClaude.cs`, `ProbeAntigravity.cs`, `ProbeZcode.cs`, `ProbeFreebuff.cs` | one file per vendor's read-only call |
| `Connections.cs` | the Connections surface: discovery, state model, guided onboarding |
| `Assets.cs` | the embedded palettes, sounds and icon frame set |
| `ChildSweeper.cs` | the job object that kills vendor CLI children when LIMISAW exits |
| `build.ps1` | `csc` build and the `-Tests` runner — no runtime, no package manager |
| `tests/` | the harness suite; one `-Tests` run exercises all of it |
| `tools/make_ico.cs` | the canonical PNG -> ICO icon pipeline |
| `Themes/`, `Sounds/` | the shipped palettes and the two alert WAVs |

## Building

Windows ships the compiler this needs. Nothing to install:

```powershell
pwsh .\build.ps1            # -> LIMISAW.exe
pwsh .\build.ps1 -Tests     # build + run the whole suite
```

`tests\` is the whole suite — the last verified full `-Tests` run executed **68
harnesses, 0 failures** on one unchanged tree (every harness prints its own
exact check count each run, so the totals are read from the build log rather
than quoted here): the quota rules and
the costs-nothing-to-read contract (`limits.cs`), the settings panel's own rules
(`settings_ux.cs`), the one-file claim plus the PNG -> ICO generator
(`standalone.cs`), tray rendering pixel purity, window layout, alert timing,
carry-forward, gating, tooltips and the tray item picker, stable reading identity
(`metric_identity.cs`), unknown-vs-zero quota (`unknown_quota.cs`),
spawned-process containment with real process trees (`child_job.cs`),
batch-shim CLI launching (`cmd_shim.cs`), account visibility filters and window
controls (`accounts_visibility.cs`), the singleton launch protocol
(`single_instance.cs`), the settings durability contract — structured save
results, dirty recovery and the verified autostart reconciler with the
rebinding tray menu (`settings_recovery.cs`, `settings_consistency.cs`), the
Connections foundation — tab, discovery, state model and ZCode vertical
(`connections_foundation.cs`) — plus
the per-source read budgets: the Codex app-server session pool, the Antigravity
and Claude journal scans, the INI read/write paths, the Connections
discovery/auth/onboarding/concurrency harnesses and the CLI launch paths
(`connections_concurrency.cs`, `connections_onboarding.cs`, `cli_connections.cs`),
and the paint paths'
native-resource lifetime (`gdi_paint.cs`). The performance wave added its own
deterministic cost harnesses: viable cold-start scheduling for Codex homes
(`codex_scheduling.cs`), lazy deadline-bounded journal discovery
(`lazy_discovery.cs`), the bounded Antigravity body cache (`body_cache.cs`),
viewport-bounded paint work with one per-paint layout snapshot
(`viewport_paint.cs`), and the tray popup content cache
(`hover_cache.cs`). The newest waves added theirs: the external-audit repairs
(`core002_progress_ownership.cs`, `core003_settings_revision.cs`,
`core004_dispatch_failure.cs`), stable Codex account identity
(`codex_remote_identity.cs`), bounded wire payloads (`payload_bounds.cs`), the
reset-credit lock (`reset_lock.cs`), the audio pipeline (`audio_pipeline.cs`),
the tray render modes and the popup (`tray_render_modes.cs`, `tray_popup.cs`),
and per-paint cost guards (`settings_paint_cost.cs`, `sound_paint_cost.cs`).

`tools\make_ico.cs` is the canonical icon pipeline:
`assets\branding\LIMISAW1.png` -> `tools\make_ico.cs` -> `LIMISAW.ico`, one
point-sampled frame per size the shell asks for (16/24/32/48/64/128/256), run
whenever the artwork changes. `build.ps1` regenerates `LIMISAW.ico` from the
brand PNG if it is ever missing. `heh.ico` is not a build input; only a legacy
external override file beside the exe is still honoured at runtime.

---

## Issues and feedback

Vendor quirks and bugs are welcome as [issues](https://github.com/vacterro/limisaw/issues).
A report that names the vendor, the account directory and the exact line LIMISAW
printed is reproducible; "it shows nothing" is not.

---

**Requires** Windows 10/11 x64 and whichever vendors you actually use. LIMISAW
deliberately does not log you into anything — those are your credentials, not a
dependency.

**License** — MIT, see `LICENSE`.
