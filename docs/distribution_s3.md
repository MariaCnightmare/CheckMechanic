# Distribution via S3 + CloudFront (Velopack)

## Goal
- Distribute Windows app builds via S3 + CloudFront.
- Use Velopack release feed for self-update.
- Keep S3 bucket private (CloudFront OAC).

## Prerequisites
- AWS account with S3 + CloudFront
- OAC configured so CloudFront can read private S3 bucket
- Windows build machine with .NET 8 SDK
- Velopack CLI available locally (global tool or CI step)

## Artifact layout example
Upload release artifacts under:

```text
s3://<private-bucket>/checkmechanic/releases/win-x64/
```

CloudFront public URL example:

```text
https://downloads.example.com/checkmechanic/releases/win-x64/
```

## Build and package flow (example)
1. Build publish binaries:

```powershell
dotnet publish .\src\CheckMechanic.Desktop\CheckMechanic.Desktop.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\desktop
dotnet publish .\src\CheckMechanic.SensorHelper\CheckMechanic.SensorHelper.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\helper
```

2. Place helper next to desktop executable in release layout.
3. Use Velopack tooling to produce release feed + packages.
4. Upload generated `releases/` artifacts to S3.
5. In app configuration, point updater source to CloudFront releases URL.

## CloudFront + OAC notes
- Keep bucket private.
- Allow only CloudFront distribution (OAC principal) in bucket policy.
- Invalidate cache for release index files on new rollout.

## Optional: Signed URL / Signed Cookie
- For gated distribution, CloudFront Signed URL/Cookie can restrict artifact access.
- Update client must be able to access release metadata and package files.
- Prefer short-lived signed access issued by backend when needed.

## Security rules
- Do not commit AWS keys, CloudFront private keys, or credentials.
- Use local credential profiles, IAM roles, or OIDC in CI.
- Keep secret material outside repository and deployment artifacts.
