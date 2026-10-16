# AGENTS.md — JHJ_GIT_CENTER 에이전트 가이드

워크스페이스 규약은 `JHJ\AGENTS.md`(정본 `JHJ_DEV/000.AGENTS_MD`)가 먼저다. 고객사는 늘 **`JHJ`**, 제목은 `[JHJ] <Role> - <요약>`.
사람용 설명(무엇을 하는 앱인가 · 빌드 · 설정)은 [README.md](README.md) 에 있다 — 여기로 복사하지 않는다.

## 1. 이 repo 의 정체

| 폴더 | 역할 |
|---|---|
| `200.Source/` | 솔루션·프로젝트 4개(Core · Application · Infrastructure · App) — 고치는 곳은 여기뿐 |
| `300.Docs/` | 계약 문서 — `GitHub-Desktop-목록-읽기.md`(LevelDB·snappy 를 푸는 근거와 한계). 소스에는 경로 한 줄만 둔다 |
| `800.Deploy/` | `Build-Exe.bat` · `publish.ps1` · `bump-version.ps1` · `Release.bat`(`release.ps1` — 커밋·빌드·push·GitHub Release) · `prune-releases.ps1`. 산출물 `publish/` 는 git 제외 |

여러 git 저장소를 한 화면에서 다루는 **Windows 데스크톱 앱**(WPF · `net10.0-windows`). 서버 배포 대상이 아니다.
★ **기본 편의 기능(트레이 · 단일 인스턴스 · 테마 · 언어 · 로그 · 설정 저장 · 시작 프로그램 · 단축키 · 외부 도구 열기 · 프로세스 실행)은
`JHJ_CS_CORE` 가 갖는다** — 지도는 `JHJ_CS_CORE/200.Source/100.Core/README.md`,
무엇이 공통이고 무엇이 이 앱 것인지는 `JHJ_CS_CORE/300.Docs/010.공통-전용-기능-구분.md` 다.
⛔ **그 기능을 여기서 다시 구현하지 않는다** — 고칠 일이 생기면 Core 를 고치고 `JHJ_AI_USAGE` 까지 함께 빌드한다.
계약: 지운 이름들(`ThemeManager`·`Loc`·`StartupRegistry`·`ProcessRunner`)은 각 프로젝트의 `GlobalUsings.cs` 가 Core 형식으로 잇고 있다.

## 2. 지켜야 할 것

- ⛔ **되돌릴 수 없는 git 명령을 넣지 않는다** — `push --force`(및 `--force-with-lease`) · `reset --hard` · `clean` · `branch -D`.
  이 앱의 약속이고 설정으로도 열지 않는다. push 가 거부되면 「먼저 받아서 합쳐 주십시오」로 끝낸다.
  계약: 일괄 작업이 충돌·실패로 끝난 저장소는 목록 체크를 자동으로 푼다(`MainViewModel.UncheckStuck`) — 「이 저장소만 고르기」가 다시 체크한다.
  함정: `RepoHealth.NoRemote` 는 아무도 매기지 않는다 — 원격 없는 저장소의 「Push 전」 제외는 `RepoStatus.HasRemote`(`git remote` 한 번)가 한다.
  ★ 계약: 원격이 없는 저장소에 일괄 Fetch·Pull·Merge·Push 를 걸면 git 을 부르지 않고 「건너뜀」으로 센다(`GitService.ExecuteAsync` 의 `NeedsRemote` 선판정) —
  git 원문(`fatal: 'origin' does not appear…`)은 사용자가 못 읽는다(2026-10-12 사용자 지시). 그 결과는 `BulkNextAction.Publish` 로 저장소 줄의 「GitHub 에 올리기」를 깜빡인다.
  계약: Fetch·Pull·Merge 의 git fatal 원문 셋(원격 없음·주소 틀림 · 접속 실패 · 인증 실패)은 `GitService.DescribeFailure` 가 우리말로 바꾼다 — 원문은 `Detail`(「자세히」)이 갖는다.
  ★ 계약: GitHub 계정(`Services/GitHubAccount`)은 **앱을 켤 때 세 출처를 로컬에서 읽어** 제목 띠의 계정 칩(`AccountButton`)에 띄운다 — gh CLI(hosts.yml · 자격 증명 `gh:github.com:*`) → GitHub Desktop(자격 증명 `GitHub - https://api.github.com/*`) → git 자격 증명(`git:https://*` 중 github.com).
  읽기는 `Infrastructure/Accounts/GitHubAccountSources`, 자격 증명은 항목 이름·사용자 이름만 본다(`CredentialStoreReader` — blob 은 손대지 않는다). 설정 「일반」 탭이 같은 객체를 보고, 칩 툴팁·상태 줄에 **출처**를 함께 적는다(JHJ 규약 §4). 원격에는 닿지 않는다.
  계약: `IsLoggedIn` 은 **gh CLI 로그인**뿐이다 — 올리기·공유(`gh api`)가 이것을 본다. 다른 출처에서만 읽힌 계정은 칩에 보이되(`HasAccount`) 그 일은 못 하고, 누르면 gh 로그인 콘솔을 연다. 토큰은 여전히 gh 가 쥔다.
  계약: 로그인은 `gh auth login --web` 콘솔을 띄워 사람이 하고, 끝나면 `gh api user` 로 확인한다. gh 가 없으면 칩이 「gh 설치」가 되고 누르면 **winget 콘솔로 먼저 깐 뒤**(`InstallGhAsync` · `ToolLocator.Invalidate` 가 PATH 를 다시 읽는다) 로그인 콘솔이 이어진다.
  계약: git · gh 의 자리는 `Infrastructure/Tools/ToolLocator` 가 정한다 — PATH 에 없어도 알려진 설치 폴더에서 찾는다. 커밋 작성자(`git config --global user.name·user.email`)도 시작 때 읽어 칩 툴팁·「일반」 탭에 보인다.
  ★ 계약: 「GitHub 에 올리기」(`Views/PublishLayer` · `MainViewModel.Publish.cs`)는 원격이 없는 저장소(`RepoRow.IsLocalOnly`)에만 보이고, `gh repo create --source --remote --push` 한 번으로 **만들기 + 연결 + 첫 Push** 를 한다 — 기본은 비공개(끄면 공개).
  커밋이 없거나 원격이 이미 있으면 gh 를 부르기 전에 막는다. 여는 곳은 셋 — 저장소 줄 단추 · 상세 「작업」 탭 「GitHub 에 새 저장소 만들어 연결」 · 일괄 작업 「원격 없음」 건너뜀의 Nudge.
  계약: 「오류 보고」(상태 줄 · 시스템 작업 로그 탭)는 실패 기록 + 환경·설정 + 이벤트 로그를 복사하고 이슈 양식을 연다 — Git Center 구역은
  `Services/GitCenterDiagnostics` 하나이고 설정 창 「진단 보고서」도 같은 것을 쓴다. 저장소 경로·원격 주소는 `MaskRepo` 로 가린다.
- 계약: **원격에 닿는 일은 사용자가 단추를 눌렀을 때만** 일어난다. 상태 읽기(`status`·`log`·`for-each-ref`)는 로컬만 본다.
  자동 Fetch 는 설정에서 켠 사람만 쓰고 기본은 0(꺼짐)이다.
