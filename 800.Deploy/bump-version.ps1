<#
.SYNOPSIS
    Sets VersionPrefix (major.minor.patch) in Directory.Build.props.
    Copied from JHJ_CS_CORE\800.Deploy\_templates\bump-version.ps1. Keep this file ASCII-only.
    The 4th part (build date) is added by the build itself - never written here.

.PARAMETER Version
    Explicit major.minor.patch. Cannot be combined with -Bump.
.PARAMETER Bump
    major, minor or patch. Cannot be combined with -Version.
.PARAMETER DryRun
    Show what would change without writing.

.EXAMPLE
    .\bump-version.ps1 -Bump patch -DryRun
#>
param(
    [string]$Version = "",
    [ValidateSet("major", "minor", "patch")]
    [string]$Bump = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ---- CONFIG -------------------------------------------------------------
$PropsRel = '200.Source\Directory.Build.props'
# -------------------------------------------------------------------------

$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$propsPath = Join-Path $repoRoot $PropsRel
# Trap: Windows PowerShell 5.1 Get-Content/Set-Content default to ANSI and corrupt UTF-8 comments in the props file.
$utf8 = New-Object System.Text.UTF8Encoding $false

if ($Version -and $Bump) { throw "Specify either -Version or -Bump, not both." }
if (-not $Version -and -not $Bump) { throw "Specify either -Version '1.2.3' or -Bump (major|minor|patch)." }
if (-not (Test-Path $propsPath)) { throw "Directory.Build.props not found at $propsPath" }

$content = [System.IO.File]::ReadAllText($propsPath, $utf8)
if ($content -notmatch '<VersionPrefix[^>]*>(\d+\.\d+\.\d+)</VersionPrefix>') { throw "Could not read VersionPrefix from $PropsRel" }
$oldVersion = $Matches[1]

if ($Bump) {
    $parts = $oldVersion.Split('.')
    switch ($Bump) {
        "major" { $parts[0] = [int]$parts[0] + 1; $parts[1] = 0; $parts[2] = 0 }
        "minor" { $parts[1] = [int]$parts[1] + 1; $parts[2] = 0 }
        "patch" { $parts[2] = [int]$parts[2] + 1 }
    }
    $newVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
}
else {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch. Received: '$Version'." }
    $newVersion = $Version
}

if ($newVersion -eq $oldVersion) {
    Write-Host "Version is already $oldVersion - nothing to do." -ForegroundColor Yellow
    exit 0
}

$label = if ($DryRun) { "[DRY RUN] " } else { "" }
Write-Host "${label}VersionPrefix: $oldVersion -> $newVersion" -ForegroundColor Cyan

if (-not $DryRun) {
    $content = $content -replace "(<VersionPrefix[^>]*>)$([regex]::Escape($oldVersion))(</VersionPrefix>)", "`${1}$newVersion`${2}"
    [System.IO.File]::WriteAllText($propsPath, $content, $utf8)
    Write-Host "Wrote $PropsRel" -ForegroundColor Green
}
