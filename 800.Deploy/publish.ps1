<#
.SYNOPSIS
    Publishes JHJ_Git-Center as a self-contained single-file exe plus the auto-update zip.
    Copied from JHJ_CS_CORE\800.Deploy\_templates\publish.ps1 - only the CONFIG block differs. Keep this file ASCII-only.

.PARAMETER Version
    major.minor.patch. Defaults to VersionPrefix in Directory.Build.props.
.PARAMETER Platform
    x64, arm64 or all. Defaults to x64.
.PARAMETER Configuration
    Release or Debug. Defaults to Release.
.PARAMETER BuildDate
    yyyyMMdd used in the release name and inside the exe. Defaults to today.

.EXAMPLE
    .\publish.ps1 -Platform x64
#>
param(
    [string]$Version = "",
    [ValidateSet("x64", "arm64", "all")]
    [string]$Platform = "x64",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$BuildDate = ""
)

$ErrorActionPreference = "Stop"

# ---- CONFIG: the only part an app edits ---------------------------------
# Contract: $AppExe equals the csproj AssemblyName and JhjApp.Name (JHJ_<Program-Name>).
$AppExe        = 'JHJ_Git-Center'
$ProjectRel    = '200.Source\GitCenter.App\GitCenter.App.csproj'
$PropsRel      = '200.Source\Directory.Build.props'
# Extra exes published into the SAME folder (e.g. a Windows service next to its manager UI). Empty for most apps.
$ExtraProjects = @()
# Contract: must equal UpdateOptions.PackagePrefix in the app. Empty = the app has no auto-update, so no zip is made.
$PackagePrefix = 'JHJ_Git-Center'
# Files copied next to the exe, as 'source relative to repo root=name in output'. Example: 'LICENSE=LICENSE.txt'
$ExtraFiles    = @()
# Contract: must equal AppUpdate.SetupAssetName in the app - the fixed name behind releases/latest/download/<name>. Empty = none.
$SetupAsset    = 'JHJ_Git-Center_Setup.exe'
# -------------------------------------------------------------------------

if ("$AppExe$ProjectRel" -match '[<>]') { throw "publish.ps1 is still the template - fill in the CONFIG block first." }

$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot $ProjectRel
$outputBase  = Join-Path $PSScriptRoot "publish"

if ([string]::IsNullOrWhiteSpace($Version)) {
    # Trap: VersionPrefix may carry a Condition attribute, so [xml] access yields an XmlElement - read it with a regex.
    $propsText = [System.IO.File]::ReadAllText((Join-Path $repoRoot $PropsRel))
    if ($propsText -notmatch '<VersionPrefix[^>]*>(\d+\.\d+\.\d+)</VersionPrefix>') { throw "Could not read VersionPrefix from $PropsRel" }
    $Version = $Matches[1]
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch. Received: '$Version'." }

# Contract: release name = <VersionPrefix>.<build date>, and the same date goes into the exe so the file name and the app agree.
if ([string]::IsNullOrWhiteSpace($BuildDate)) { $BuildDate = Get-Date -Format "yyyyMMdd" }
if ($BuildDate -notmatch '^\d{8}$') { throw "BuildDate must be yyyyMMdd. Received: '$BuildDate'." }
$Release = "$Version.$BuildDate"

# Why: Get-FileHash fails to auto-load when Windows PowerShell inherits a PowerShell 7 PSModulePath - hash via .NET.
# Contract: file content is '<hash>  <file name>' - the format GithubUpdateService reads.
function Write-Checksum {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { $hash = ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    Set-Content -Path "$Path.sha256" -Value "$hash  $(Split-Path -Path $Path -Leaf)" -Encoding Ascii
}

# Trap: an instance started from this repo locks bin\ and publish\ and breaks the build - stop it first.
# Trap: filter by NAME first - scanning every process risks killing someone else's when the path test misfires.
Get-Process $AppExe -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

$platforms = if ($Platform -eq "all") { @("win-x64", "win-arm64") } else { @("win-$Platform") }
Write-Host "Publishing $AppExe v$Release ($Configuration, $($platforms -join ', '))" -ForegroundColor Cyan

foreach ($rid in $platforms) {
    $outputPath = Join-Path $outputBase $rid

    # Why: a renamed exe from an older build would otherwise stay here and ship inside the zip or the install copy.
    if (Test-Path $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }

    foreach ($proj in @($projectPath) + @($ExtraProjects | ForEach-Object { Join-Path $repoRoot $_ })) {
        dotnet publish $proj `
            --configuration $Configuration `
            --runtime $rid `
            --self-contained true `
            --output $outputPath `
            --nologo `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=embedded `
            -p:VersionPrefix=$Version `
            -p:Version=$Version `
            -p:BuildDate=$BuildDate
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to publish $proj for $rid" -ForegroundColor Red
            exit 1
        }
    }

    foreach ($pair in $ExtraFiles) {
        $src, $dst = $pair -split '=', 2
        Copy-Item -LiteralPath (Join-Path $repoRoot $src) -Destination (Join-Path $outputPath $dst) -Force
    }

    # Contract: human-facing copy is JHJ_<Program-Name>_<version>.<date>_<rid>.exe - same shape in every JHJ app.
    $exeAsset = Join-Path $outputBase "${AppExe}_${Release}_$rid.exe"
    Copy-Item -LiteralPath (Join-Path $outputPath "$AppExe.exe") -Destination $exeAsset -Force
    Write-Checksum -Path $exeAsset
    Write-Host "Created: $exeAsset" -ForegroundColor Green

    # Why: a versioned name cannot be linked once and stay valid - the same exe also ships under one fixed name (x64 only).
    if ($SetupAsset -and $rid -eq 'win-x64') {
        $setupPath = Join-Path $outputBase $SetupAsset
        Copy-Item -LiteralPath $exeAsset -Destination $setupPath -Force
        Write-Checksum -Path $setupPath
        Write-Host "Created: $setupPath" -ForegroundColor Green
    }

    if ($PackagePrefix) {
        # Contract: <prefix>-<rid>-v<release>.zip - installed builds look for exactly this name. Never rename casually.
        $zipPath = Join-Path $outputBase "$PackagePrefix-$rid-v$Release.zip"
        if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath }
        Compress-Archive -Path "$outputPath\*" -DestinationPath $zipPath
        Write-Checksum -Path $zipPath
        Write-Host "Created: $zipPath" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Done. Release tag would be: v$Release" -ForegroundColor Cyan
Get-ChildItem $outputBase -File | Where-Object { $_.Extension -in ".zip", ".exe" } | Sort-Object LastWriteTime -Descending |
    ForEach-Object { Write-Host ("  {0} - {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Gray }
