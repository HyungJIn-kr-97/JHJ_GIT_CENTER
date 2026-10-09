# JHJ_Git-Center publish - single-file self-contained exe
# Contract: output name is JHJ_Git-Center_<version>_win-<arch>.exe
# Trap: ASCII only - Windows PowerShell 5.1 breaks on UTF-8 without BOM
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root '200.Source\GitCenter.App\GitCenter.App.csproj'
$outDir = Join-Path $PSScriptRoot "publish\win-$Platform"

$props = Join-Path $root '200.Source\Directory.Build.props'
$version = ([xml](Get-Content $props)).Project.PropertyGroup.VersionPrefix.'#text'
if (-not $version) { $version = ([xml](Get-Content $props)).Project.PropertyGroup.VersionPrefix }
$stamp = Get-Date -Format 'yyyyMMdd'

Write-Host "publishing JHJ_Git-Center $version.$stamp win-$Platform"

& dotnet publish $project -c $Configuration -r "win-$Platform" --self-contained true -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $outDir 'JHJ_Git-Center.exe'
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }

$named = Join-Path $PSScriptRoot "publish\JHJ_Git-Center_${version}.${stamp}_win-$Platform.exe"
Copy-Item $exe $named -Force
(Get-FileHash $named -Algorithm SHA256).Hash | Set-Content "$named.sha256" -Encoding ascii

Write-Host "done: $named"
