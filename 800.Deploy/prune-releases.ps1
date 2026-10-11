<#
.SYNOPSIS
    Keeps only the newest dated release per version (major.minor.patch) on GitHub.

.DESCRIPTION
    Release tags look like v1.0.0.20261006. For each major.minor.patch the newest date is kept
    and every older date is deleted together with its tag. Old three-part tags (v1.0.3) count as
    the oldest build of that version.
    Dry run by default: it only prints what would be deleted. Pass -Apply to delete.
    Deleting a release cannot be undone - run it only when the user asked for it.
    Keep this file ASCII-only: Windows PowerShell 5.1 misparses BOM-less UTF-8.

.EXAMPLE
    .\prune-releases.ps1
    Lists the releases that would be deleted.

.EXAMPLE
    .\prune-releases.ps1 -Apply
    Deletes them.
#>

param(
    [string]$Repository = "HyungJIn-kr-97/JHJ_GIT_CENTER",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

$tags = gh release list --repo $Repository --limit 200 --json tagName | ConvertFrom-Json | ForEach-Object { $_.tagName }
if ($LASTEXITCODE -ne 0) { throw "gh release list failed" }

$parsed = foreach ($tag in $tags) {
    if ($tag -match '^v?(\d+\.\d+\.\d+)(?:\.(\d{8}))?$') {
        $date = 0
        if ($Matches[2]) { $date = [int]$Matches[2] }
        [pscustomobject]@{ Tag = $tag; Version = $Matches[1]; Date = $date }
    }
}

$stale = @()
foreach ($group in ($parsed | Group-Object Version)) {
    $ordered = @($group.Group | Sort-Object Date -Descending)
    Write-Host ("keep   {0}" -f $ordered[0].Tag) -ForegroundColor Green
    if ($ordered.Count -gt 1) { $stale += $ordered[1..($ordered.Count - 1)] }
}

foreach ($item in $stale) {
    if ($Apply) {
        gh release delete $item.Tag --repo $Repository --cleanup-tag --yes
        if ($LASTEXITCODE -ne 0) { throw "gh release delete failed: $($item.Tag)" }
        Write-Host ("deleted {0}" -f $item.Tag) -ForegroundColor Yellow
    }
    else {
        Write-Host ("would delete {0}" -f $item.Tag) -ForegroundColor Yellow
    }
}

if (-not $Apply -and $stale.Count -gt 0) {
    Write-Host ""
    Write-Host "Dry run. Re-run with -Apply to delete." -ForegroundColor Cyan
}
