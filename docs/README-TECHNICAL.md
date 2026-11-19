# XCTrack Map Sync - technical documentation

[Main README](../README.md) · [Lietuviškai](README-TECHNICAL-LT.md)

## Requirements

- Windows x64;
- .NET 10 Desktop Runtime;
- both devices unlocked and set to USB **File transfer**;
- XCTrack closed on both devices during synchronization.

Supported XCTrack layouts:

- `Internal storage/Android/data/org.xcontest.XCTrack/files/Map`;
- `Internal storage/XCTrack/Map`.

The GUI embeds `Sync-XCTrackMaps.ps1` and extracts it to
`%LOCALAPPDATA%\XCTrackMapSync` when needed.

## Synchronization rules

The default scope is:

- `Map/RoadMap`: road maps (`.map`);
- `Map/gec`: terrain tiles (`.gec`).

Missing files are copied in both directions. If the same relative path has a
different size on the two devices, the clearly newer file replaces the older
one. If timestamps are unavailable or differ by no more than two minutes, the
conflict is skipped and recorded in the audit. Files with the same path and
size are treated as identical because some MTP devices change timestamps while
copying.

The program never deletes map files.

## Safety, backup, and audit

- Files pass through a temporary Windows staging directory instead of being
  copied directly from MTP to MTP.
- File sizes are checked at the source, in staging, and on the destination.
- Before replacement, the existing file is saved under
  `Documents\XCTrackMapSync\<date>\backup`.
- Each run writes `audit.md` and `audit.csv`.
- A disconnected cable, locked device, or revoked Android permission can
  interrupt MTP operations. Keep an independent backup of important files.

Restart XCTrack on both devices after a successful synchronization.

## PowerShell engine

The engine can also be run directly from PowerShell:

```powershell
.\Sync-XCTrackMaps.ps1 -ListDevices
.\Sync-XCTrackMaps.ps1 -WhatIf
.\Sync-XCTrackMaps.ps1
```

If more than two suitable devices are connected, select them explicitly:

```powershell
.\Sync-XCTrackMaps.ps1 `
  -DeviceA "AIR3-7.3+ #1" `
  -DeviceB "AIR3-7.3+ #2"
```

To copy only missing files and never replace different-size files:

```powershell
.\Sync-XCTrackMaps.ps1 -ConflictPolicy SkipConflicts
```

## Language command-line option

```powershell
.\XCTrackMapSync.exe --lang=en
.\XCTrackMapSync.exe --lang=lt
```

## Build from source

Run from the repository root:

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

The generated executable requires the .NET 10 Desktop Runtime. Release
binaries belong in GitHub Releases; `bin/`, `obj/`, and `publish/` are excluded
from Git history.
