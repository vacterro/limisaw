# LIMISAW

**Üks fail. No runtime. Kõik tehisintellekti agendi limiidid süstrayis.**

[English](README.md) · **Eesti** · [Eesti (lihtne)](README.ded.md) · [日本語](README.ja.md)

LIMISAW on Windowsi süstray monitaor, mis näitab, kui palju kvooti sul on jäänud teenustes **Codex, Claude Code, Antigravity ja Zcode** — iga konto, mille need paljastavad. See küsib igalt hankijalt numbri, mida hankija juba teab, kasutades selle hankija enda kirjutuskaitstud väljakutset, nii et **kvoodi lugemine ei kuluta sellest midagi**. See ei pars'i `auth.json`, ei saada prompte ega installi midagi omal algatusel.

**Laadi alla `LIMISAW.exe` lehelt [Releases](https://github.com/vacterro/limisaw/releases) — see on kogu paigaldus.** Viimane: **v0.0.9**. Iga palett, mõlemad häälestushelid ja ikoon on selles sees. Aseta see tühja kausta ja käivita.

```
LIMISAW.exe          <- Releases'ist, see on kogu paigaldus
LIMISAW.ini          <- kirjutatakse esimesel käivitusel, exe kõrvale
```

---

## Mida see loeb

| Hankija | Allikas | Windowsid |
| --- | --- | --- |
| **Codex** | `codex app-server` JSON-RPC `account/rateLimits/read`, üks väljakutse iga `CODEX_HOME` kohta | 5-tunnine, nädalane, kuinine (Free plaan), plui mis tahes reservpuul, mida plaan kannab |
| **Claude Code** | `claude -p "/usage"` (0 pööret, $0.00), plui oleku-reavahemälu, Claude Desktopi enda kasutusnäidistaja ja Claude Code'i keeldumiste päevik varudena | 5-tunnine, nädalane, mudelipõhised nädalased |
| **Antigravity** | `agy -p "/usage" --output-format json`, plui IDE 429 päevik | nädalane **ja** 5-tunnine **mudeli puuli kohta** (Gemini / Claude & GPT) |
| **Zcode** | `GET /api/monitor/usage/quota/limit` — sama moniitori lõpp-punkt, mida rakendus ise kasutab, hostil `api.z.ai` või `open.bigmodel.cn` | 5-tunnine, nädalane (GLM Coding Plan) |

**Lugemine ei maksa midagi.** Iga üks neist on hankija enda "ütle mulle, ära tee" väljakutse — lugemismeetod, või lõppkäsk, mida CLI vastab lokaalselt, või moniitori lõpp-punkt. Mõõdetud, mitte eeldatud: Claude teatab `num_turns: 0` ja `$0.00`, ja kuus järjestikust Zcode lugemit jättis loendurid identsiks. Test kinnitab seda allika vastu, nii et keegi ei saa hiljem "parandada" sondi küsima mudelilt, kui palju kvooti on jäänud.

**Pangastatud lähtestused.** Codex annab ühekordseid krediite, mis täidavad kulutatud akna nõudmisel. Kaart näitab, mis sul on ja millal see aegub — `banked: Full reset (Weekly + 5 hr)  expires in 29d` — ja `Use reset` nupp kulutab ühe, kuid ainult pärast dialoogi, mis nimetab täpse käsu (`account/rateLimitResetCredit/consume`) ja ütleb, et seda ei saa tagasi võtta. See on ainus asi LIMISAW'is, mis muudab midagi hankija juures, ja see ei juhtu kunagi kaudselt.

**Avastatud, mitte kodeeritud.** Iga konto, mida iga hankija paljastab, saab kaardi, tray näidu ja menüü rea — uus sisselogimine ei vaja koodi muutmist. `~/.codex`, `~/.codex-account2`, `~/.codex-whatever`: kõik need, automaatselt. Pla koos reservpuuliga saab oma read selle jaoks, märgistatud hankija nimega selle puuli jaoks.

**Puuliteadlik blokeerimine.** Kulutatud pikk aken nullib lühemad *ainult oma enda kvooti puulis*, nii et ammendatud Claude/GPT nädalane ei fabritseeri surnud Gemini 5-tunnist akent, ja kulutatud Codex nädalane ei fabritseeri surnud `gpt-reserve` akent. Kaart ütleb `locked by <window>`. Antigravity `disabled` 5-tunnine ämber hoitakse reaalsena — blokeeritud 0%-le, kui selle puul on kulutatud, `--` kui ei ole — selle asemel et lugeda 100% vabaks.

**Ebaõnnestunud sweep ei tühjenda kaarti.** Hankija CLI, mis ebaõnnestub ühel korral, ei ole hankija ilma kvoodita. Viimased head numbrid jäävad, tuhmunud ja märgistatud `last good HH:MM:SS`, ühe reaga, mis ütleb, *miks* nad on vananenud — hankija enda sõnades, nii et "Not logged in" loeb midagi, mida saad parandada.

**Möödunud aken on täis.** Kui akna enda lähtestamise aeg möödub, loeb see kohe 100% — kell on seda juba tõestanud; sondi ei vaja.

---

## Zcode vajab ühte lubarida

Zcode on ainus siin oleval hankijal, kellel puudub CLI: see on Electron töölaua rakendus ega pane midagi PATH'ile. Selle kvoot vajab API võtit, ja **võti, mis istub teise rakenduse konfiguratsioonifailis, ei ole LIMISAW'i oma**. Nii et seal on täpselt kaks teed:

```powershell
$env:ZAI_API_KEY = "..."        # töötab kohe, lülitit ei ole
```

```ini
; LIMISAW.ini — lubab LIMISAW'l lugeda võtit Zcode'i enda konfiguratsioonist
ZcodeReadConfig=1
```

Ilma kummata jääb Zcode kaart idle'iks ja ütleb, millised kaks valikut eksisteerivad — midagi ei loeta. Kummaga teeb LIMISAW **ühe GET** ühele kahest konstantsest hostist, keeldub ümbersuunamistest, nii et võti ei saa kuhugi edasi suunata, loeb täpselt ühe välja ühest failist, ja kaitseb võti mis tahes sõnumist, mis võib jõuda kaardile või logisse.

---

## Tray

- **Neli paigutust** — üks number, kaks virnastatud numbrit (halvim lühike halvima pika peal), üks riba iga näidu jaoks, üks lahter iga näidu jaoks 1x1/2x2/3x3 võrgus — korda **neli täitmissügavust** (pooleks, veeranditeks, kaheksandikeks, täpne pikslite järgi).
- **Sa valid, mida see näitab.** Tosina akent nelja hankija poolt ei mahu 16 piksli ikoonile, nii et `Tray` vaheleht loetleb iga avastatud näidu selle reaalse väärtusega ja laseb sul seda ümber tõsta (**lohista rida** või kasuta nooli), peita seda, ja piirata, kui palju neist ikoonile jõuab (1-9). Read pärast piiri on tuhmunud, mitte peidetud. Uus, brändi näid on vaikimisi nähtav — vaikselt värske sisselogimise peitmine näeks välja nagu hankija lagunes.
- **Hõljuta teemalise paneeli jaoks**: üks rida iga näidu jaoks selle enda mõõduga, grupeeritud konto järgi, näidud tray valiku väldes tuhmunud. Kesta tooltip on üks 63-märgiline rida, mis ei saa kanda kümmet näidu.
- **Allaloendamine**: `Tray shows: Off / % / Time`. `Time` paneb oote kuni selle akna enda lähtestamiseni ikoonile — `12m`, `3h`, `2d`. Tundmatu või möödunud tempel on `--`, mitte kunagi `0m`.
- **Piksel-kunst, alati.** Tray ikoon joonistatakse 16x16 võrgul ja skaleeritakse tervete pikslitega; rakenduse ikoon kannab päris raami iga suuruse jaoks, mida kest küsib, laaditud kesta enda laadija kaudu. Midagi ei anta kunagi Windows'ile silumiseks.
- **Üksik vasak klõps** avab akna, parem klõps avab menüü — maalitud aktiivses teemas, mitte süsteemi valges.

## Häired

Iga häire on **kaks lülitit** — balloon ja heli — sest ühe vaigistamine ja teise hoidmine on päris eelistus:

- **Täitmisel**: balloon ja/või kõne, kui aken lähtestab (laevad `success_powerup.wav`).
- **Madal häire N%-le**: balloon ja/või kõne **esimest** korda, kui aken langeb lävepaku juurde (laevad `pop_cartoon_pop.wav`). Üks kord akna kohta iga lähtestamise tsükli kohta, nii et see ei saa tüüdata: esimene sweep pärast käivitust registreerib ainult selle, mis on juba madal, aken, mis jääb madalaks, ei tee uuesti häiret iga värskenduse järel, ja *veerav* aken, mille lähtestamise aeg triibib, kui sa kulutad, ei loeta uueks tsükliks. Lävepakk on **sündmuse oma**, nii et kumb kanal hoiab selle elus.
- **Helitugevus** neile kõigile (Windows'il ei ole heli kohta helitugevust, nii et WAV'i näidised skaleeritakse vahemällu), kaustavalija oma WAV'i raamatukogu jaoks, `WAV` nupp iga sündmuse jaoks ja `Play` nupp, mis eelvaatleb päris helitugevusel isegi siis, kui see häire on väljas.
- Helitugevus ja lävepakk on **lohistamise liugurid** — vajuta rööbast, et hüpata, lohista, et pühkida. Helitugevus laevab 5%-le.

## Aken

Neli vahelehte (`Accounts`, `Tray`, `Settings`, `CLIs`; klahvid `1`-`4`) kannavad iga seadistust, mida tray menüü omab, kolmes märgistatud grupis: `TRAY ICON`, `ALERTS`, `APP`.

- **Kõik seletab ennast.** Hõljuta mis tahes kontrolli ja jalus ütleb, mida see teeb — jalus, mitte ujuv tooltip, sest tooltip üle eelvaate peidab asja, mida sa hindad.
- **Kontroll, mis ei saa oluline olla, on nähtavalt surnud.** Täitmissügavus hallitab välja, kui ikoon joonistab palja numbri; number lugemine hallitab välja ribade ja lahtrite jaoks; helitugevus hallitab välja mõlema kõnega väljas; häire WAV'i valija ilmub ainult siis, kui selle kõne on sees, ja madal lävepakk hallitab välja ainult siis, kui mõlemad madalad kanalid on väljas. Iga üks ütleb miks.
- **Eelvaade on tahtlikult võlts**, oma enda kvoodi liuguriga: hinda mis tahes paigutust 5%-le ja 90%-le ilma ootamata, et konto sinna jõuab. See renderdab läbi päris tray tee, nii et see ei saa ikooniga lahku minna.
- **Lohista ümber tõstmiseks** — tray näidud `Tray` vahelehel, konto kaardid `Accounts`'il.
- **Kontode nähtavus** — `x` igal kaardil peidab selle konto `Accounts` vahelehelt; Settings loetleb iga konto `hidden`/`shown` lülitiga. Kaks kuvafiltrit teevad sama tervele laevastikule: `Hide spent` peidab kontod, mille iga aken loeb 0%, `Only 5h` hoiab kontod, mille 5h aken on praegu kasutatav. Kõik kolm on **ainult kuvamiseks**: sweep sondib iga konto olenemata, nii et peidetud või filtreeritud konto tuleb tagasi ise hetkel, kui kvoot tagasi tuleb — monitaor, mis lakkab moniteerimast, ei ole monitaor.
- **Parem-lohista liigutab akna** (vasak nupp omab iga kontrolli), ja `Alt+A` kinnitab selle **alati peal** — püsiv, nii et monitaor, mis ujus üle sinu tööala, jääb ujuvaks pärast taaskäivitust.
- **Used/Left**: üks nupp (või `U`) pöörab iga protsendi järelejäänud ja kulutatud vahel — **ja ribad täidavad teistpidi ka**, nii et ammendatud konto loeb täis ribana `100%` kasutatud, mitte tühjana. Värvid hoiatavad alati selle kohta, mis on *jäänud*.
- **Teemad**: 16 Wintage paletti on sisse ehitatud; `T` tsükleb neid.
- `F5` värskenda, `U` used/left, `T` teema, `Alt+A` peal, `1`-`4` vahelehed, parem-hoidke liiguta, `Esc` peida. Aken **kasvab, et sisu ära mahuks** — kerimisriba ei peida ridu žesti taga.
- **Start with Windows** käivitab selle vaikses tray's; teine käivitus aktiveerib olemasoleva instantsi, selle asemel et lisada teist ikooni.
- **Install CLIs** näitab iga hankija enda avaldatud installi käsku, selle avaldajat ja selle sihtteed, ja käivitab selle ainult pärast selget kinnitust, nähtavas konsoolis. Kaugskripti torustamine kestasse ei ole kunagi kaudne.

---

## Kohandamine ilma uuesti ehitamiseta

Fail exe kõrval **võidab** manustatud koopia üle:

- `Themes\mine.json` lisab paleti; `Themes\goldendefault.json` asendab sisseehitatud.
- `Sounds\` exe kõrval saab heli raamatukoguks (laevatud WAV'id lahenduvad endiselt nime järgi).
- `heh.ico` exe kõrval asendab rakenduse ikooni.
- `LIMISAW.ini` hoiab iga seadistust lihttekstina.

## Ehitamine

Windows saadab kompilaatori, mida see vajab. Mitte midagi paigaldada:

```powershell
pwsh .\build.ps1            # -> LIMISAW.exe
pwsh .\build.ps1 -Tests     # ehitus + käivita kogu komplekt
```

`tests\` on 34 komplekti / ~6300 väidet: kvoodi reeglid ja ei-maksa-midagi-lugeda leping (`limits.cs`), seadistuste paneeli enda reeglid (`settings_ux.cs`), ühe-faili väide (`standalone.cs`), tray renderdamine piksel-puhtus, akna paigutus, häire ajastus, edasikandmine, blokeerimine, tooltip'id ja tray üksuse valija, stabiilne näidu identiteet (`metric_identity.cs`), tundmatu-vs-null kvoot (`unknown_quota.cs`), käivitatud protsessi sisaldus päris protsessi puudega (`child_job.cs`), partii-shim CLI käivitamine (`cmd_shim.cs`), konto nähtavuse filtrid ja akna kontrollid (`accounts_visibility.cs`), plui iga-allika lugemise eelarved — Codex app-serveri sessiooni bassein, Antigravity ja Claude päeviku skannid, INI lugemis/kirjutamise teed ja värvi radade loodusliku ressursi eluiga (`gdi_paint.cs`).

`tools\make_ico.cs` taastegenerab `heh.ico` ühe punktisampleeritud raamiga iga suuruse jaoks, mida kest küsib, kui kunst muutub.

---

**Vajab** Windows 10/11 x64 ja mis tahes hankijaid, mida sa tegelikult kasutad. LIMISAW ei logi sind tahtlikult millessegi sisse — need on sinu volikirjad, mitte sõltuvus.

**Litsents** — MIT, vaata `LICENSE`.

<!-- source-digest: README.md sha256:00f6be5342abafc8 -->
