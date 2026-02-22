  param(
      [Parameter(Mandatory = $true)]
      [string]$Version,
      [Parameter(Mandatory = $true)]
      [string]$DownloadUrl,
      [string]$ReleaseNotesUrl = "",
      [string]$SetupExePath = "dist/installer/Setup.exe",
      [string]$OutputPath = "dist/latest.json"
  )

  $ErrorActionPreference = "Stop"

  $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
  $setupPath = Join-Path $repoRoot $SetupExePath
  $manifestPath = Join-Path $repoRoot $OutputPath

  if (!(Test-Path $setupPath -PathType Leaf)) {
      throw "Setup.exe not found: $setupPath"
  }

  $setupHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
  $manifestDir = Split-Path -Parent $manifestPath
  if (!(Test-Path $manifestDir)) {
      New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
  }

  $manifest = [ordered]@{
      version      = $Version
      download_url = $DownloadUrl
      sha256       = $setupHash
  }

  if (![string]::IsNullOrWhiteSpace($ReleaseNotesUrl)) {
      $manifest.release_notes_url = $ReleaseNotesUrl
  }

  $manifest |
      ConvertTo-Json -Depth 4 |
      Set-Content -Path $manifestPath -Encoding utf8

  Write-Host "Update manifest created: $manifestPath"
  Write-Host "sha256: $setupHash"
