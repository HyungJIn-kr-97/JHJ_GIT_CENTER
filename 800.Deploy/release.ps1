<#
.SYNOPSIS
    한 번에 배포한다: (선택) 버전 올리기 -> 커밋 -> 빌드 -> push -> GitHub Release -> 옛 날짜판 정리.

.DESCRIPTION
    릴리스 이름은 <배포 버전>.<날짜 yyyyMMdd> 다(예: v1.0.1.20261007).
    질문마다 Enter 만 치면 기본값으로 간다 - 패치 자리가 하나 올라가고, 날짜는 오늘이다. 안 올리려면 0 을 친다.
    밖으로 나가는 단계(커밋 · push · 릴리스 · 삭제)는 실행 전에 묻는다. Release.bat 으로 실행한다.
    함정: 이 파일은 한글이 있어 반드시 UTF-8 BOM 으로 저장한다 - BOM 이 없으면 Windows PowerShell 5.1 이 깨뜨려 읽는다.
#>

param(
    [string]$Repository = "HyungJIn-kr-97/JHJ_GIT_CENTER",
    # 계약: 아래 넷은 앱의 「배포하기」 화면이 넘긴다(Core AppDeploy) - 손으로 돌릴 때는 비워 두면 지금처럼 묻는다
    [ValidateSet('', 'patch', 'minor', 'major', 'none')]
    [string]$Bump = '',
    [string]$Date = '',
    # 계약: 묻지 않고 기본값으로 간다 - 단, 같은 이름의 릴리스를 지우는 것만은 -Overwrite 가 있어야 한다
    [switch]$Unattended,
    [switch]$Overwrite,
    # 계약: 저장소에서 띄운 앱을 내리지 않는다 - 빌드를 bin\deploy 로 돌려 잠금을 피한다(publish.ps1 -KeepApp)
    [switch]$KeepApp
)

# 함정: "Stop" 이면 Windows PowerShell 5.1 이 git/gh 의 진행 표시(stderr)를 오류로 보고 멈춘다 - 종료 코드로 판정한다
$ErrorActionPreference = "Continue"
$env:GIT_TERMINAL_PROMPT = "0"

$repo  = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# 계약: 이 저장소가 만드는 실행 파일 이름 - 빌드 전에 내릴 대상이다
$appProcessNames = @('JHJ_Git-Center')
# 계약: publish.ps1 의 $PackagePrefix · 앱의 AppUpdate.PackagePrefix 와 같아야 한다
$package = 'JHJ_Git-Center'
$props = Join-Path $repo "200.Source\Directory.Build.props"
$out   = Join-Path $PSScriptRoot "publish"

function Fail {
    param([string]$Message)
    Write-Host ""
    Write-Host "[중단] $Message" -ForegroundColor Red
    exit 1
}

function Ask {
    param([string]$Question, [string]$Default)
    if ($Unattended) { return $Default }
    $answer = Read-Host $Question
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim().ToLowerInvariant()
}

# 계약: 기본은 Enter = 예 — 되돌릴 수 없는 질문(기존 릴리스 삭제)만 -DefaultNo 로 Enter = 아니오
function Confirm {
    param([string]$Question, [switch]$DefaultNo)
    if ($DefaultNo) {
        return (Ask "$Question (y = 예 / Enter = 아니오)" "n") -match '^(y|yes|ㅛ|예|네)$'
    }
    return (Ask "$Question (Enter = 예 / n = 아니오)" "y") -notmatch '^(n|no|ㅜ|아니오|아니요)$'
}

function Get-Prefix {
    $text = [System.IO.File]::ReadAllText($props)
    if ($text -match '<VersionPrefix[^>]*>(\d+\.\d+\.\d+)</VersionPrefix>') { return $Matches[1] }
    Fail "Directory.Build.props 에서 VersionPrefix 를 읽지 못했습니다."
}

function Step {
    param([string]$Title)
    Write-Host ""
    Write-Host "== $Title" -ForegroundColor Cyan
}

# --- 0. 준비 ----------------------------------------------------------------
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Fail "gh 로그인이 안 되어 있습니다. 먼저 실행: gh auth login --web" }

