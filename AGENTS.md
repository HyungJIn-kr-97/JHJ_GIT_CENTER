# AGENTS.md — JHJ_GIT_CENTER 에이전트 가이드

워크스페이스 규약은 `JHJ\AGENTS.md`(정본 `JHJ_DEV/000.AGENTS_MD`)가 먼저다. 고객사는 늘 **`JHJ`**, 제목은 `[JHJ] <Role> - <요약>`.
사람용 설명(무엇을 하는 앱인가 · 빌드 · 설정)은 [README.md](README.md) 에 있다 — 여기로 복사하지 않는다.

## 1. 이 repo 의 정체

| 폴더 | 역할 |
|---|---|
| `200.Source/` | 솔루션·프로젝트 4개(Core · Application · Infrastructure · App) — 고치는 곳은 여기뿐 |
| `300.Docs/` | 계약 문서 — `GitHub-Desktop-목록-읽기.md`(LevelDB·snappy 를 푸는 근거와 한계). 소스에는 경로 한 줄만 둔다 |
| `800.Deploy/` | `Build-Exe.bat` · `publish.ps1` · `bump-version.ps1`. 산출물 `publish/` 는 git 제외 |

여러 git 저장소를 한 화면에서 다루는 **Windows 데스크톱 앱**(WPF · `net10.0-windows`). 서버 배포 대상이 아니다.
기본 편의 기능(트레이 · 단일 인스턴스 · 테마 · 언어 · 로그 · 설정 저장 · 시작 프로그램)의 본보기는 `JHJ_AI_USAGE` 다.

## 2. 지켜야 할 것

- ⛔ **되돌릴 수 없는 git 명령을 넣지 않는다** — `push --force`(및 `--force-with-lease`) · `reset --hard` · `clean` · `branch -D`.
  이 앱의 약속이고 설정으로도 열지 않는다. push 가 거부되면 「먼저 받아서 합쳐 주십시오」로 끝낸다.
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
- 계약: XAML 의 색은 전부 `DynamicResource`, 문구는 `{loc:Tr '한국어 원문'}` 이다. 새 문구는 `Localization/LocStrings.cs` 에 한 줄 더한다.
  ⚠️ `JHJ_AI_USAGE` 는 **영어가 키**다 — 이 앱은 방향이 반대다. 섞어 쓰지 않는다.
- 계약: 실행 파일·데이터 폴더·시작 프로그램 값·뮤텍스 이름은 모두 **`JHJ_Git-Center`** 하나다. 바꿀 때는 이사 코드를 함께 넣는다.
- 계약: 배포 버전은 `Directory.Build.props` 의 `VersionPrefix` 세 자리만 올린다 — 네 번째(빌드 날짜)는 빌드가 붙인다.
- ⛔ **버전 올리기·릴리스 생성·태그 push 는 사용자 지시가 있을 때만** 한다.
- 빌드 전에 **이 폴더에서 띄운** `JHJ_Git-Center` 만 종료한다(`bin\` 잠금).

## 3. 검증

```text
빌드      dotnet build 200.Source\GitCenter.slnx -c Release --nologo -v q     (경고 0 · 오류 0)
게시      800.Deploy\Build-Exe.bat
git 계층  임시 저장소(bare origin + 작업 사본 2개)를 만들어 Fetch·Commit·Push·Pull·충돌·되돌리기를 실제로 돌린다
화면      앱을 띄워 PrintWindow 로 창을 찍어 본다 — 빌드 통과는 화면 검증이 아니다
```

⚠️ **검증에 사용자의 실제 저장소로 fetch·push 를 돌리지 않는다.** 원격이 필요한 검증은 **로컬 bare 저장소**로 한다.