- 함정: git 인자는 **배열로** 넘긴다(`IGitRunner.RunAsync`). 한 문자열로 이어 붙이면 공백 있는 경로·커밋 메시지가 잘린다.
- 함정: `status --porcelain=v2` 는 「변화 없음」을 공백이 아니라 **`.`** 으로 적는다 — v1 기준으로 세면 모든 줄이 스테이지된 것으로 잡힌다.
- 함정: `for-each-ref` 에서 `refs/remotes/origin/HEAD` 의 짧은 이름은 **`origin`** 이다 — 짧은 이름으로 거르면 브랜치로 둔갑한다.
- 계약: GitHub Desktop 목록은 **추측한 경로를 `.git` 존재로 검증한 것만** 쓴다. 레코드 형식을 풀지 않으므로 검증이 유일한 거름망이다.
- 함정: WPF 목록의 `ItemsSource` 를 비우면 ListBox 가 `SelectedItem` 을 null 로 **되돌려 보낸다** — 거르기·상태 갱신마다 선택이 풀린다.
- 함정: 상세 읽기는 선택 바뀜과 상태 갱신이 겹쳐 두 번 돈다 — 세대 번호(`_detailGeneration`)로 늦게 온 쪽을 버린다.
- 함정: `UseSerilog((context, services, logger) => …)` 는 로거를 **늦게** 만든다. `ILogger<T>` 를 아무도 주입받지 않으면
  끝내 안 만들어져 로그가 통째로 사라진다 — 이 앱은 2인자 overload 로 **코드에서 한 번만** 설정한다(`App.Configure`).
- 계약: XAML 의 색은 전부 `DynamicResource`, 문구는 `{loc:Tr '한국어 원문'}` 이다. 새 문구는 `Localization/LocStrings.cs`(영어)와 `LocStringsZh.cs`(중국어)에 같은 키로 한 줄씩 더한다 — 언어는 영어(기본)·한국어·중국어 셋이다.
  ⚠️ `JHJ_AI_USAGE` 는 **영어가 키**다 — 이 앱은 방향이 반대다. 섞어 쓰지 않는다(`LocCatalog` 가 양쪽을 다 받는다).
  계약: 브러시 키·팔레트 정본은 Core 의 `Theme/JhjTheme.cs` 다. 저장소 상태 색(`BehindBrush`·`AheadBrush`)은
  `App.AddGitBrushes` 가 공통 뜻 색(`InfoBrush`·`WarnBrush`)에 이름만 붙인 것이다.
- ★ 계약: **모양의 정본은 Core `Jhj.Core.Wpf/Theme/CoreStyles.xaml`(공통 — 치수 상수 · 글꼴 · 아이콘 · 글자 · 스크롤바 · 말풍선 · 단추 · 글상자 · 체크 · 고르는 상자 · 탭 · 카드)과 이 앱의 `Styles.xaml`(전용 — 제목 띠 · 창 단추 · 내비 · 알약 토글 · 검색칸 · 목록 줄 · 진행 막대 · 펼침 · 나누개 · 대시보드) 둘이다**(2026-10-12 이사 — 값의 정본은 이 앱 것이었다). 창 XAML 은 거기 정의된 Style·Geometry 만 쓰고 색·크기를 직접 적지 않는다. 공통 구역을 고칠 일은 Core 에서 하고 네 앱을 빌드한다.
  스크롤바·체크박스·콤보박스·말풍선·진행막대는 **템플릿을 새로 씌웠다**(Windows 기본은 어두운 테마에서 밝은 회색으로 남는다).
- 함정: 알약 모서리를 `CornerRadius="999"` 로 주면 WPF 가 가로까지 둥글려 **타원**이 된다 — `Pill`(16)·`PillSm`(9)·`PillXs`(3) 중에서 고른다.
- 함정: 글상자의 흐린 안내 문구(`Tag`)는 **바인딩으로** 켠다 — `ControlTemplate.Triggers` 의 `DataTrigger`(`Text` = "")는 켜지지 않았다.
- 계약: 설정 창은 **탭마다 파일 하나**다(`Views/SettingsTabs/*Tab.xaml`) — 공통 탭 「일반」·「화면」·「아이콘」이 먼저 오고, 세로 금(`TabGroupDivider`) 뒤에 전용 탭 셋(목록 출처 · 동기화 · git 동작)이 온다.
  탭 조각에는 로직을 두지 않는다 — `DataContext` 는 `SettingsWindow` 에서 물려받고, 줄 단추는 `AncestorType=Window` 로 명령을 찾는다. 규격은 `JHJ_CS_CORE/300.Docs/030.설정-창-기본-규격.md`.
  계약: 「일반」 탭의 단축키 줄·진단 보고서는 Core 형식(`HotkeySlotEditor` · `DiagnosticsReportBuilder`)을 물린 것이다 — 판정·등록·가리기를 여기서 다시 짜지 않는다.
  단축키는 줄의 「저장」에서 **즉시 등록**되고 설정 파일 저장은 「적용」이다. 추가 칸은 `Settings.ExtraHotkeys`(1번 칸 `Hotkey` 와 합쳐 넷까지).
  ⛔ 진단 보고서에 저장소 경로·원격 주소·작성자를 넣지 않는다 — 사용자가 GitHub 이슈에 그대로 붙인다(`SettingsViewModel.BuildDiagnostics`).
  함정: 「화면」 탭의 「기본값으로」는 아직 「일반」·「아이콘」 탭의 값(단축키 · 트레이 · 아이콘)까지 되돌린다 — 탭별로 가르는 일이 남았다.
  계약: 창 머리 「앱 공유하기」의 「배포하기」는 Core 의 `AppDeploy` 를 물린 것이다 — 개발 장비 + 저장소 소유자 계정일 때만 보이고,
  `JHJ_DEV\800.Deploy\deploy-history.jsonl` 에 시작 기록을 쓴 뒤에만 배포를 돌린다(030 규격 §1-1). 판정·이력을 여기서 다시 짜지 않는다.
  계약: 누르면 콘솔이 아니라 **설정 창 안의 배포 화면**(Core `DeployPanel` — `SettingsWindow` 의 `DeployHost` 가 탭 자리를 덮는다)이 여닫힌다 — `release.ps1` 이 `-Unattended` 를 받을 때이고(`AppDeploy.SupportsInApp`), 못 받는 옛 스크립트면 콘솔 배포 그대로다.
  계약: 앱 안 배포는 `release.ps1 -Unattended -KeepApp -Bump … -Date … [-Overwrite]` 로 돌고 `-KeepApp` 이 빌드를 `bin\deploy` 로 보내 앱을 내리지 않는다 — `publish.ps1` 의 그 인자를 지우지 않는다.
  함정: 배포 화면의 뷰모델은 설정 창과 함께 사라진다 — 창을 닫았다 열면 진행 표시는 이어지지 않고 배포 자체는 끝까지 간다.
  계약: 「앱 공유하기」 셋(winget 명령 · 설치 파일 주소 · 내려받기)의 좌표는 `Services/AppUpdate.cs` 다 — `SetupAssetName` 은 `publish.ps1` 의 `$SetupAsset`,
  `WingetId` 는 `winget-manifest.ps1` 의 `$id` 와 같아야 한다.
  ★ 계약: **설치 파일(`JHJ_Git-Center_Setup.exe`)은 진짜 설치 관리자다** — `200.Source/GitCenter.Setup`(net48 · `Jhj.Core.Setup` 소스 링크 — 화면은 Core `SetupWindow` 상속, 앱 몫은 꾸러미 풀기뿐 · 규격 040 §6)이고 `publish.ps1` 이 업데이트 꾸러미 zip 을 품겨 빌드한다(`-p:PackageZip`). 같은 내용이 `…_Setup_<버전>.exe` 로도 나간다. ⛔ 앱 exe 를 이름만 바꿔 설치 파일로 내지 않는다(규격 `JHJ_CS_CORE/300.Docs/040`).
  ★ 계약: 설치 화면의 「필수 도구」는 **Git · GitHub CLI** 둘이다(`GitCenter.Setup/Prerequisites.cs` → Core `SetupWindowOptions.Prerequisites`) — 없는 것은 켠 채로 보이고 앱 설치 뒤 winget 으로 깐다(2026-10-14 사용자 지시 — 첫 설치 PC 에서 git 없이 뜨지 않게).
  조용한 설치(`--silent`)는 건너뛰고 `800.Deploy/winget-manifest.ps1` 의 `Dependencies`(같은 두 패키지)가 맡는다. 실행 파일 이름·알려진 폴더는 `ToolLocator` 와 같아야 한다.
  계약: 받은 앱 exe(`JHJ_Git-Center_<버전>_win-x64.exe`)를 직접 돌리면 Core `SelfInstaller` 가 묻고 설치한다 — `--silent` 면 묻지 않는다(`App.ConfirmInstall`). 어느 길이든 설치 폴더에는 `JHJ_Git-Center.exe` 하나다.
  함정: `Start-Process <설치 파일> -Wait` 는 설치 파일이 띄운 앱까지 기다려 끝나지 않는다 — 시험 스크립트에서는 `--no-launch` 를 주거나 `-Wait` 를 빼고 폴링한다.
  함정: winget 명령은 `microsoft/winget-pkgs` 가 매니페스트를 받아들인 뒤에만 된다 — 스크립트로 매니페스트를 쓰는 것만으로는 안 된다.
