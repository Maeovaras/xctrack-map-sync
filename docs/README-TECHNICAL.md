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

Device status uses two stages. A native Windows Configuration Manager query
reads only the currently present USB device IDs. On a warm application restart,
the connected AIR³ status is shown in under two seconds. A separate Windows
Shell scan then validates the XCTrack `Map` directory and changes the state
from **USB / MTP connected** to **Connected and ready**. If the Shell scan is
temporarily slow, the USB status remains green and validation is retried
without requiring the cable to be disconnected.

Both stages identify a device by its USB vendor ID, product ID, and serial
number, so they update the same device card. Devices are ordered by that
identity: device A, device B, and the `#1` / `#2` suffixes of identical models
stay the same between checks. The background check runs every 10 seconds, but
the full map comparison is repeated only when the connected pair changes, after
**Refresh**, or after a synchronization or restore.

The progress bar is always determinate. Device checking advances through fixed
USB, folder-validation, inventory, and completion stages. Synchronization and
restore use the actual completed-file count reported by the engine.

As soon as a device is ready, its card shows the number of road maps (`.map`)
and terrain files (`.gec`), even if the second device is not connected yet.
This count reads only names and extensions, not every file's size and date.
It runs once per connection and again after **Refresh**, synchronization, or
restore. The full two-device comparison also shows data sizes.

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

Synchronization never deletes map files.

## Safety, backup, and audit

- Files pass through a temporary Windows staging directory instead of being
  copied directly from MTP to MTP.
- File sizes are checked at the source, in staging, and on the destination.
- Before replacement, the existing file is saved under
  `Documents\XCTrackMapSync\<date>\backup`.
- Every successful transfer is recorded in `restore-manifest.json`. Local
  replacement backups include a SHA-256 value so restore can validate them.
- Each run writes `audit.md` and `audit.csv`.
- A disconnected cable, locked device, or revoked Android permission can
  interrupt MTP operations. Keep an independent backup of important files.

Restart XCTrack on both devices after a successful synchronization.

## Restore behavior

**Restore A** and **Restore B** use the newest matching restore manifest for the
selected connected device. Restore processes the successful transfer records
in reverse order:

- a replaced older file is restored from the verified local backup;
- a file added only by synchronization is copied to a local restore quarantine
  and then removed from the device;
- a file whose current size differs from the recorded synchronized size is
  treated as a conflict and left unchanged;
- if copying an older file back fails, the app attempts to return the
  synchronized file from its local safety copy.

Restore writes a separate `restore-*.csv` audit under the original run
directory. XCTrack must be closed and the device must remain connected and
unlocked during the operation.

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
