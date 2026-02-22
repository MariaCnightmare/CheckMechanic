param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [bool]$PublishSingleFile = $true,
    [string]$OutputDir = "dist/app"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$desktopProjectPath = Join-Path $repoRoot "src/CheckMechanic.Desktop/CheckMechanic.Desktop.csproj"
$helperProjectPath = Join-Path $repoRoot "src/CheckMechanic.SensorHelper/CheckMechanic.SensorHelper.csproj"
$outPath = Join-Path $repoRoot $OutputDir

if (Test-Path $outPath) {
    Remove-Item -Recurse -Force $outPath
}
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$sc = if ($SelfContained) { "true" } else { "false" }
$psf = if ($PublishSingleFile) { "true" } else { "false" }

Write-Host "Publishing CheckMechanic.Desktop => $outPath"

dotnet publish $desktopProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $sc `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:PublishSingleFile=$psf `
    -p:PublishTrimmed=false `
    -o $outPath

Write-Host "Publishing CheckMechanic.SensorHelper => $outPath"

dotnet publish $helperProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $sc `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:PublishSingleFile=$psf `
    -p:PublishTrimmed=false `
    -o $outPath

Write-Host "Publish complete: $outPath"
