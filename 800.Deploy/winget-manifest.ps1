# winget-manifest.ps1 - writes the three winget manifest files for one published release.
# Contract: ASCII only (Windows PowerShell 5.1 parses this file). PackageIdentifier must equal AppUpdate.WingetId in the app.
# Contract: the Silent switch must equal AppUpdate.SilentInstallArg - the setup exe installs without asking when it sees it.
# Trap: "winget install <id>" finds nothing until microsoft/winget-pkgs accepts the manifest - writing it here is not enough.
# Usage:  .\winget-manifest.ps1 -Version 1.0.0.20261011        (release tag without the leading "v")
# Output: .\winget\manifests\h\HyungJin\JHJ_Git-Center\<version>\*.yaml
# Next:   winget validate <dir>  ->  winget install --manifest <dir>  ->  wingetcreate submit <dir>  (or a PR to microsoft/winget-pkgs)
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repository = "HyungJIn-kr-97/JHJ_GIT_CENTER",
    [string]$OutRoot = (Join-Path $PSScriptRoot "winget")
)

$ErrorActionPreference = "Stop"
$Version = $Version.TrimStart("v", "V")
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must look like 1.0.0.20261011 (got '$Version')." }

$id        = "HyungJin.JHJ_Git-Center"
$tag       = "v$Version"
$url       = "https://github.com/$Repository/releases/download/$tag/JHJ_Git-Center_Setup.exe"
$outDir    = Join-Path $OutRoot "manifests\h\HyungJin\JHJ_Git-Center\$Version"
$schema    = "1.6.0"

# Hash the exact bytes GitHub serves - a local build may differ from the uploaded asset.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "JHJ_Git-Center-Setup-$Version.exe"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
$sha = (Get-FileHash -Path $tmp -Algorithm SHA256).Hash.ToUpperInvariant()
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
- silent
- silentWithProgress
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
# 계약: 조용한 설치(winget)는 설치 화면의 「필수 도구」가 돌지 않는다 — 같은 두 패키지를 winget 이 먼저 깐다(GitCenter.Setup/Prerequisites.cs 와 짝)
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Git.Git
  - PackageIdentifier: GitHub.cli
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@

# TODO: License is a placeholder - the repository has no LICENSE file yet, and winget-pkgs reviewers ask for a real one.
$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: HyungJin Ju
PublisherUrl: https://github.com/HyungJIn-kr-97
PublisherSupportUrl: https://github.com/$Repository/issues
PackageName: JHJ Git Center
PackageUrl: https://github.com/$Repository
License: Proprietary
ShortDescription: Windows desktop app that shows many git repositories on one screen and fetches, commits and pushes them in bulk.
Moniker: jhjgitcenter
Tags:
- git
- github
- repository
- tray
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/$tag
ManifestType: defaultLocale
ManifestVersion: $schema
"@

[System.IO.File]::WriteAllText((Join-Path $outDir "$id.yaml"), $versionYaml, $utf8)
[System.IO.File]::WriteAllText((Join-Path $outDir "$id.installer.yaml"), $installerYaml, $utf8)
[System.IO.File]::WriteAllText((Join-Path $outDir "$id.locale.en-US.yaml"), $localeYaml, $utf8)

Write-Host "winget manifest written: $outDir"
Write-Host "  sha256: $sha"
Write-Host "  validate : winget validate `"$outDir`""
Write-Host "  try      : winget install --manifest `"$outDir`""
Write-Host "  submit   : wingetcreate submit `"$outDir`"   (winget install wingetcreate, once)"
$outDir
