param(
  [string]$Configuration = "Release",
  [string]$Version = "0.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "== CheckMechanic build_installer_zip.ps1 =="
Write-Host "Configuration: $Configuration"
Write-Host "Version: $Version"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $RepoRoot

$ArtifactsDir = Join-Path $RepoRoot "artifacts"
if (Test-Path $ArtifactsDir) {
  Remove-Item $ArtifactsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ArtifactsDir | Out-Null

# --- 1) ここで「インストーラ生成」を実行する（プロジェクトに合わせて調整する箇所） ---
# 既に installer/packaging に生成スクリプトがあるなら、ここをそれに置換してください。
# 例:
#   pwsh -NoProfile -ExecutionPolicy Bypass -File ".\installer\build.ps1" -Configuration $Configuration -Version $Version
#   または
#   dotnet build .\CheckMechanic.sln -c $Configuration

Write-Host "Step: dotnet build (baseline)"
dotnet build ".\CheckMechanic.sln" -c $Configuration

# --- 2) 生成された「インストーラ exe」を探索する ---
# 期待：dist/ や installer/ 配下に Setup.exe / *Setup*.exe / *Installer*.exe 等が出る
Write-Host "Step: find installer exe candidate"

# 探索対象ディレクトリ候補（必要なら追加）
$SearchRoots = @(
  (Join-Path $RepoRoot "dist"),
  (Join-Path $RepoRoot "installer"),
  (Join-Path $RepoRoot "packaging"),
  (Join-Path $RepoRoot "src"),
  $RepoRoot
) | Where-Object { Test-Path $_ }

$Candidates = @()
foreach ($r in $SearchRoots) {
  $found = Get-ChildItem -Path $r -Recurse -File -Filter "*.exe" -ErrorAction SilentlyContinue
  if ($found) { $Candidates += $found }
}

# 「それっぽい」順で絞り込み
$InstallerExe =
  $Candidates |
  Where-Object {
    $_.Name -match '(?i)setup|installer|bootstrapper|burn|checkmechanic'
  } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $InstallerExe) {
  Write-Host "---- DEBUG: exe candidates (newest 40) ----"
  $Candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 40 FullName, LastWriteTime | Format-Table -AutoSize
  throw "Installer EXE not found. Please adjust build step to actually produce installer, or expand the exe name pattern."
}

Write-Host "Installer candidate: $($InstallerExe.FullName)"

# --- 3) artifacts/Setup.exe として固定名で配置 ---
$SetupOut = Join-Path $ArtifactsDir "Setup.exe"
Copy-Item -Path $InstallerExe.FullName -Destination $SetupOut -Force
Write-Host "Copied: $SetupOut"

# --- 4) ZIP 作成（Setup.exe を必ず含める） ---
$ZipOut = Join-Path $ArtifactsDir "CheckMechanic-Setup.zip"
if (Test-Path $ZipOut) { Remove-Item $ZipOut -Force }

Compress-Archive -Path $SetupOut -DestinationPath $ZipOut -Force
Write-Host "Created ZIP: $ZipOut"

# --- 5) 目視ログ ---
Write-Host "Artifacts:"
Get-ChildItem -Path $ArtifactsDir -File | Format-Table Name, Length, LastWriteTime -AutoSize
