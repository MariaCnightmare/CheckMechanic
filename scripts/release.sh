#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  scripts/release.sh --version <VERSION> [options]

Required:
  --version <VERSION>                 Release version (e.g. 1.0.2)
  --bucket <BUCKET>                   S3 bucket name
  --distribution-id <ID>              CloudFront distribution ID
  --domain <DOMAIN>                   Public domain for download URLs (e.g. checkmechanic.apiron.jp)

Optional:
  --repo-win-path <PATH>              Windows repo path (default: auto from current directory)
  --notes-url <URL>                   Release notes URL (default: https://<domain>/checkmechanic/<version>/notes.html)
  --skip-build                         Skip installer build
  --skip-upload                        Skip S3 upload
  --skip-invalidation                  Skip CloudFront invalidation
  -h, --help                           Show help
EOF
}

VERSION=""
BUCKET=""
DISTRIBUTION_ID=""
DOMAIN=""
NOTES_URL=""
SKIP_BUILD=0
SKIP_UPLOAD=0
SKIP_INVALIDATION=0
REPO_WIN_PATH=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2 ;;
    --bucket) BUCKET="${2:-}"; shift 2 ;;
    --distribution-id) DISTRIBUTION_ID="${2:-}"; shift 2 ;;
    --domain) DOMAIN="${2:-}"; shift 2 ;;
    --repo-win-path) REPO_WIN_PATH="${2:-}"; shift 2 ;;
    --notes-url) NOTES_URL="${2:-}"; shift 2 ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --skip-upload) SKIP_UPLOAD=1; shift ;;
    --skip-invalidation) SKIP_INVALIDATION=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$VERSION" || -z "$BUCKET" || -z "$DISTRIBUTION_ID" || -z "$DOMAIN" ]]; then
  echo "Missing required options." >&2
  usage
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [[ -z "$REPO_WIN_PATH" ]]; then
  REPO_WIN_PATH="$(wslpath -w "$REPO_ROOT")"
fi

if [[ "$REPO_WIN_PATH" == \\\\wsl.localhost\\* ]]; then
  echo "repo-win-path resolves to a WSL UNC path: $REPO_WIN_PATH" >&2
  echo "Use a drive-letter Windows path with --repo-win-path (e.g. C:\\Users\\ATake\\workspace\\CheckMechanic\\CheckMechanic)." >&2
  exit 1
fi

REPO_WSL_PATH_FROM_WIN="$(wslpath -u "$REPO_WIN_PATH" 2>/dev/null || true)"
ARTIFACT_ROOT="$REPO_ROOT"
if [[ -n "$REPO_WSL_PATH_FROM_WIN" && -d "$REPO_WSL_PATH_FROM_WIN" ]]; then
  ARTIFACT_ROOT="$REPO_WSL_PATH_FROM_WIN"
fi

if [[ -z "$NOTES_URL" ]]; then
  NOTES_URL="https://${DOMAIN}/checkmechanic/${VERSION}/notes.html"
fi

DOWNLOAD_URL="https://${DOMAIN}/checkmechanic/${VERSION}/Setup.exe"
SETUP_PATH="$ARTIFACT_ROOT/dist/installer/Setup.exe"
MANIFEST_PATH="$ARTIFACT_ROOT/dist/latest.json"

echo "[release] repo          : $REPO_ROOT"
echo "[release] repo(win)     : $REPO_WIN_PATH"
echo "[release] artifacts     : $ARTIFACT_ROOT"
echo "[release] version       : $VERSION"
echo "[release] download_url  : $DOWNLOAD_URL"
echo "[release] notes_url     : $NOTES_URL"
echo "[release] bucket        : s3://$BUCKET"
echo "[release] distribution  : $DISTRIBUTION_ID"

if [[ "$SKIP_BUILD" -eq 0 ]]; then
  echo "[release] building installer..."
  pwsh.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-Location '$REPO_WIN_PATH'; & .\scripts\build_installer_zip.ps1 -Configuration Release -Version $VERSION"
fi

if [[ ! -f "$SETUP_PATH" ]]; then
  echo "Setup.exe not found: $SETUP_PATH" >&2
  exit 1
fi

echo "[release] generating latest.json..."
pwsh.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-Location '$REPO_WIN_PATH'; & .\scripts\new_update_manifest.ps1 -Version $VERSION -DownloadUrl '$DOWNLOAD_URL' -ReleaseNotesUrl '$NOTES_URL'"

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

if [[ "$SKIP_UPLOAD" -eq 0 ]]; then
  echo "[release] uploading artifacts..."
  aws s3 cp "$SETUP_PATH" "s3://${BUCKET}/checkmechanic/${VERSION}/Setup.exe" --content-type application/octet-stream
  aws s3 cp "$MANIFEST_PATH" "s3://${BUCKET}/latest.json" --content-type application/json --cache-control "no-cache, no-store, must-revalidate"
fi

if [[ "$SKIP_INVALIDATION" -eq 0 ]]; then
  echo "[release] creating cloudfront invalidation..."
  aws cloudfront create-invalidation --distribution-id "$DISTRIBUTION_ID" --paths "/latest.json" "/checkmechanic/${VERSION}/Setup.exe"
fi

echo "[release] done"
echo "[release] verify: https://${DOMAIN}/latest.json"
