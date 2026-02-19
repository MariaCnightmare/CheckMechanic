param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [string]$OutputDir = "dist/app"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src/CheckMechanic.Desktop/CheckMechanic.Desktop.csproj"
$outPath = Join-Path $repoRoot $OutputDir

if (Test-Path $outPath) {
    Remove-Item -Recurse -Force $outPath
}
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$sc = if ($SelfContained) { "true" } else { "false" }

Write-Host "Publishing CheckMechanic.Desktop => $outPath"

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $sc `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $outPath

Write-Host "Publish complete: $outPath"