- 계약: 설정 창의 고르는 상자는 **내부 키를 그대로 보이지 않는다** — `ThemeLabel`·`PaletteLabel`·`LanguageLabel`·`ScaleLabel` 컨버터를 거친다.
- 계약: 명암·색·배율·언어는 **고르는 즉시** 비친다(`SettingsViewModel` 의 감싼 속성). 「적용」은 저장이다.
  `AppSettings` 는 알림이 없는 보통 객체라 **감싸지 않으면 가로챌 수 없다.**
- 계약: 창이 직접 그리는 설정(배율·작업 로그 단)은 `MainViewModel.NotifyDisplaySettingsChanged()` → `MainWindow.ApplyDisplaySettings()` 로 전해진다.
- 계약: **Windows 제목 표시줄을 쓰지 않는다** — `WindowChrome`(CaptionHeight 0)으로 떼고 창 제어줄이 끌기·최대화·시스템 메뉴를 맡는다.
  `WindowStyle="None"` 으로 바꾸지 않는다 — 스냅·가장자리 크기 조절·그림자를 잃는다.
  계약: 보통 창은 **지금 올라 있는 모니터의 작업 영역을 넘지 않는다** — Core `WindowFit.KeepInsideWorkArea` 가 크기·자리 변화마다 조인다(2026-10-11 사용자 지시 · 외부 `MoveWindow` 뒤 창이 1527px 로 튀어 아래 띠가 작업 표시줄 뒤로 숨던 것).
  계약: 일괄 작업 띠는 **묶음 넷**이다 — 1 브랜치 · 2 Fetch · Pull · 3 Merge · 4 Commit · Push. 묶음마다 머리 한 줄 + 두 줄이고, 띠의 용어만 git 용어다.
  계약: 창의 최소 폭은 띠가 **한 줄로 들어가는 폭**이다(`MainWindow.OnActionTopRowSizeChanged` → `ApplyDisplaySettings`) — 앞 묶음 셋의 폭 + 넷째 묶음의 최소 폭 430 + 여백 50. 띠가 3·4줄로 흐르지 않는다.
  TODO: 한국어 1625 · 영어 1739(배율 1)는 띠를 넷으로 다시 짜기 전의 실측이다 — 다시 잰다.
  계약: 창 맨 아래 상태 줄(`StatusBar`)은 **늘 보인다** — 왼쪽은 하는 일(`StatusBarText` · 없으면 「준비」)과 진행막대, 가운데는 가장 최근 작업 기록 한 줄(`LastActivityLine` ← `PushActivity`), 오른쪽은 갱신 줄(`RefreshLine`)이다.
  함정: 「상태 새로 고침」은 `IsBusy` 를 켜지 않는다 — 상태 줄의 「일하는 중」은 `BusyText` 로 판정한다(`IsBusy` 에 묶었더니 새로 고침 때 진행 표시가 사라졌다).
  계약: 복원 크기의 기본은 화면 비율 그대로 작업 영역 넓이의 60% 쯤이다(`MainWindow.RestoreRatio` 0.775 = √0.6) — 화면을 꽉 채운 저장값은 고른 적 없는 값으로 보고 기본 크기·가운데 자리를 쓴다.
  함정: 기본 `Button` 의 `MinHeight` 32 는 `Height` 를 이긴다 — 줄 안의 작은 아이콘 단추는 `MinHeight="0"` 을 함께 준다(저장소 줄 톱니가 이름 줄을 32px 로 벌렸다).
  ⚠️ 창 단추 옆에 앱 단추를 두지 않는다 — 성격이 다른 둘이 한 줄에 섞이면 어색하다(2026-10-10 사용자 지적).
- ★ 함정: `TabControl` 템플릿의 내용 자리는 반드시 **`x:Name="PART_SelectedContentHost"`** 다 —
  없으면 고른 탭의 내용이 **접근성 트리에 통째로 안 올라간다**(화면 낭독기도 UI 자동화도 못 읽는다. 2026-10-10 실측).
