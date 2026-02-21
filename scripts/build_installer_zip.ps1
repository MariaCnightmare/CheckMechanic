param(
  [string]$Configuration = "Release",
  [string]$Version = "0.0.0",
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

Write-Host "== CheckMechanic build_installer_zip.ps1 =="
Write-Host "Configuration: $Configuration"
Write-Host "Version: $Version"
Write-Host "Runtime: $Runtime"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $RepoRoot

Write-Host "RepoRoot: $RepoRoot"
Write-Host "RepoRoot listing:"
Get-ChildItem -Path $RepoRoot -Force | Select-Object Name, Mode | Format-Table -AutoSize

$ArtifactsDir = Join-Path $RepoRoot "artifacts"
if (Test-Path $ArtifactsDir) { Remove-Item $ArtifactsDir -Recurse -Force }
New-Item -ItemType Directory -Path $ArtifactsDir | Out-Null

# ------------------------------------------------------------
# 0) “ビルド対象” を自動検出
#    優先:
#      1) src/CheckMechanic.Desktop/*.csproj
#      2) CheckMechanic.Desktop.csproj を repo 全体から探索
# ------------------------------------------------------------
$DesktopProj = $null

$Prefer1 = Get-ChildItem -Path (Join-Path $RepoRoot "src") -Recurse -File -Filter "*.csproj" -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match "(?i)CheckMechanic\.Desktop\.csproj$" } |
  Select-Object -First 1

if ($Prefer1) {
  $DesktopProj = $Prefer1.FullName
} else {
  $Prefer2 = Get-ChildItem -Path $RepoRoot -Recurse -File -Filter "*.csproj" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "(?i)CheckMechanic\.Desktop\.csproj$" } |
    Select-Object -First 1
  if ($Prefer2) { $DesktopProj = $Prefer2.FullName }
}

if (-not $DesktopProj) {
  Write-Host "---- DEBUG: csproj candidates (first 50) ----"
  Get-ChildItem -Path $RepoRoot -Recurse -File -Filter "*.csproj" -ErrorAction SilentlyContinue |
    Select-Object -First 50 FullName |
    ForEach-Object { Write-Host $_ }

  throw "Desktop csproj not found (expected CheckMechanic.Desktop.csproj). Please confirm repo structure."
}

Write-Host "Desktop project: $DesktopProj"

# ------------------------------------------------------------
# 1) dotnet publish で「単体 exe」を作る（これを Setup.exe として扱う）
# ------------------------------------------------------------
$PublishDir = Join-Path $ArtifactsDir "publish"
New-Item -ItemType Directory -Path $PublishDir | Out-Null

Write-Host "Step: dotnet restore"
dotnet restore "$DesktopProj"

Write-Host "Step: dotnet publish (single-file self-contained)"
dotnet publish "$DesktopProj" `
  -c $Configuration `
  -r $Runtime `
  --self-contained true `
  -o "$PublishDir" `
  /p:PublishSingleFile=true `
  /p:PublishTrimmed=false `
  /p:DebugType=None `
  /p:Version=$Version

Write-Host "PublishDir listing:"
Get-ChildItem -Path $PublishDir -File | Select-Object Name, Length | Format-Table -AutoSize

# publish 先の exe を探す（単体 exe が複数出る可能性があるので newest）
$Exe = Get-ChildItem -Path $PublishDir -File -Filter "*.exe" |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $Exe) {
  throw "Published EXE not found in $PublishDir"
}

Write-Host "Published EXE: $($Exe.FullName)"

# ------------------------------------------------------------
# 2) artifacts/Setup.exe に固定名で置く
# ------------------------------------------------------------
$SetupOut = Join-Path $ArtifactsDir "Setup.exe"
Copy-Item -Path $Exe.FullName -Destination $SetupOut -Force
Write-Host "Created: $SetupOut"

# ------------------------------------------------------------
# 3) ZIP 作成（Setup.exe を必ず含める）
# ------------------------------------------------------------
$ZipOut = Join-Path $ArtifactsDir "CheckMechanic-Setup.zip"
if (Test-Path $ZipOut) { Remove-Item $ZipOut -Force }

Compress-Archive -Path $SetupOut -DestinationPath $ZipOut -Force
Write-Host "Created ZIP: $ZipOut"

# ------------------------------------------------------------
# 4) 最終ログ
# ------------------------------------------------------------
Write-Host "Artifacts:"
Get-ChildItem -Path $ArtifactsDir -Recurse -File |
  Select-Object FullName, Length |
  Format-Table -AutoSize
