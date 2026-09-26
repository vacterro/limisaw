# LIMISAW

**Üks fail. Mitte midagi paigaldada. Kõik agentide limiidid tray's.**

[English](README.md) · [Eesti](README.ee.md) · **Eesti (lihtne)** · [日本語](README.ja.md)

LIMISAW on Windowsi tray monitaor: kui palju kvooti on jäänud **Codex, Claude Code, Antigravity ja Zcode** juures — iga konto eraldi. Küsib hankijalt numbri, mida hankija juba teab, hankija enda read-only väljakutsega. **Lugemine ei kuluta kvooti.** `auth.json` ei parsita, prompte ei saadeta, midagi ei installita.

**Laadi `LIMISAW.exe` [Releases](https://github.com/vacterro/limisaw/releases) lehelt — kogu paigaldus.** Viimane: **v0.0.9**. Paletid, helid, ikoon — kõik sees. Tühjasse kausta, käivita.

```
LIMISAW.exe          <- Releases'ist, kogu paigaldus
LIMISAW.ini          <- kirjutatakse esimesel käivitusel
```

---

## Mida loeb

| Hankija | Allikas | Aken |
| --- | --- | --- |
| **Codex** | `codex app-server` JSON-RPC `account/rateLimits/read`, üks väljakutse iga `CODEX_HOME` kohta | 5h, nädalane, kuinine (Free), plui reservpuul |
| **Claude Code** | `claude -p "/usage"` (0 pööret, $0.00), plui kolm varuallikat | 5h, nädalane, nädalased mudelite kaupa |
| **Antigravity** | `agy -p "/usage" --output-format json`, plui IDE 429 päevik | nädalane ja 5h **iga mudelipuuli kohta** |
| **Zcode** | `GET /api/monitor/usage/quota/limit` — sama endpoint, mida rakendus ise kasutab | 5h, nädalane (GLM Coding Plan) |

**Lugemine on tasuta.** Kõik need on hankija enda "ütle, ära tee" väljakutsed. Mõõdetud: Claude teatab `num_turns: 0`, `$0.00`; kuus Zcode lugemit järjest — loendurid identsed. Test kinnitab allika vastu. Keegi ei "paranda" sondi küsima mudelilt kvooti.

**Pangatud reset'id.** Codex annab ühekordsed krediidid. Kaart näitab, mis olemas ja millal aegub — `banked: Full reset (Weekly + 5 hr)  expires in 29d` — ja `Use reset` nupp kulutab ühe. Ainult pärast dialoogi, mis nimetab täpse käsu (`account/rateLimitResetCredit/consume`) ja ütleb: tagasi ei saa. Ainus asi, mis hankija juures midagi muudab. Mitte kunagi vaikimisi.

**Avastatakse, mitte ei hardcoded'ita.** Uus sisselogimine = uus kaart, uus tray rida. Koodi ei puutu. `~/.codex`, `~/.codex-account2`, `~/.codex-misiganes` — kõik automaatselt.

**Puul-teadlik blokeering.** Kulutatud pikk aken nullib lühemad ainult **oma puulis**. Claude/GPT nädalane täis ei tea Gemini 5h kohta midagi. Kaart ütleb `locked by <window>`. Antigravity `disabled` 5h ämber jääb päris aknaks — 0% kui puul täis, `--` kui mitte. Mitte kunagi "100% vaba".

**Sweep ei saa mitte õnnestuda — kaart ei tühjene.** Üks CLI viga pole "kvoot otsas". Viimased head numbrid jäävad, tuhmina, `last good HH:MM:SS`, ja rida, mis ütleb miks — hankija enda sõnadega. "Not logged in" on midagi, mida saad parandada.

**Aegunud aken on täis.** Reset'aeg möödus? 100% kohe. Kell tõestas. Sondi ei vaja.

---

## Zcode vajab ühte rida

Zcode'il pole CLI't — Electron asi, PATH'il pole midagi. Vajab API võtit. Ja **võti teise rakenduse konfigis pole LIMISAW'i oma**. Kaks teed:

```powershell
$env:ZAI_API_KEY = "..."        # töötab kohe
```

```ini
; LIMISAW.ini — loeb võtme Zcode'i enda konfigist
ZcodeReadConfig=1
```

Kumbki puudub — kaart on idle, loeb midagi. Olemas — **üks GET**, üks kahest hostist, redirect'id keelatud (võti ei liigu kuhugi), üks väli ühest failist, võti kustutatakse iga sõnumist, mis kaardile või logisse võib jõuda.

---

## Tray

- **Neli paigutust** korda **neli täitmissügavust**.
- **Sa otsustad, mis ikoonile jõuab.** `Tray` vaheleht: iga näid, väärtus, järjestus (lohista), peitmine, limiit 1-9. Üle limiidi — tuhm, mitte kadunud. Uus näid on nähtav. Vaikselt peidetud uus konto näeks välja nagu hankija lagunes.
- **Hõljuta** — teemaline paneel, rida iga näidu kohta, gruppidena. Kesta tooltip on 63 märki. Kümmet näidu ei kannaks.
- **Countdown**: `Off / % / Time`. `Time` — `12m`, `3h`, `2d`. Tundmatu — `--`, mitte kunagi `0m`.
- **Piksel-kunst.** 16x16 võrgustik, täisarv pikslitega skaleeritud. Mitte midagi Windows'i silumislastile.
- **Vasak klõps** aken, **parem** menüü — aktiivses teemas.

## Häired

Iga häire — **kaks lülitit**: balloon ja heli. Üks vaiki, teine töötab — päris soov:

- **Refill**: balloon ja/või heli, kui aken reset'ib (`success_powerup.wav`).
- **Low N%**: balloon ja/või heli **esimest** korda, kui aken langeb läveni (`pop_cartoon_pop.wav`). Üks kord akna kohta tsükli kohta. Ei nuudla. Pluiv aken, mille reset'aeg triibib, pole uus tsükkel. Lävi kuulub **sündmusele**.
- Üks **helitugevus** kõigile (WAV'i näidised skaleeritakse vahemällu), oma WAV'i kaust, `Play` eelvaatleb päris valjusega isegi kui häire väljas.
- Mõlemad **lohista-slaidrid**. Helitugevus tehases 5%.

## Aken

Neli vahelehte (`Accounts`, `Tray`, `Settings`, `CLIs`; klahvid `1`-`4`), grupid `TRAY ICON`, `ALERTS`, `APP`.

- **Kõik seletab ennast** — jaluses, mitte tooltip'is. Tooltip peidaks eelvaate.
- **Kasutu kontroll on nähtavalt surnud.** Ja ütleb miks.
- **Eelvaade on võlts**, oma kvoodi slaidriga. 5% ja 90% ilma ootamata. Renderdab päris tray teed — ei saa ikooniga lahku minna.
- **Lohista järjestama.** Kaardid, read — mõlemad.
- **Konto peitmine** — `x` kaardil; Settings'is `hidden`/`shown`. `Hide spent`, `Only 5h` — kogu laevastikule. Kõik **ainult kuvamine**: sweep loeb kõik ikkagi. Monitor, mis lakkab monitorimast, pole monitor.
- **Parem-lohista** liigutab akna, `Alt+A` alati peal — püsib üle taaskäivituse.
- **Used/Left** (`U`) — pöörab kõik %, **ka ribad täituvad teistpidi**. Värvid hoiatavad selle kohta, mis on *jäänud*.
- **16 teemat**, `T` vahetab.
- `F5` värskenda, `U`, `T`, `Alt+A`, `1`-`4`, parem-hoidke, `Esc`. Aken **kasvab sisu alla**.
- **Start with Windows** — vaikne tray käivitus. Teine käivitus ei tee teist ikooni.
- **Install CLIs** näitab iga hankija enda installi käsku ja jookseb ainult peale selget kinnitust, nähtavas konsoolis.

---

## Kohandamine ilma build'ita

Fail exe kõrval **võidab**:

- `Themes\mine.json` — uus palett; `Themes\goldendefault.json` asendab sisseehitatud.
- `Sounds\` — helikogu. Shippiidud WAV'id leiab nime järgi ikka.
- `heh.ico` — asendab ikooni.
- `LIMISAW.ini` — kõik seaded lihttekstis.

## Ehitamine

Windows'il on kompilaator juba olemas:

```powershell
pwsh .\build.ps1            # -> LIMISAW.exe
pwsh .\build.ps1 -Tests     # build + kogu testikomplekt
```

`tests\` — 34 harnessi / ~6300 väidet: kvoodireeglid ja "lugemine on tasuta" leping, paneeli reeglid, ühefaili väide, tray piksel-puhtus, häire ajastus, blokeering, identiteet, protsessi pidamine, INI read/write, GDI+ ressursi eluiga. Kui harness kirjutatud, et vanal koodil FAILIDA — see nii on.

`tools\make_ico.cs` teeb `heh.ico` uuesti, kui kunst muutub.

---

**Vajab** Windows 10/11 x64 ja hankijaid, mida kasutad. LIMISAW ei logi sind kuhugi sisse — volikirjad on sinu omad, mitte sõltuvus.

**Litsents** — MIT, vaata `LICENSE`.

<!-- source-digest: README.md sha256:00f6be5342abafc8 -->