- 계약: 오른쪽 단은 **「변경 내용」·「작업 로그」·「시스템 작업 로그」·「시스템 오류」 네 탭**이다. 가운데에서 변경 파일을 고르면 `SelectedChange` 가 바뀌고 0번 탭으로 옮긴다.
  계약: 로그 탭 셋은 몸통(`ActivityLogPane`)과 거르개(`ViewModels/ActivityLogView` — 아래 줄의 분류 알약 + 검색)를 함께 쓴다. 「시스템 오류」는 전체 기록에서 실패·충돌만 남긴 보기다 — 원본 목록은 `Activity` 하나다.
  함정: 분류 알약은 `IsChecked` 양방향 바인딩이다 — Command 로 묶으면 UI 자동화·화면 낭독기의 토글이 분류를 못 바꾼다(2026-10-12 실측).
  계약: 「병합」·「Pull」(자동 stash 꺼짐)은 git 을 부르기 전에 커밋하지 않은 변경과 들어올 파일(`HEAD...<ref>`)이 겹치는지 본다(`GitService.OverlapWithIncomingAsync`) — 겹치면 실패로 끝내고 받기 미리 보기의 충돌·자동 병합·추적 전 수를 문구에, 파일별 판정을 `Detail` 에 싣고 `conflict` 를 깜빡인다.
  ★ 계약: 받기 미리 보기(`GitService.PreviewIncomingAsync` · `MainViewModel.Incoming.cs` · 충돌 탭 `IncomingPanel`)는 `stash create`(작업 트리 스냅숏 — stash 목록·파일은 그대로) + `merge-tree --write-tree`(git 2.38+)로 **받기 전에** 합쳐 보고, 충돌 구간과 처리 순서를 보인다 — 받을 것이 있고 병합 중이 아닌 저장소를 고를 때 계산하며 원격에는 닿지 않는다(마지막 Fetch 기준).
  계약: 충돌 탭은 두 단이다 — 왼쪽 항목 목록(`ConflictItems` — 실제 충돌, 없으면 미리 보기의 충돌)과 오른쪽 고른 항목 하나(`SelectedConflict` · 「다음 항목」). 충돌 아닌 겹침(자동 병합 · 추적 전)은 왼쪽 아래에 이름만 보인다.
  계약: 미리 보기에서도 구간을 고를 수 있고 파일은 안 바뀐다 — 고른 것은 저장소별로 기억했다가(`_pickedHunks`, 양쪽 내용으로 같은 구간을 찾는다) 「커밋하고 받기」(겹칠 때만 띠의 커밋 제목으로 커밋 → 원격의 같은 브랜치 Merge) 뒤 실제 충돌에 이어 붙인다. 적용은 여전히 「이 파일 적용」·「고른 것 모두 적용」이다. 추적 전 파일은 스냅숏에 안 들어가 `Untracked` 로 따로 센다.
  계약: 가운데 상세 단은 탭 여섯이다 — 「변경 파일」·「브랜치」·「충돌」·「상태」·「작업」·「설정」(`MainViewModel.ShareTabIndex` = 5).
  ★ 계약: 「설정」 탭(`Views/RepoShareView` · `MainViewModel.Share.cs` · `Services/GitHubAccount.Share.cs`)은 그 저장소의 GitHub 공유 사용자(Collaborators)다 — 전부 `gh api`(repos/{owner}/{repo} · /collaborators?affiliation=direct · /invitations)이고 토큰은 gh 가 쥔다.
  계약: 「내 저장소」는 `permissions.admin` 이다 — 원격 주소의 소유자 이름과 로그인을 견주지 않는다. 참일 때만 「추가」(`PUT …/collaborators/{login}` · 기본 권한 push)와 줄의 「빼기」(`DELETE` — 초대 중이면 `/invitations/{id}`)가 보이고, 아니면 목록만 본다.
  계약: 원격에 닿는 때는 탭을 처음 열 때 한 번(`OnDetailTabIndexChanged` → `OnShareTabOpened`)과 「새로 읽기」·「추가」·「빼기」뿐이다 — 저장소를 고르는 것만으로는 읽지 않는다(`ResetShare` 는 원격 주소만 본다). 빼기는 먼저 묻는다.
  함정: 더하기는 초대다(201) — 받아들이기 전까지 「초대 중」 칩으로 보이고, 조직 구성원·이미 공유된 사람만 204 로 바로 반영된다. `gh api --paginate` 는 쪽마다 배열을 이어 붙여 내므로 `AllowMultipleValues` 로 읽는다.
  계약: 「상태」 탭의 stash 목록은 줄마다 「되살리기」(`stash pop <ref>`)·「버리기」(`stash drop <ref>`)를 갖는다(`MainViewModel.Status.cs`) — 버리기는 되돌릴 수 없어 메시지·파일 수를 보이고 먼저 묻는다. 그 밖에 이 탭이 git 에 쓰는 것은 없다.
  계약: 거르개 「상태」에는 `stash`(StashCount > 0) 줄이 있다 — 상단 현황 칩에는 없다.
  계약: 「브랜치」 탭은 왼쪽 브랜치 목록 + 오른쪽 이력(최근 200건 · `MainViewModel.BranchLog.cs`)이다 — 원격에 없는 커밋에 「Push 전」을 달고, 판정은 로컬이 아는 원격 추적 ref 뿐이다.
  계약: 이력의 커밋을 누르면 그 아래에 그 커밋이 바꾼 파일이 뜨고(`ReadCommitFilesAsync` · 병합 커밋은 첫 부모 기준), 파일을 누르면 오른쪽 「변경 내용」에 그 코드가 보인다 — 「변경 내용」의 표시는 `IsDiffShown` 하나가 정하고 변경 파일·커밋 파일 선택은 서로를 푼다.
  ★ 계약: 띠 넷째 묶음의 「Push 취소」(`BulkOperationKind.UndoPush` · `GitService.UndoPushAsync`)는 **올리기 전 내용으로 되돌리는 커밋을 하나 더해 보통 Push 로 올린다** — force push·reset 이 아니라서 올렸던 커밋은 이력에 남는다. 화면이 먼저 묻는다(`OnUndoPushClick`).
  계약: 되돌릴 Push 는 원격 추적 ref 의 reflog 맨 위가 `update by push` 이고 HEAD 가 그때 올린 커밋 그대로일 때만이다 — Push 뒤 커밋이 더 생겼거나, 그 Push 가 원격 브랜치를 처음 만들었거나, 업로드 브랜치의 올리기 전 내용이 작업 브랜치와 달랐으면 건너뛴다.
  계약: 브랜치를 두 번 누르면 화면이 먼저 묻고 Switch 한다(`MainWindow.Repos.cs` → `SwitchBranchAsync`) — 강제 옵션이 없어 겹치는 변경이 있으면 git 이 거부한다.
  계약: 이 단은 **접힌 채(손잡이만)로 시작한다** — `MainViewModel.IsRightPanelOpen` 이 펴고, 변경 파일을 누르면 저절로 펴진다.
  설정 「작업 로그 단」을 끄면 손잡이까지 숨는다(`MainWindow.ApplyDisplaySettings`).
- 계약: 화면은 **제목 띠의 탭 넷**이다 — 0 「대시보드」 · 1 「저장소 관리」 · 2 「커밋 내역」 · 3 「점검」(`MainViewModel.PageIndex`). `Settings.StartPage` 에는 0·1 만 남긴다.
  계약: 「커밋 내역」은 대시보드 줄의 `ChartRow.Tag` 가 연다 — 캘린더는 `yyyy-MM-dd..yyyy-MM-dd`, 저장소 줄은 `repo:<Id>`, 마지막 커밋 시점은 `age:<일>..<일>` 이다(`MainViewModel.History.cs`).
  함정: 저장소·시점 줄은 최근 735일만 읽는다 — 캘린더가 읽어 둔 가장 긴 기간과 같은 값이다.
- 계약: 제목 띠의 「도움말」은 **지금 화면의 요소를 차례로 비춘다** — 걸음 목록은 `Views/MainWindow.Tour.cs`, 그리기는 `Controls/TourOverlay` 다.
  화면에 기능을 더하면 그 화면의 걸음을 하나 더하고, 비출 요소에 `x:Name` 을 붙인다. 보이지 않는 요소의 걸음은 건너뛴다.
  함정: 쪽지 크기를 잴 때 `Margin` 을 먼저 지운다 — `DesiredSize` 가 여백을 포함해 앞 걸음의 자리만큼 크게 재진다.
- 계약: 「점검」의 표는 **읽기 전용 보고**다(`MainViewModel.Audit.cs` · `IGitService.ReadAuditAsync`) — 저장소마다 `git config` 한 번과 파일 확인뿐이다.
  「다름」은 다수와 견준 것이다 — 작성자 메일·`autocrlf` 는 가장 많이 쓰인 값이 기준이다.
