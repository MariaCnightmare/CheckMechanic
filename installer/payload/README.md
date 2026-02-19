# Core Temp payload (optional)

Place Core Temp installer binary here only if redistribution terms allow it.

Expected file name:
- `CoreTempSetup.exe`

Then build bundle with local payload mode:

```powershell
pwsh ./scripts/build_installer_zip.ps1 -UseLocalCoreTempPayload
```
