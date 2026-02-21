param(
    [string]$Configuration = "Release",
    [string]$Version = "0.0.0"
)

Write-Host "Building installer..."
Write-Host "Configuration: $Configuration"
Write-Host "Version: $Version"

# 仮ビルド（例）
dotnet publish -c $Configuration

# 仮ZIP出力先
$OutputDir = "artifacts"
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$ZipPath = Join-Path $OutputDir "CheckMechanic-Setup.zip"

Compress-Archive -Path "." -DestinationPath $ZipPath -Force

Write-Host "Created: $ZipPath"