- ★ 계약: 「작성자 일괄 적용」은 `user.name`·`user.email` 만, **저장소 로컬(`--local`)에만**, **미리 보기에 오른 저장소에만** 쓴다(`WriteAuthorAsync`). 대상은 `SelectedShown` 이다.
- ★ 계약: 「공통 무시 목록」은 고른 저장소의 `.git/info/exclude` 에 **없는 줄만 끝에 덧붙인다**(`MainViewModel.Exclude.cs` · `AppendExcludeAsync`) — 있는 줄은 지우지도 고치지도 않는다.
  계약: 「.gitignore 에 쓰기」를 켜면 같은 줄을 작업 트리 맨 위 `.gitignore` 에 덧붙인다(없으면 만든다 · `AppendGitignoreAsync`) — 스테이지·커밋은 하지 않고, 있던 줄바꿈(CRLF/LF)을 따른다.
  적어 둔 줄은 `Settings.CommonExcludes` 에 남고, 통째로 가리는 줄(`*` · `**` · `/*` 등)은 일괄로 넣지 않는다.
  함정: exclude 의 자리는 `rev-parse --git-path info/exclude` 로 묻는다 — 작업 트리(worktree)는 공통 `.git` 의 파일을 쓴다.
  ⛔ `autocrlf` 를 「작성자 일괄 적용」에 더하지 않는다 — 줄바꿈이 통째로 바뀌어 파일 전체가 변경으로 잡힌다(2026-10-11 사용자 결정). 바꾸기 전 값은 작업 로그에 남긴다.
- 계약: 변경 파일 목록은 확장자 하나로 좁힐 수 있다 — `Changes` 는 걸러진 목록이고 채우는 길은 `SetChanges` 하나다(`MainViewModel.ChangeFilter.cs`).
- 계약: 커밋 캘린더의 **기간과 보기 형식은 독립**이다 — 기간은 `Settings.DashboardChartKinds["calendar.range"]`, 세로·선은 30일=날 · 90일=주 · 180일 이상=달로 묶는다.
  함정: 「달력」은 칸 높이가 16px 미만이면 잔디로 대신 그린다 — 1년 이상에서 잔디와 같아 보이는 것은 고장이 아니다.
