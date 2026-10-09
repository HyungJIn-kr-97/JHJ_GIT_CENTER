# Raise VersionPrefix in 200.Source/Directory.Build.props
# Contract: agents never run this on their own - ask which digit to raise first
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
$props = Join-Path (Split-Path -Parent $PSScriptRoot) '200.Source\Directory.Build.props'
$text = Get-Content $props -Raw
if ($text -notmatch '<VersionPrefix Condition="[^"]*">([0-9]+)\.([0-9]+)\.([0-9]+)</VersionPrefix>') {
    throw 'VersionPrefix not found'
}

$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}

$next = "$major.$minor.$patch"
$text = $text -replace '(<VersionPrefix Condition="[^"]*">)[0-9]+\.[0-9]+\.[0-9]+(</VersionPrefix>)', "`${1}$next`${2}"
Set-Content $props $text -NoNewline
Write-Host "VersionPrefix -> $next"
