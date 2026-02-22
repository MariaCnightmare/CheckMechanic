param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [bool]$PublishSingleFile = $true,
    [string]$CoreTempOfficialUrl = "https://www.alcpu.com/CoreTemp/Core-Temp-setup.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$distRoot = Join-Path $repoRoot "dist"
$appDir = Join-Path $distRoot "app"
$installerDir = Join-Path $distRoot "installer"
$zipRoot = Join-Path $distRoot "ziproot"
$zipPath = Join-Path $distRoot "CheckMechanic-Setup.zip"
$payloadWxsPath = Join-Path $repoRoot "installer/AppPayload.wxs"

function New-SafeId {
    param(
        [string]$Prefix,
        [string]$Text
    )

    $raw = ($Text -replace '[^A-Za-z0-9_]', '_')
    if ([string]::IsNullOrWhiteSpace($raw)) {
        $raw = "item"
    }
    if ($raw.Length -gt 54) {
        $raw = $raw.Substring(0, 54)
    }
    if ($raw -match '^[0-9]') {
        $raw = "_" + $raw
    }
    return "${Prefix}_${raw}"
}

function Write-AppPayloadWxs {
    param(
        [string]$PublishDir,
        [string]$OutputWxsPath
    )

    $files = Get-ChildItem -Path $PublishDir -File |
        Where-Object { $_.Name -ne "CheckMechanic.Desktop.exe" } |
        Sort-Object Name

    $lines = @()
    $lines += '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
    $lines += '  <Fragment>'
    $lines += '    <ComponentGroup Id="HarvestedAppPayload">'

    $index = 0
    foreach ($file in $files) {
        $base = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $ext = [System.IO.Path]::GetExtension($file.Name)
        $componentId = New-SafeId -Prefix "Cmp$index" -Text $base
        $fileId = New-SafeId -Prefix "Fil$index" -Text ("$base$ext")
        $source = $file.FullName.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')

        $lines += "      <Component Id=""$componentId"" Directory=""INSTALLFOLDER"" Guid=""*"">"
        $lines += "        <File Id=""$fileId"" Source=""$source"" KeyPath=""yes"" />"
        $lines += '      </Component>'
        $index++
    }

    $lines += '    </ComponentGroup>'
    $lines += '  </Fragment>'
    $lines += '</Wix>'

    Set-Content -Path $OutputWxsPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
}

if (Test-Path $installerDir) {
    Remove-Item -Recurse -Force $installerDir
}
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

& (Join-Path $PSScriptRoot "publish_win_x64.ps1") `
    -Configuration $Configuration `
    -Version $Version `
    -Runtime $Runtime `
    -SelfContained:$SelfContained `
    -PublishSingleFile:$PublishSingleFile `
    -OutputDir "dist/app"

Write-Host "Generating app payload wix fragment..."
Write-AppPayloadWxs -PublishDir $appDir -OutputWxsPath $payloadWxsPath

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

Write-Host "Building Burn bundle..."
dotnet build $bundleProj -c $Configuration `
    -p:Version=$Version `
    -p:MsiPath=$msiPath `
    -p:CoreTempOfficialUrl=$CoreTempOfficialUrl

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
Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $zipRoot "LICENSE.txt") -Force

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Compress-Archive -Path (Join-Path $zipRoot "*") -DestinationPath $zipPath -Force
Write-Host "Installer ZIP created: $zipPath"