- 계약: 일괄 커밋 글상자는 「커밋 제목」·「메시지」 둘이고 자리표 `{기능}`·`{그룹}`·`{저장소}` 는 **누를 때** 채운다(`MainViewModel.FillCommitText`). 본문은 `commit -m` 을 두 번 준다.
- ★ 계약: 브랜치 설정은 둘이다 — 「작업 브랜치」(`Settings.WorkBranch`, 내가 일하는 곳)와 「업로드 브랜치」(`Settings.UploadBranch`, 코드가 모이는 곳).
  저장소별 값(`Settings.RepoBranches`)이 공통 값을 덮고, 일괄 작업이 저장소마다 풀어 쓴다(`BulkOperationOptions.WorkBranchFor`·`UploadBranchFor`).
  계약: 「병합」은 고른 브랜치(비면 업로드 브랜치)를 `fetch` 한 뒤 지금 브랜치로 합치고, 「올리기」는 두 벌이다 — 지금(작업) 브랜치를 제 원격 브랜치로, 업로드 브랜치가 다르면 거기에도 `push <remote> HEAD:refs/heads/<업로드>` 한다 — force 없음.
  계약: 업로드 브랜치로 올린 뒤 로컬의 같은 이름 브랜치도 빨리감기로 맞춘다(`GitService.SyncLocalUploadAsync` · `fetch .` 라 원격에 닿지 않는다) — 로컬에 없으면 만들지 않고, 갈라졌으면 그대로 둔다.
  계약: 지금 브랜치가 가져올 브랜치와 같으면 「병합」은 원격의 같은 브랜치(`<remote>/<브랜치>`)를 합친다 — 올리기가 거부된(갈라진) 저장소를 이 단추로 푼다. Pull 은 `--ff-only` 라 갈라지면 못 푼다.
  계약: 실패를 푸는 단추가 정해진 결과는 `BulkStepResult.NextAction`(`BulkNextAction`)에 그 이름을 싣고, 화면은 같은 이름을 `Nudge.Key` 로 단 단추를 깜빡인다(Core `Jhj.Core.Wpf` `Shell/Nudge` · `MainViewModel.NudgeAction` — 규격 `JHJ_DEV/000.AGENTS_MD/080.CS앱-화면/080.CS앱-화면-공통.md` §6). 다음 일괄 작업을 시작하면 꺼진다.
  함정: 접힌 단(Collapsed) 위의 덧그림은 WPF 가 지우지 않는다 — `Nudge` 가 단추의 `IsVisibleChanged` 를 따라 테를 함께 숨긴다(2026-10-11 「점검」 화면에 테가 남던 것).
  셋이다 — `merge`(올리기 거부 → 「지금 브랜치로 병합」) · `conflict`(충돌 → 상세 「충돌」 탭, 그 탭을 열면 꺼진다) · `switch`(작업 브랜치가 아님 → 「전환」). 결과가 섞이면 충돌 → 실패 → 건너뜀 차례로 첫 것을 고른다.
  계약: 넷째 `sync` 는 결과가 아니라 **층이 정한다** — Push 되지 않은 저장소 층(`PushGapLayer`)이 뜨면 「받아서 Merge + Push」를 깜빡이고, 층이 닫히면 뜨기 전 값으로 돌아간다(`ShowPushGap` · `OnIsPushGapOpenChanged`). 예외를 가리는 것보다 받아서 올리는 것이 먼저라서다(2026-10-13 사용자 지시).
  ★ 계약: **단추 색은 「하는 일」이 정한다** — 낱말이 달라도 같은 일이면 띠·층 어디서든 같은 색이다(`Styles.xaml` 머리). `PushButton`(채운 강조색) = 올리는 일(커밋 + Push · 받아서 Merge + Push · 진행 · 만들어 올리기) ·
  `MergeButton`(강조색 테두리) = 받아 합치는 일(지금 브랜치로 Merge · 병합 마무리) · `DangerButton` = 취소·버리기. Core `PrimaryButton` 은 그 밖의 적용·만들기에만 남긴다. 층의 「이 저장소만 고르기」는 보통 단추다.
  계약: 지금 브랜치가 작업 브랜치와 다르면 병합·커밋·올리기를 건너뛴다 — 단 작업 브랜치가 그 저장소에(로컬에도 원격 추적 ref 에도) 없으면 막지 않는다(`GitService.NotOnWorkBranchAsync`).
  ★ 계약: 커밋 · Push · 커밋 + Push 는 건너뛰기 전에 **먼저 묻는다** — 그런 저장소를 층(`Views/OffBranchLayer` · `MainViewModel.PushCheck.cs`)에 모아 저장소별로 체크받고, 체크한 것만 `BulkOperationOptions.ConfirmedBranches`(저장소 Id → 확인한 브랜치)로 넘겨 지금 브랜치 그대로 진행한다.
  계약: 체크는 기본이 꺼짐이고 「취소」는 일괄 작업을 통째로 그만둔다. 확인한 뒤 브랜치가 바뀌었으면 다시 막는다. 병합은 묻지 않고 예전처럼 건너뛴다.
  계약: 커밋은 됐는데 Push 되지 않은 저장소(`Ahead > 0` 또는 upstream 없음 · 원격 없는 저장소 제외)는 층(`Views/PushGapLayer`)이 저장소별로 보인다 — 띠 넷째 묶음의 「Push 전 n」 단추가 열고, Push 가 낀 일괄 작업 뒤에 남은 것이 있으면 저절로 뜬다.
  ★ 계약: 그 층의 「받아서 Merge + Push」(`SyncPushGapAsync`)는 원격의 같은 브랜치를 합친 뒤(`BulkOperationOptions.MergeOwnRemote`) 이어서 Push 한다 — Merge 가 충돌·실패한 저장소는 Push 로 넘기지 않고, 층에 「충돌」 줄로 다시 모아 「충돌」 탭으로 안내한다. force 는 없다.
  ★ 함정: `add -A` 는 충돌 표식(`<<<<<<<`)이 든 파일도 스테이지한다 — 풀지 않은 충돌이 있으면 `CommitAllAsync` 가 커밋하지 않고 「충돌」로 끝낸다(2026-10-11 실측: 표식째 main 에 올라갔다).
  계약: 결과 문구에 「원격」이라고만 적지 않는다 — `origin/main` 처럼 원격·브랜치 이름으로, 업로드 브랜치면 「업로드 브랜치 origin/main」으로 적는다.
  계약: 충돌 탭의 차이 요약은 세 층이다 — 저장소(`ConflictOverview`) · 파일 · 구간(`DiffSummary`). 구간마다 코드는 갈라지기 전(공통 조상) · 내 것 · 상대 것 셋을 보인다.
  계약: 띠 네 묶음의 두 줄은 높이를 함께 쓴다(`SharedSizeGroup` `ActionRowTop`·`ActionRowBottom`) — 묶음마다 아래 여백이 달라지지 않는다.
  계약: 저장소 목록의 「요약 보기」(`IsSummaryView` ↔ `Settings.RepoSummaryView`)는 줄을 둘로 줄인다 — 이름 줄, 그리고 브랜치 줄 오른쪽에 마지막 결과.
  계약: 창 안에서 F5 는 「상태 새로 고침」, Shift+F5 는 「목록 새로 고침」이다(`Window.InputBindings`) — 전역 단축키가 아니다.
  계약: 상태 줄의 갱신 줄(`MainViewModel.RefreshLine`)은 「2026-10-11 오후 10:06 갱신 · 3분 전 · 다음 22:11 · 4:12 후」 꼴이다 — 주기는 설정 「동기화」의 「상태 다시 읽기(분)」이고 1초 타이머(`_refreshLineTicker`)가 다시 그린다.
  계약: 화면의 때는 **날짜 + 경과**로 적는다(`RepoRow.Ago`) — 저장소 줄의 다시 읽은 때(`RefreshedClockText` · 「2026-10-11 22:28:55 · 3분 전」)와 마지막 커밋(`LastCommitLine` · 「2026-08-23 · 7주 전 · 제목」)이 그렇다. 시각이나 경과 하나만 적지 않는다.
  계약: 최대화된 창은 제목 띠를 끌어도 풀리지 않는다 — 두 번 누르거나 창 단추로만 푼다(`MainWindow.OnTitleMouseDown`).
  함정: `MainWindow.xaml` 은 UTF-8 BOM 이다 — 스크립트로 다시 쓸 때 BOM 을 지우지 않는다.
  계약: 일괄 작업의 대상·확인을 밖에서 줄 때는 `RunBulkAsync(kind, only, ownRemote, preset, showGap)` 를 쓴다 — 단추는 인자 없이 부른다(보이는 줄 중 고른 것).
  계약: Pull 은 upstream 이 없으면 원격의 같은 이름을 받고, 그것도 없으면 실패가 아니라 건너뜀이다 — 갓 만든 작업 브랜치를 「실패」로 세지 않는다.
  계약: 「전환」은 고른 저장소를 작업 브랜치로 `switch` 한다 — 로컬에 없으면 원격의 같은 이름을 추적해 만들고, 둘 다 없으면 건너뛴다. 강제 옵션은 없다.
  함정: 업로드 브랜치가 지금 브랜치와 다를 때, 원격에 없는 이름으로는 올리지 않는다 — 그대로 올리면 새 원격 브랜치가 조용히 생긴다(2026-10-11 실측).
  계약: 그 판정은 로컬이 아는 ref(`refs/remotes/<remote>/<업로드>`)로 한다 — 원격에 있어도 Fetch 전이면 건너뛴다. 업로드 브랜치가 비면 지금 브랜치를 그대로 올린다(원격에 없으면 새로 생긴다).
  함정: 분리된 HEAD 는 `rev-parse --abbrev-ref HEAD` 가 `HEAD` 를 돌려준다 — `CurrentBranchAsync` 가 빈 값으로 바꿔 올리기를 건너뛴다(그대로 쓰면 `push HEAD:HEAD` 로 거부된다).
  계약: 브랜치·그룹은 **있는 것 중에서 고른다**(고르는 상자) — 손으로 적는 칸은 「새 그룹 이름」 하나뿐이다. 줄 하나의 값은 줄을 두 번 누르거나 톱니로 여는 쪽지가 고친다(`MainViewModel.Branches.cs`).
  함정: 이 앱의 `ComboBox` 틀은 고른 줄을 `ToString` 으로 그린다 — 줄 객체가 `ToString` 을 안 덮으면 레코드 모양이 보인다.
  계약: 저장소 줄을 끌어 그룹 머리·다른 그룹의 줄·거르개 단의 「그룹」 줄에 놓으면 옮겨진다(`Views/MainWindow.Repos.cs`).
- 계약: 필터 단은 조건 넷(그룹 · 상태 · 작업 · 브랜치)을 겹쳐 건다(`MainViewModel.Facets.cs`).
  계약: 「브랜치」는 **설정 기준으로 둘로 나눈다** — 「공통 설정」(공통 작업·업로드 브랜치를 따르는 저장소)과 「저장소별 설정」(`Settings.RepoBranches` 를 따로 정한 저장소). 묶음 머리는 묶음만, 그 아래 줄은 묶음 + 지금 브랜치 이름을 함께 건다(`BranchScope` · `BranchFilter`). 「작업 중」(`RepoRow.IsMine` ↔ `Settings.WorkingRepos`)은 일괄 작업이 도는 중(`IsWorking`)과 다른 뜻이다.
- 함정: 단추 템플릿의 내용 가로 맞춤은 `HorizontalContentAlignment` 를 따른다 — `Center` 로 박으면 줄 모양 단추(필터 단)가 가운데로 몰린다.
  제목 띠는 **테마와 무관하게 짙다** — 띠 위의 글자·단추는 테마 브러시가 아니라 `Styles.xaml` 의 `Title*` 브러시를 쓴다.
- ★ 계약: 저장소 목록은 **`RepoRow.GroupNode` 객체로 묶인다**(`PropertyGroupDescription`) — 그룹 머리가 `{Binding Name.…}` 으로 접힘·건수를 읽는다.
  `ApplyFilter` 가 `Rows` 에 넣기 전에 줄마다 `GroupNode` 를 채우고 **그룹별로 모아 세워** 넣는다(묶인 목록의 그룹 차례는 처음 나온 줄 차례다).
  함정: 묶인 `ListBox` 는 `ScrollViewer.CanContentScroll="False"` 여야 한다 — 켜 두면 그룹 단위로 뚝뚝 넘어간다.
