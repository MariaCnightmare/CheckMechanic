# Distribution via S3 + CloudFront (`latest.json` + `Setup.exe`)

## Goal
- Distribute Windows installer updates via S3 + CloudFront.
- Provide `latest.json` manifest for Desktop update check.
- Keep S3 bucket private (CloudFront OAC).

## Prerequisites
- AWS account with S3 + CloudFront
- OAC configured so CloudFront can read private S3 bucket
- Windows build machine with .NET 8 SDK
- PowerShell 7+

## Artifact layout example
Store a versioned installer and root manifest:

```text
s3://<private-bucket>/checkmechanic/1.0.2/Setup.exe
s3://<private-bucket>/checkmechanic/latest.json
```

CloudFront public URL example:

```text
https://downloads.example.com/checkmechanic/1.0.2/Setup.exe
https://downloads.example.com/checkmechanic/latest.json
```

## Build and package flow (example)
1. Build installer:

```powershell
pwsh ./scripts/build_installer_zip.ps1 -Configuration Release -Version 1.0.2 -PublishSingleFile $true
```

2. Generate update manifest (`sha256` is computed from `dist/installer/Setup.exe`):

```powershell
pwsh ./scripts/new_update_manifest.ps1 `
  -Version 1.0.2 `
  -DownloadUrl "https://downloads.example.com/checkmechanic/1.0.2/Setup.exe" `
  -ReleaseNotesUrl "https://downloads.example.com/checkmechanic/1.0.2/notes.html"
```

3. Upload artifacts:
- `dist/installer/Setup.exe` -> `s3://<private-bucket>/checkmechanic/1.0.2/Setup.exe`
- `dist/latest.json` -> `s3://<private-bucket>/checkmechanic/latest.json`

4. Invalidate CloudFront cache for `/checkmechanic/latest.json`.

## `latest.json` schema
```json
{
  "version": "1.0.2",
  "download_url": "https://downloads.example.com/checkmechanic/1.0.2/Setup.exe",
  "release_notes_url": "https://downloads.example.com/checkmechanic/1.0.2/notes.html",
  "sha256": "hex..."
}
```

## CloudFront + OAC notes
- Keep bucket private.
- Allow only CloudFront distribution (OAC principal) in bucket policy.
- Cache invalidation is required for `latest.json` when releasing updates.

## Optional: Signed URL / Signed Cookie
- For gated distribution, CloudFront Signed URL/Cookie can restrict artifact access.
- Update client must be able to access both `latest.json` and `Setup.exe`.
- Prefer short-lived signed access issued by backend when needed.

## Security rules
- Do not commit AWS keys, CloudFront private keys, or credentials.
- Use local credential profiles, IAM roles, or OIDC in CI.
- Keep secret material outside repository and deployment artifacts.