$branch = (git -C $repo rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") { Fail "지금 브랜치가 '$branch' 입니다. main 에서 실행해 주십시오." }

# --- 1. 버전 ----------------------------------------------------------------
Step "1/5 버전"
$current = Get-Prefix
$parts   = $current.Split('.')
$patch   = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
$minor   = "$($parts[0]).$([int]$parts[1] + 1).0"
$major   = "$([int]$parts[0] + 1).0.0"

$sample  = Get-Date -Format "yyyyMMdd"

Write-Host "지금 버전: $current"
Write-Host ""
Write-Host "버전을 올립니다. Enter 는 패치 자리를 올립니다 (날짜는 다음 단계에서 바꿀 수 있습니다)."
Write-Host ""
Write-Host "  선택    뜻                         올라가는 릴리스"
Write-Host "  -----   ------------------------   ----------------------"
Write-Host "  Enter   고치기만 했음 (패치)       v$patch.$sample"
Write-Host "  2       기능을 더했음 (마이너)     v$minor.$sample"
Write-Host "  3       호환이 깨짐 (메이저)       v$major.$sample"
Write-Host "  0       안 올림, 날짜만 바뀜       v$current.$sample"
Write-Host ""
# 함정: PowerShell 변수는 대소문자를 안 가린다 - $bump 로 받으면 매개변수 $Bump 의 ValidateSet 에 걸려 죽는다
$bumpChoice = switch ($Bump) {
    'patch' { "1" }
    'minor' { "2" }
    'major' { "3" }
    'none'  { "0" }
    default { Ask "선택 (Enter · 2 · 3 · 0)" "1" }
}

$bumpArgs = $null
switch ($bumpChoice) {
    "0" { }
    "1" { $bumpArgs = @{ Bump = "patch" } }  # 계약: 인자 이름은 bump-version.ps1 의 -Bump 다
    "2" { $bumpArgs = @{ Bump = "minor" } }
    "3" { $bumpArgs = @{ Bump = "major" } }
    default { Fail "'$bumpChoice' 은(는) 고를 수 없습니다. Enter · 2 · 3 · 0 중 하나입니다." }
}

if ($bumpArgs) {
    try { & (Join-Path $PSScriptRoot "bump-version.ps1") @bumpArgs | Out-Null }
    catch { Fail "버전 올리기 실패: $($_.Exception.Message)" }
}
$version = Get-Prefix

# --- 2. 날짜 ----------------------------------------------------------------
Step "2/5 날짜"
$today = Get-Date -Format "yyyyMMdd"
Write-Host "배포 날짜를 정합니다."
Write-Host ""
Write-Host "  입력        올라가는 릴리스"
Write-Host "  ---------   ----------------------"
Write-Host "  Enter       v$version.$today  (오늘)"
Write-Host "  1007        v$version.$(Get-Date -Format 'yyyy')1007"
Write-Host "  20261007    v$version.20261007"
Write-Host ""
$date = if ($Date) { $Date } else { Ask "날짜 (Enter = 오늘)" $today }
if ($date -match '^\d{4}$') { $date = (Get-Date -Format "yyyy") + $date }
if ($date -notmatch '^\d{8}$') { Fail "'$date' 은(는) 날짜가 아닙니다. 1007 또는 20261007 처럼 적습니다." }
try { [void][datetime]::ParseExact($date, "yyyyMMdd", $null) }
catch { Fail "'$date' 은(는) 없는 날짜입니다." }

$release = "$version.$date"
$tag     = "v$release"

Write-Host ""
Write-Host "배포할 릴리스: $tag" -ForegroundColor Yellow
if (-not (Confirm "이대로 GitHub 에 배포할까요?")) { Fail "취소했습니다. 아무것도 올라가지 않았습니다." }

# --- 3. 커밋 ----------------------------------------------------------------
Step "3/5 소스 커밋"
$dirty = @(git -C $repo status --porcelain)
if ($dirty.Count -gt 0) {
    $dirty | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    if (-not (Confirm "위 변경 $($dirty.Count)개를 '$tag' 이름으로 커밋할까요?")) {
        Fail "커밋하지 않은 변경이 있습니다. 올라가는 프로그램과 소스가 같아야 하므로 먼저 커밋해 주십시오."
    }
    git -C $repo add -A
    git -C $repo commit -q -m $tag
    if ($LASTEXITCODE -ne 0) { Fail "git commit 실패." }
    Write-Host "커밋했습니다: $(git -C $repo log --oneline -1)"
}
else {
    Write-Host "커밋할 변경이 없습니다: $(git -C $repo log --oneline -1)"
}

# --- 4. 빌드 ----------------------------------------------------------------
Step "4/5 빌드"
# 왜: 이 저장소에서 띄운 앱이 bin\ 이나 publish\ 를 잠가 빌드가 깨진다 - 설치본(%LOCALAPPDATA%)은 저장소 밖이라 안 걸린다
# 함정: 200.Source 만 보면 publish\ 산출물로 확인해 본 프로세스를 놓쳐 GenerateBundle 이 UnauthorizedAccessException 으로 죽는다
# 함정: 이름으로 먼저 거른다 - 전체 프로세스를 훑으면 경로 비교가 빗나갔을 때 남의 프로세스를 내린다
$devApp = Get-Process $appProcessNames -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $repo -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase) }
$devAppWasRunning = $null -ne $devApp
if (-not $KeepApp) { $devApp | Stop-Process -Force }