- 계약: 대시보드의 카드·줄은 **건너가는 길**이다 — `GoToState`·`GoToGroup`·`GoToRepo` 가 거르개를 맞추고 1번 화면으로 옮긴다.
  계약: 건너갈 때는 남아 있던 검색어·거르개를 먼저 푼다(`ResetFilters`) — 겹치면 빈 목록으로 보인다.
- 계약: 대시보드는 `Views/DashboardView` 한 파일이다 — 통계 카드의 수는 `MainViewModel.Dashboard.cs`, 그림은 `Controls/ChartView`(라이브러리 없이 직접 그린다)가 맡는다.
  표시 방식은 카드 머리 고르개가 바꾸고 **고르는 즉시** `Settings.DashboardChartKinds` 에 남는다. 범주 색은 여덟 고정 — 넘치면 자료 쪽이 「기타」로 접는다.
  계약: 통계 칸은 일곱(`Slots`)이고 고를 통계는 열여섯이다 — 「UI 편집」 중 카드 제목 자리의 고르개가 바꾸고, 이미 다른 칸에 있으면 맞바꾼다. 자리는 `DashboardChartKinds["slot.<n>"]` 에 즉시 남는다.
  계약: 「그룹별 현황」은 지표 고르개(`GroupChart.Metric` — 상태 · 갱신 파일 · 커밋 · 변경 파일 · 변경 줄 · 추적 파일 · 용량)와 기간(7일~1년)을 갖는다 — 카드·그림이 같은 수를 보고, 「상태」 밖 지표는 값이 큰 그룹부터(`GroupCards`) 놓인다. 「최근 커밋」·「최근 갱신 파일」 카드도 같은 기간 조각을 쓴다. 저장 열쇠는 `<카드>.metric` · `<카드>.range`.
- 계약: 「상태 새로 고침」은 저장소마다 git 을 일곱 번 더 부른다 — 바뀐 것이 있을 때만 `diff HEAD --shortstat`(추가·삭제 줄 수), 늘 `log --since=735.days.ago --format=%cs`(커밋 캘린더 — 가장 긴 기간 2년치)와 `ls-files`(확장자 구성 · 추적 파일 수) · `log --format=%cd%x09%an`(작성자별 · 시간대별 커밋) · `count-objects -v`(.git 용량) · `log --since=365.days.ago --format=%x1f%cs --name-only`(최근 갱신 파일 — 경로마다 마지막 날·횟수, `RepoRow.RecentFiles`) · `reflog show --all --date=iso-strict`(최근 30일 — `RepoRow.WorkLog`).
  ★ 계약: 대시보드 「최근 작업」은 **git reflog** 다(`MainViewModel.ActivityLog.cs` 의 `RecentWork` — HEAD 의 커밋·전환·Merge·Pull·Reset, 원격 추적 ref 의 `update by push`, stash, 브랜치 생성) — CLI·다른 도구로 한 일도 보인다.
  앱이 직접 한 일(`Activity`)은 「시스템 작업 로그」 탭이 보이고 `data\activity.jsonl` 에 남아 다음 실행에 되살아난다(최근 500건). 2026-10-14 전에는 메모리뿐이라 재시작마다 비었다.
  함정: 줄 수는 추적 전 새 파일을 세지 않는다 — 세려면 파일을 다 읽어야 해서 일부러 뺐다. 캘린더는 지금 브랜치의 커밋만, 작성자를 가르지 않고 센다.
- 계약: 카드 크기는 **「UI 편집」 중에만** 바뀐다 — 나누개(`LayoutSplitter`)는 평소 꺼진 빈 간격이고, 「배치 저장」이 별(*) 비율을 `Settings.DashboardLayout` 에 적는다.
  함정: 격자의 짝수 자리가 카드, 홀수 자리가 나누개다 — 카드를 더하면 `DashboardView.xaml.cs` 의 `Defaults` 칸 수도 함께 고친다(안 맞는 저장값은 버려진다).
- ★ 계약: 일괄 작업·그룹 옮기기·숨기기는 **보이는 줄 중 고른 것**에만 돈다(`SelectedShown`) — 거르개에 가려진 저장소는 체크돼 있어도 건드리지 않는다.
  「고른 수 / 보이는 수」 칩도 같은 기준이다. 대시보드 「대표 상태 분포」는 저장소를 `StateText` 하나로 세어 현황 칸(겹쳐 셈)과 수가 다르다.
- ★ 계약: 충돌 탭은 **구간마다 고르고 적용**한다 — 판정은 `ConflictResolver`(글자만 다룬다), 쓰기·스테이지는 `GitService.ResolveConflictAsync` 다.
  자동(`IsCertain`)은 넷뿐이다: 양쪽이 같음 · 공백만 다름 · 한쪽만 바꿈 · 서로 다른 줄을 고침(`TryMergeLines`). 자동도 **미리 골라 두기만** 하고 파일은 「적용」을 눌러야 바뀐다.
- 계약: 충돌 내용은 작업 파일이 아니라 **인덱스 세 단계**로 다시 만든다(`checkout-index --stage=all --temp` → `merge-file --diff3`) — 공통 조상이 있어야 자동 판정을 한다.
  함정: 그 결과는 **바이트로** 읽고 Latin1 로 줄만 골라 다시 쓴다 — 글자로 풀면 UTF-8 이 아닌 파일·CRLF 가 조용히 바뀐다.
- 계약: 적용 전의 작업 파일은 `.git/jhj-conflict-backup/<때>/` 에 남긴다. 한쪽이 지운 파일·바이너리는 앱에서 풀지 않는다(`CanResolve` false).
  「병합 마무리」는 **병합(MERGE_HEAD)일 때만** `commit --no-edit` 을 한다 — 리베이스·체리픽 이어 가기는 넣지 않았다.
- 함정: 추적 전 파일은 `git diff` 가 빈 결과를 낸다 — `--no-index` 로 `/dev/null` 과 견준다(`NUL` 로 바꾸면 안 읽는다).
- 계약: 그룹은 **사용자가 지정한 것(`Settings.RepoGroups`)이 스캔이 매긴 폴더 이름을 덮는다.** 그룹을 지워도 저장소는 남는다.
  계약: 스캔이 매기는 이름은 루트의 `ScanRoot.GroupLevel` 이 정한다 — 0 은 바로 위 폴더, 1·2 는 루트에서 그 단계까지의 폴더(`RepoKey.GroupUnder`, 2단계는 `JHJ / 900.Legacy` 꼴). 다른 출처가 먼저 들고 온 저장소도 `RepoRegistry.ApplyRootGroup` 이 합친 뒤 다시 매긴다.
  계약: 설정 「목록 출처」의 「폴더 기준으로 다시 잡기」·「목록·그룹 초기화」·「전체 초기화」는 **「적용」을 기다리지 않는다** — 확인 한 번 뒤 바로 저장하고 다시 훑는다(`SettingsViewModel.Remap.cs`). 설정 파일만 고치고 저장소·git 에는 손대지 않는다.
  함정: 「전체 초기화」는 속성을 통째로 옮긴다(`AppSettings.ResetEverything`) — 언어만 남긴다. 「기본값으로」(`ResetToDefaults`)는 여전히 루트·수동 추가·숨김·그룹을 남긴다.
