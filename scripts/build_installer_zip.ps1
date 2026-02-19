param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [bool]$PublishSingleFile = $true,
    [string]$CoreTempDownloadUrl = "https://www.alcpu.com/CoreTemp/Core-Temp-setup.exe",
    [switch]$UseLocalCoreTempPayload
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$distRoot = Join-Path $repoRoot "dist"
$appDir = Join-Path $distRoot "app"
$installerDir = Join-Path $distRoot "installer"
$zipRoot = Join-Path $distRoot "ziproot"
$zipPath = Join-Path $distRoot "CheckMechanic-Setup.zip"

if (Test-Path $installerDir) {
    Remove-Item -Recurse -Force $installerDir
}
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

& (Join-Path $PSScriptRoot "publish_win_x64.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SelfContained:$SelfContained `
    -PublishSingleFile:$PublishSingleFile `
    -OutputDir "dist/app"

$msiProj = Join-Path $repoRoot "installer/CheckMechanic.Msi.wixproj"
$bundleProj = Join-Path $repoRoot "installer/CheckMechanic.Bundle.wixproj"
$msiPath = Join-Path $installerDir "CheckMechanic.msi"

Write-Host "Building MSI..."
dotnet build $msiProj -c $Configuration `
    -p:Version=$Version `
    -p:AppPublishDir=$appDir

if (!(Test-Path $msiPath)) {
    throw "MSI build output not found: $msiPath"
}

$useLocal = if ($UseLocalCoreTempPayload.IsPresent) { "1" } else { "0" }
$coreTempPayloadPath = Join-Path $installerDir "CoreTempSetup.exe"

if ($UseLocalCoreTempPayload.IsPresent) {
    $localPayload = Join-Path $repoRoot "installer/payload/CoreTempSetup.exe"
    if (!(Test-Path $localPayload)) {
        throw "UseLocalCoreTempPayload was specified, but payload is missing: $localPayload"
    }
    Copy-Item $localPayload $coreTempPayloadPath -Force
}
else {
    Write-Host "Downloading Core Temp installer from official URL..."
    Invoke-WebRequest -Uri $CoreTempDownloadUrl -OutFile $coreTempPayloadPath
}

if (!(Test-Path $coreTempPayloadPath)) {
    throw "Core Temp payload was not prepared: $coreTempPayloadPath"
}

Write-Host "Building Burn bundle..."
dotnet build $bundleProj -c $Configuration `
    -p:Version=$Version `
    -p:MsiPath=$msiPath `
    -p:CoreTempDownloadUrl=$CoreTempDownloadUrl `
    -p:UseLocalCoreTempPayload=$useLocal `
    -p:CoreTempPayloadPath=$coreTempPayloadPath

$bundleExe = Join-Path $installerDir "CheckMechanicBootstrapper.exe"
if (!(Test-Path $bundleExe)) {
    throw "Bundle build output not found: $bundleExe"
}
$setupExe = Join-Path $installerDir "Setup.exe"
Copy-Item $bundleExe $setupExe -Force

if (Test-Path $zipRoot) {
    Remove-Item -Recurse -Force $zipRoot
}
New-Item -ItemType Directory -Force -Path $zipRoot | Out-Null

Copy-Item $setupExe (Join-Path $zipRoot "Setup.exe") -Force
Copy-Item $coreTempPayloadPath (Join-Path $zipRoot "CoreTempSetup.exe") -Force
Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $zipRoot "LICENSE.txt") -Force

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Compress-Archive -Path (Join-Path $zipRoot "*") -DestinationPath $zipPath -Force
Write-Host "Installer ZIP created: $zipPath"