try { & (Join-Path $PSScriptRoot "publish.ps1") -Platform x64 -BuildDate $date -KeepApp:$KeepApp }
catch { Fail "빌드 실패: $($_.Exception.Message)" }
if ($LASTEXITCODE -ne 0) { Fail "빌드 실패. 위 메시지를 확인해 주십시오." }

$assets = @(
    (Join-Path $out "$package-win-x64-$tag.zip"),
    (Join-Path $out "$package-win-x64-$tag.zip.sha256"),
    (Join-Path $out "${package}_${release}_win-x64.exe"),
    # 계약: 고정 이름 설치 파일 - 앱의 「설치 파일 주소」(releases/latest/download/...)와 winget 이 이 이름으로 받는다
    (Join-Path $out "${package}_Setup.exe")
)
foreach ($asset in $assets) {
    if (-not (Test-Path $asset)) { Fail "빌드 결과물이 없습니다: $asset" }
}

# --- 5. push + 릴리스 -------------------------------------------------------
Step "5/5 GitHub 에 올리기"
gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "$tag 릴리스가 GitHub 에 이미 있습니다." -ForegroundColor Yellow
    $replace = if ($Unattended) { [bool]$Overwrite } else { Confirm "지우고 지금 만든 것으로 다시 올릴까요?" -DefaultNo }
    if (-not $replace) { Fail "취소했습니다. 기존 릴리스는 그대로입니다." }
    gh release delete $tag --repo $Repository --cleanup-tag --yes
    if ($LASTEXITCODE -ne 0) { Fail "기존 릴리스를 지우지 못했습니다." }
}

git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { Fail "git push 실패." }

gh release create $tag $assets --repo $Repository --target main --title $tag --generate-notes --latest
if ($LASTEXITCODE -ne 0) { Fail "릴리스 만들기 실패." }

# --- 옛 날짜판 정리 ---------------------------------------------------------
Step "옛 릴리스 정리"
$pruneScript = Join-Path $PSScriptRoot "prune-releases.ps1"
try {
    $plan  = @(& $pruneScript -Repository $Repository 6>&1 | ForEach-Object { "$_" })
    $stale = @($plan | Where-Object { $_ -match '^would delete (.+)$' } | ForEach-Object { $Matches[1] })
    if ($stale.Count -eq 0) {
        Write-Host "지울 옛 릴리스가 없습니다."
    }
    else {
        Write-Host "같은 버전의 옛 릴리스:"
        $stale | ForEach-Object { Write-Host "  $_" }
        $answer = Ask "지울까요? (Enter = 지움 / n = 남김)" "y"
        if ($answer -match '^(y|yes|ㅛ|예|네)$') {
            & $pruneScript -Repository $Repository -Apply | Out-Null
            Write-Host "지웠습니다."
        }
        else {
            Write-Host "남겼습니다. 나중에 지우려면 prune-releases.ps1 -Apply 를 실행합니다."
        }
    }
}
catch {
    Write-Host "정리 단계에서 오류가 났습니다(릴리스 자체는 올라갔습니다): $($_.Exception.Message)" -ForegroundColor Yellow
}

# --- 로컬 옛 꾸러미 정리 ----------------------------------------------------
# 왜: publish\ 에 지난 릴리스의 zip · exe 가 쌓인다 - 이번 것만 남긴다(GitHub 에 올라간 것과는 무관하다)
$oldFiles = @(Get-ChildItem $out -File | Where-Object { $_.Name -match "^$package(-win-.+-v|_)\d" -and $_.Name -notlike "*$release*" })
if ($oldFiles.Count -gt 0) {
    $oldFiles | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "로컬 publish 폴더의 옛 꾸러미 $($oldFiles.Count)개를 지웠습니다."
}

# --- 끝 ---------------------------------------------------------------------
Write-Host ""
Write-Host "[완료] $tag 배포했습니다." -ForegroundColor Green
Write-Host "  릴리스 페이지 : https://github.com/$Repository/releases/tag/$tag"
$devExe = "$repo\200.Source\GitCenter.App\bin\Release\net10.0-windows\win-x64\JHJ_Git-Center.exe"
if ($KeepApp) {
    Write-Host "  앱을 내리지 않고 배포했습니다 - 지금 떠 있는 앱은 배포 전 빌드 그대로입니다."
}
elseif (Test-Path $devExe) {
    # 왜: 배포하면 빌드 때문에 앱이 꺼진 채 남는다 — 떠 있었든 아니든 끝나면 다시 띄운다
    # 함정: explorer.exe 로 띄운다 - 이 콘솔의 자식이 되면 창을 닫을 때 같이 죽는다
    Start-Process explorer.exe $devExe
    Write-Host "  개발 빌드 앱을 띄웠습니다: $devExe"
}
else {
    Write-Host "  개발 빌드가 없어 앱을 띄우지 못했습니다: $devExe"
}
exit 0