- ★ 계약: 상세 「작업」 탭(`Views/RepoToolsView` · `MainViewModel.Tools.cs`)은 GitHub Desktop 에 있던 한 저장소짜리 일을 모은 곳이다 — Clone · 새 저장소 · 브랜치 만들기/이름 바꾸기/지우기 · 마지막 커밋 되돌리기/고치기 · Revert · Cherry-pick · stash 넣기/꺼내기 · 태그 · 원격 주소 · Pull Request(브라우저).
  ⛔ 여기에도 되돌릴 수 없는 명령을 넣지 않는다 — 지우기는 `branch -d`, 되돌리기는 `reset --soft` 다. 변경 버리기(discard) · rebase · squash · 커밋 순서 바꾸기는 force push 나 내용 소실이 따라와 **일부러 뺐다.**
  계약: 「되돌리기」·「고치기」는 HEAD 가 어느 원격 추적 ref 에도 없을 때만 돈다(`HeadIsLocalOnlyAsync`) — 올린 커밋은 Revert 로만 뒤집는다.
  함정: 이 탭은 `IGitService` 를 거치지 않고 `_gitRunner` 로 git 을 바로 부른다 — 일괄 작업과 겹치지 않게 `IsBusy` 를 함께 쥔다.
- 계약: 줄마다 `RefreshedAt` 을 찍는다 — 「상태 새로 고침」이 값을, 1분 타이머가 경과 글자(`TouchElapsed`)를 다시 그린다.
- 계약: 기본 아이콘(`Resources/app-icon.ico`)은 **검은 보라 바탕의 금속 JG 모노그램**이다(`make_jhj_icon.py branch … --style lux --mono JGm --bg obsidian` · 2026-10-12 사용자 선택). 설정 「아이콘」의 선택지(`Services/IconPresets.cs`)는 금속 JG 검은 보라 · 금속 JG 보라 · 금속 J · 「JHJ + 가지」(옛 기본, `app-icon-classic.ico`) · 가지 모양 · 연결 · 그물이다. 더하려면 `.ico` 생성 → csproj `Resource` → `IconPresets.All` → 문구 두 파일 순서다.
- ★ 함정: 설정에서 아이콘을 골라도 **작업 표시줄 단추는 안 바뀌었다**(2026-10-13) — 시작 메뉴 바로가기가 있으면 Windows 는 창 아이콘이 아니라 바로가기 아이콘을 쓴다.
  Core `AppIcon.Apply` 가 바로가기의 `IconLocation` 을 함께 맞추고 단추를 다시 만든다(080 §2) — 이 앱에서 따로 할 일은 없다.
- ★ 계약: 설정 「아이콘」의 선택지 그림은 전부 `make_jhj_icon.py … --uniform large|small` 로 만든 **모든 크기 한 디자인** 이다(2026-10-13 사용자 지시 — 견본·작업 표시줄·Alt+Tab 이 달라 보였다).
  `large` 가 금속 JG 모노그램 · 금속 JG(검은 보라·보라) · 금속 J · 「JHJ + 가지」, `small` 이 굵은 금 JG(검은 보라·보라) · 굵은 금 J · 「J + 가지」다. 기본 `app-icon.ico` 도 2026-10-14 부터 모든 크기 한 디자인(JGm)이고, 견본(`IconChoicePreviewConverter`)은 32px 판을 보인다.
- ★ 함정: 아이콘 `.ico` 는 **128 이하를 BMP 로** 적어야 한다 — Pillow 12 의 ICO 저장은 전부 PNG 로 눌러 담고,
  Windows 의 작은 아이콘 경로(작업 표시줄·Alt+Tab)가 그것을 못 읽어 기본 아이콘이 뜬다.
  생성기 `JHJ_DEV/000.AGENTS_MD/070.아이콘/make_jhj_icon.py` 의 `write_ico()` 가 맡는다(2026-10-10 수정).
- 계약: 실행 파일·데이터 폴더·시작 프로그램 값·뮤텍스 이름은 모두 **`JHJ_Git-Center`** 하나다. 바꿀 때는 이사 코드를 함께 넣는다.
  작업 관리자에 보이는 이름(`AssemblyTitle`)도 **`JHJ` 로 시작한다** — `JHJ Git 통합 관리 센터`(2026-10-10).
- 계약: 전역 단축키는 **`Ctrl+Alt+3`** 이다 — 자리표 정본은 `JHJ_DEV/000.AGENTS_MD/060.단축키/060.단축키-대장.md`.
  기본값은 `AppSettings.DefaultHotkey` 한 곳이고, `Services/HotkeyService` 의 정적 생성자가 Core 의 되살림 값에도 넣는다.
  계약: 이미 `Ctrl+Alt+G` 를 저장한 PC 는 `JsonSettingsStore.WithDefaults` 가 **읽을 때 한 번** 새 기본값으로 옮긴다.
- ★ 계약: 자가 업데이트는 `JHJ_CS_CORE` 의 `GithubUpdateService` 가 한다 — 이 앱은 좌표(`Services/AppUpdate.cs`)와 시작 결선(`App.xaml.cs`)만 갖는다.
  **꾸러미 접두(`AppUpdate.PackagePrefix` = `JHJ_Git-Center`)는 `800.Deploy\publish.ps1` 의 `$PackagePrefix` 와 같아야 한다** — 어긋나면 오류 없이 「최신입니다」로 끝난다.
  계약: 업데이트는 **설치본(`%LOCALAPPDATA%\JHJ_Git-Center\app`)에서만** 돈다 — 그 폴더는 통째로 교체되므로 다른 파일을 두는 코드를 쓰지 않는다.
  함정: `release.ps1` 은 한글이 있어 **UTF-8 BOM** 이다. 나머지 `800.Deploy\*.ps1` 은 ASCII 로만 쓴다.
- 계약: 배포 버전은 `Directory.Build.props` 의 `VersionPrefix` 세 자리만 올린다 — 네 번째(빌드 날짜)는 빌드가 붙인다.
- ⛔ **버전 올리기·릴리스 생성·태그 push 는 사용자 지시가 있을 때만** 한다.
- 빌드 전에 **이 폴더에서 띄운** `JHJ_Git-Center` 만 종료한다(`bin\` 잠금).

## 3. 검증

```text
빌드      dotnet build 200.Source\GitCenter.slnx -c Release --nologo -v q     (경고 0 · 오류 0)
Core      ..\JHJ_CS_CORE 를 나란히 받아 둬야 빌드된다 — 없으면 무엇을 하라는 오류 한 줄이 난다
게시      800.Deploy\Build-Exe.bat
git 계층  임시 저장소(bare origin + 작업 사본 2개)를 만들어 Fetch·Commit·Push·Pull·충돌·되돌리기를 실제로 돌린다
화면      앱을 띄워 PrintWindow(flags=2) 로 창을 찍어 본다 — 빌드 통과는 화면 검증이 아니다
          설정 창은 모달이라 UIA 의 창 열거에 안 잡힌다 — EnumWindows 로 「소유자가 있는 보이는 창」을 찾는다
          캡처 스크립트는 ASCII 로만 적는다 — PowerShell 5.1 은 BOM 없는 한글에서 파서가 무너진다
```

⚠️ **검증에 사용자의 실제 저장소로 fetch·push 를 돌리지 않는다.** 원격이 필요한 검증은 **로컬 bare 저장소**로 한다.
