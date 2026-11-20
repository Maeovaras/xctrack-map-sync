# XCTrack Map Sync – techninė dokumentacija

[Pagrindinis README](../README-LT.md) · [English](README-TECHNICAL.md)

## Reikalavimai

- Windows x64;
- .NET 10 Desktop Runtime;
- abu įrenginiai atrakinti ir USB ryšiui pasirinktas **File transfer**;
- sinchronizavimo metu abiejuose įrenginiuose uždarytas XCTrack.

Palaikomos XCTrack katalogų schemos:

- `Internal storage/Android/data/org.xcontest.XCTrack/files/Map`;
- `Internal storage/XCTrack/Map`.

Grafinėje programoje įterptas `Sync-XCTrackMaps.ps1`. Prireikus jis
automatiškai išskleidžiamas į `%LOCALAPPDATA%\XCTrackMapSync`.

Įrenginio būsena nustatoma dviem etapais. Greita Windows WPD patikra iškart
parodo prijungtą AIR³. Atskira Windows Shell patikra tada patvirtina XCTrack
`Map` katalogą ir būseną pakeičia iš **USB / MTP prijungtas** į **Prijungtas ir
paruoštas**. Jei Shell patikra laikinai užtrunka, USB būsena lieka žalia, o
patikra kartojama nereikalaujant atjungti laido.

## Sinchronizavimo taisyklės

Pagal nutylėjimą sinchronizuojama:

- `Map/RoadMap` – kelių žemėlapiai (`.map`);
- `Map/gec` – reljefo plytelės (`.gec`).

Trūkstami failai kopijuojami abiem kryptimis. Jei tas pats santykinis kelias
abiejuose įrenginiuose turi skirtingo dydžio failus, aiškiai naujesnis failas
pakeičia senesnį. Jei datos nepasiekiamos arba skiriasi ne daugiau kaip dvi
minutes, konfliktas praleidžiamas ir pažymimas audite. Vienodo kelio ir dydžio
failai laikomi identiškais, nes kai kurie MTP įrenginiai kopijuodami pakeičia
failo datą.

Sinchronizavimo metu programa žemėlapių failų netrina.

## Saugumas, atsarginė kopija ir auditas

- Failai perduodami per laikiną Windows katalogą, o ne tiesiogiai MTP→MTP.
- Failo dydis patikrinamas šaltinyje, laikiname kataloge ir tiksliniame
  įrenginyje.
- Prieš pakeitimą senasis failas išsaugomas
  `Documents\XCTrackMapSync\<data>\backup`.
- Kiekvienas sėkmingas perkėlimas įrašomas į `restore-manifest.json`. Vietinės
  pakeistų failų kopijos turi SHA-256 reikšmę, kuri patikrinama atkuriant.
- Kiekvieno paleidimo rezultatai įrašomi į `audit.md` ir `audit.csv`.
- Atjungtas laidas, užrakintas įrenginys ar atšauktas Android leidimas gali
  nutraukti MTP operaciją. Svarbius failus rekomenduojama saugoti ir atskirai.

Po sėkmingo sinchronizavimo abiejuose įrenginiuose iš naujo paleiskite XCTrack.

## Atkūrimo veikimas

**Atkurti A** ir **Atkurti B** parenka naujausią prijungtam įrenginiui tinkamą
atkūrimo manifestą. Sėkmingų perkėlimų įrašai apdorojami atvirkštine tvarka:

- pakeistas senesnis failas grąžinamas iš patikrintos vietinės kopijos;
- tik sinchronizavimo metu pridėtas failas nukopijuojamas į vietinį atkūrimo
  karantiną ir tada pašalinamas iš įrenginio;
- jei dabartinis failo dydis nesutampa su įrašytu sinchronizuoto failo dydžiu,
  jis laikomas konfliktu ir nekeičiamas;
- jei seno failo nepavyksta grąžinti, programa bando iš vietinės apsauginės
  kopijos vėl įkelti sinchronizuotą failą.

Atkūrimo rezultatai įrašomi į atskirą `restore-*.csv` auditą pradinio paleidimo
kataloge. Atkūrimo metu XCTrack turi būti uždarytas, o įrenginys turi likti
prijungtas ir atrakintas.

## PowerShell variklis

Variklį galima paleisti ir tiesiogiai per PowerShell:

```powershell
.\Sync-XCTrackMaps.ps1 -ListDevices
.\Sync-XCTrackMaps.ps1 -WhatIf
.\Sync-XCTrackMaps.ps1
```

Jei prijungta daugiau nei du tinkami įrenginiai, juos nurodykite rankiniu būdu:

```powershell
.\Sync-XCTrackMaps.ps1 `
  -DeviceA "AIR3-7.3+ #1" `
  -DeviceB "AIR3-7.3+ #2"
```

Norint kopijuoti tik trūkstamus failus ir nepakeisti skirtingo dydžio failų:

```powershell
.\Sync-XCTrackMaps.ps1 -ConflictPolicy SkipConflicts
```

## Kalbos parinkimas komandinėje eilutėje

```powershell
.\XCTrackMapSync.exe --lang=lt
.\XCTrackMapSync.exe --lang=en
```

## Kompiliavimas iš pradinio kodo

Saugyklos šakniniame kataloge paleiskite:

```powershell
dotnet build .\gui\XCTrackMapSync.csproj -c Release

dotnet publish .\gui\XCTrackMapSync.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o .\publish
```

Sukompiliuotam EXE reikalingas .NET 10 Desktop Runtime. EXE pateikiamas per
GitHub Releases, o `bin/`, `obj/` ir `publish/` katalogai į Git istoriją
neįtraukiami.
