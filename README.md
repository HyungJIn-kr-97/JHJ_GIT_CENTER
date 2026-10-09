# JHJ_GIT_CENTER — Git 통합 관리 센터

여러 git 저장소를 **한 화면에서 보고, 골라서 한꺼번에 받고 올리는** Windows 데스크톱 앱입니다(WPF · `net10.0-windows`).
GitHub Desktop 에 등록된 저장소를 그대로 읽어 오고, 폴더 스캔·GitHub 계정·사용자 명령으로도 목록을 채웁니다.

- 실행 파일: `JHJ_Git-Center.exe` · 표시 이름: **Git 통합 관리 센터**
- 자료 폴더: `%LOCALAPPDATA%\JHJ_Git-Center\` (`settings.json` · `logs\`)
- 트레이 상주 · 전역 단축키 기본 `Ctrl+Alt+G`

---

## 1. 무엇을 하나

| 하고 싶은 것 | 화면에서 |
|---|---|
| 내 저장소가 지금 어떤 상태인지 한눈에 | 왼쪽 목록 — 브랜치 · 앞섬/뒤처짐 · 변경 수 · stash · 충돌이 줄마다 보입니다 |
| 변경 있는 것만 골라 보기 | 머리띠의 **「변경된 것만」** · 검색칸(이름·묶음·경로) |
| 여러 저장소를 한꺼번에 받기 | **전체 Fetch** → **main 받기** → **현재로 병합** |
| 체크아웃을 바꾸지 않고 main 만 갱신 | **main 받기** — 빨리감기로만 당기고, 갈라져 있으면 건드리지 않습니다 |
| 모든 추적 브랜치 갱신 | **모든 추적 브랜치 갱신** — 뒤처진 것만, 갈라진 것은 건너뜁니다 |
| 내 코드를 한꺼번에 올리기 | **커밋 + 올리기** — 메시지 한 줄로 고른 저장소를 모두 |
| 충돌이 어디서 났는지 보기 | 가운데 **「충돌」 탭** — 파일 · 줄 번호 · 양쪽 내용이 색으로 갈립니다(충돌이 있으면 저절로 열립니다) |
| 병합을 되돌리기 | **병합 되돌리기** — `merge --abort` · `rebase --abort` 를 상태에 맞게 |

### 하지 않는 것

⛔ **force push · `reset --hard` · `clean` 은 어떤 설정으로도 실행하지 않습니다.** push 가 거부되면
「먼저 받아서 합쳐 주십시오」로 알릴 뿐 강제로 밀지 않습니다. 원격을 건드리는 일은 **사용자가 단추를
누를 때만** 일어납니다 — 상태 읽기는 로컬만 봅니다(자동 Fetch 는 기본 꺼짐).

---

## 2. 저장소 목록은 어디서 오나

설정 **「목록 출처」** 에서 켜고 끕니다. 여러 출처가 같은 저장소를 찾으면 **한 줄로 합쳐지고**, 줄에
어느 출처에서 왔는지 함께 보입니다.

| 출처 | 무엇을 읽나 |
|---|---|
| **루트 폴더 스캔** | 루트 아래를 깊이만큼 훑어 `.git` 을 찾습니다. 저장소를 찾으면 그 아래로는 안 내려갑니다 |
| **GitHub Desktop** | Desktop 이 들고 있는 저장소 목록을 그대로 — 아래 §2-1 |
| **GitHub 계정(gh)** | `gh repo list` 로 계정·조직 저장소. 아직 clone 하지 않은 것도 목록에 둘 수 있습니다 |
| **명령 출처** | 사용자가 정한 명령의 출력을 한 줄에 경로 하나로 읽습니다(예: `cmd /c dir /b /s /ad .git`) |
| **수동 추가** | 폴더를 직접 골라 더합니다. 숨긴 저장소는 설정에서 되살립니다 |

### 2-1. GitHub Desktop 목록을 읽는 방법

Desktop 은 저장소 목록을 **IndexedDB(LevelDB)** 에 두고 내보내는 파일이 없습니다. 게다가 그 블록은
**snappy 로 눌려 있어** 파일을 그냥 뒤지면 경로 문자열이 하나도 안 나옵니다.

그래서 이 앱은 `.ldb` 의 테이블 구조(footer → index block → data block)를 따라가 블록을 풀고,
경로처럼 생긴 문자열을 주운 뒤 **실제로 `.git` 이 있는 것만** 목록에 올립니다. 레코드 형식(V8 직렬화)까지
풀지 않으므로 **검증이 유일한 거름망**입니다 — 구현은
[LevelDbBlockScanner.cs](200.Source/GitCenter.Infrastructure/GitHubDesktop/LevelDbBlockScanner.cs) ·
[Snappy.cs](200.Source/GitCenter.Infrastructure/GitHubDesktop/Snappy.cs) ·
[GitHubDesktopReader.cs](200.Source/GitCenter.Infrastructure/GitHubDesktop/GitHubDesktopReader.cs).

- **Desktop 을 켜 둔 채로도 읽습니다**(읽기 전용·공유 열기).
- Desktop 이 저장소를 아직 한 번도 안 열었으면 목록이 비어 있을 수 있습니다.
- 자료 폴더 기본값은 `%APPDATA%\GitHub Desktop` 이고, 다른 곳에 깔았으면 설정에 적습니다.

### 2-2. 끊어진 저장소를 다시 잇기

경로가 사라진 저장소는 **같은 이름으로 루트를 다시 뒤져 경로를 고칩니다**(설정 「동기화」의
`경로가 사라진 저장소를 … 다시 찾아 잇습니다`). 못 찾으면 줄에 `경로 없음` 으로 남고, 일괄 작업에서는
git 을 부르지 않고 건너뜁니다.

---

## 3. 만들기

```powershell
dotnet build 200.Source\GitCenter.slnx -c Release --nologo
```

```powershell
& "800.Deploy\Build-Exe.bat"
```

- 결과는 `800.Deploy\publish\JHJ_Git-Center_<버전>.<빌드날짜>_win-x64.exe` (git 제외 폴더).
- `800.Deploy\publish.ps1 -Platform arm64` 로 arm64 도 만듭니다.
- ⚠️ 빌드 전에 **이 폴더에서 띄운** `JHJ_Git-Center` 를 종료해 주십시오 — `bin\` 이 잠깁니다.

### 버전 체계

`Directory.Build.props` 의 `VersionPrefix`(major.minor.patch) **세 자리만** 사람이 올리고,
네 번째 자리(빌드 날짜)는 빌드가 붙입니다. `800.Deploy\bump-version.ps1 -Part patch`.

⛔ **Agent 는 버전을 스스로 올리지 않습니다** — 어느 자리를 올릴지 먼저 묻습니다.

---

## 4. 구조

| 프로젝트 | 하는 일 |
|---|---|
| `GitCenter.Core` | 모델만 — 저장소·상태·브랜치·충돌·작업 결과 |
| `GitCenter.Application` | 추상화(`IGitService`·`IRepoSource`·`ISettingsStore`)와 순수 로직(목록 합치기 · 충돌 파싱 · 일괄 실행) |
| `GitCenter.Infrastructure` | git 실행기 · porcelain 파싱 · 출처 구현 · GitHub Desktop 읽기 · 설정 저장 |
| `GitCenter.App` | WPF 화면 · 트레이 · 단축키 · 테마 · 언어 |

화면은 **3단**입니다 — 왼쪽 저장소 목록(고르기) · 가운데 상세(변경 파일 · 브랜치 · 충돌) ·
오른쪽 작업 로그. 아래 띠에 일괄 작업 단추와 커밋 메시지가 있습니다.

---

## 5. 설정

`%LOCALAPPDATA%\JHJ_Git-Center\settings.json` 한 파일입니다. 설정 창은 네 묶음입니다.

| 묶음 | 들어 있는 것 |
|---|---|
| 목록 출처 | 루트·깊이 · GitHub Desktop · gh 계정·조직 · 명령 출처 · 수동 추가 · 숨긴 저장소 |
| 동기화 | 목록 다시 훑기(분) · 상태 다시 읽기(분) · 자동 Fetch(분, 기본 0) · 끊어진 저장소 다시 잇기 |
| git 동작 | 원격 이름 · 동시 실행 수 · 제한 시간 · `--ff-only` · 자동 stash · 커밋 메시지 기본값 |
| 화면 | 명암(system·light·dark) · 팔레트 5종 · 언어(ko·en) · 단축키 · 로그인 시 실행 · 트레이로 보내기 |

- 「적용」을 눌러야 저장됩니다. 묶음마다 **기본값으로** 되돌릴 수 있고, 루트·수동 추가·숨김은
  「전부 되돌리기」에서도 지우지 않습니다(사람이 손으로 모은 값입니다).
- 아이콘은 JHJ 공통 규격입니다 — 팔레트 `violet` · 작은 판 `branch`(트레이에 함께 상주하는
  `JHJ_AI_USAGE`(bull)·`JHJ_OPS`(emerald)·`JHJ_VPN`(navy)과 색·모양으로 갈립니다).
