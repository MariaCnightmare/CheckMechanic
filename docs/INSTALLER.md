# CheckMechanic Installer (Zip + Setup.exe)

## Overview
This project supports a Zip distribution flow:

1. User downloads `CheckMechanic-Setup.zip`
2. User extracts Zip and runs `Setup.exe`
3. Burn bootstrapper installs prerequisites and app
4. User can launch CheckMechanic after completion

Core Temp is treated as a required dependency for temperature-required mode.

## Prerequisites (Developer)
- Windows 10/11 x64
- .NET SDK 8+
- WiX Toolset v4 (via `WixToolset.Sdk` NuGet restore; no separate global install required)

## Project Layout
- `installer/CheckMechanic.Msi.wixproj`: MSI project for app payload
- `installer/Product.wxs`: MSI authoring (Program Files install, Start Menu shortcut)
- `installer/CheckMechanic.Bundle.wixproj`: Burn bundle project
- `installer/Bundle.wxs`: Bundle chain (Core Temp + app MSI)
- `scripts/publish_win_x64.ps1`: Desktop publish to `dist/app`
- `scripts/build_installer_zip.ps1`: End-to-end build to `dist/CheckMechanic-Setup.zip`

## Build Commands

### 1) Publish app
```powershell
pwsh ./scripts/publish_win_x64.ps1 -Configuration Release -Runtime win-x64 -SelfContained $true -PublishSingleFile $true
```

### 2) Build full installer Zip
```powershell
pwsh ./scripts/build_installer_zip.ps1 -Configuration Release -Version 1.0.0 -PublishSingleFile $true
```

Output:
- `dist/installer/CheckMechanic.msi`
- `dist/installer/Setup.exe`
- `dist/CheckMechanic-Setup.zip`

## Core Temp handling

### Detection
Bundle checks the following x64 registry key:

`HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{086D343F-8E78-4AFC-81AC-D6D414AFD8AC}_is1`

If key exists, Core Temp install is skipped.

### Install mode (default)
- Burn downloads Core Temp from official URL and runs silent install:
  - `/VERYSILENT /NORESTART`
- If installer behavior changes and silent install fails, setup exits with error.
  - Fallback: install Core Temp manually from official page, then rerun `Setup.exe`.

### Optional local payload mode
If redistribution is permitted, place installer at:

`installer/payload/CoreTempSetup.exe`

Then build with:

```powershell
pwsh ./scripts/build_installer_zip.ps1 -UseLocalCoreTempPayload
```

## Licensing / redistribution notes
- Core Temp redistribution and automated download are subject to third-party license/terms.
- Default policy in this repo is **not** to bundle Core Temp installer binary.
- Verify the latest terms on official source before distributing at scale.

## End-user flow
1. Extract `CheckMechanic-Setup.zip`
2. Run `Setup.exe` (administrator rights may be required)
3. Follow wizard
4. If Core Temp is missing, setup installs it first
5. Launch CheckMechanic from finish screen or Start Menu
