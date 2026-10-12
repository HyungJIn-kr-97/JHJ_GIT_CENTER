using GitCenter.Core.Models;

namespace GitCenter.Application.Settings;

/// <summary>
/// %LOCALAPPDATA%\JHJ_Git-Center\settings.json 하나가 앱의 모든 설정이다.
/// 계약: 새 설정을 더하면 ResetToDefaults 에도 적는다 — 빠뜨리면 「기본값으로」가 그 칸만 조용히 남긴다.
/// </summary>
public sealed class AppSettings
{
    // ── 목록 출처 ─────────────────────────────────────────────
    public List<ScanRoot> ScanRoots { get; set; } = [];

    /// 계약: 사용자가 손으로 더한 저장소 경로 — 스캔 루트 밖에 있어도 목록에 남는다
    public List<string> ManualPaths { get; set; } = [];

    /// 계약: 목록에서 뺀 저장소의 Id — 스캔이 다시 찾아도 숨긴 채로 둔다
    public List<string> ExcludedIds { get; set; } = [];

    // ── 그룹 ─────────────────────────────────────────────────
    /// 계약: 사용자가 만든 그룹 이름 — 적은 차례가 곧 화면에 보이는 차례다
    public List<string> Groups { get; set; } = [];

    /// 계약: 저장소 Id → 그룹 이름. 여기 없으면 스캔이 매긴 폴더 이름을 쓴다
    public Dictionary<string, string> RepoGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>저장소를 그룹에 넣는다 — 빈 이름이면 그룹에서 뺀다.</summary>
    /// 계약: 없는 그룹 이름을 주면 그 그룹을 새로 만든다 — 화면이 두 번 물을 일이 없게
    public void AssignGroup(string repoId, string? group)
    {
        var name = group?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            RepoGroups.Remove(repoId);
            return;
        }

        if (!Groups.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(name);
        }

        RepoGroups[repoId] = name;
    }

    /// <summary>그룹을 지운다 — 그 그룹에 있던 저장소는 스캔이 매긴 이름으로 돌아간다.</summary>
    public void RemoveGroup(string group)
    {
        Groups.RemoveAll(g => g.Equals(group, StringComparison.OrdinalIgnoreCase));
        foreach (var id in RepoGroups.Where(p => p.Value.Equals(group, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key).ToList())
        {
            RepoGroups.Remove(id);
        }
    }

    /// <summary>그룹 이름을 바꾼다 — 그 그룹에 든 저장소를 함께 옮긴다.</summary>
    public void RenameGroup(string from, string to)
    {
        var name = to.Trim();
        if (name.Length == 0 || name.Equals(from, StringComparison.Ordinal))
        {
            return;
        }

        var at = Groups.FindIndex(g => g.Equals(from, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return;
        }

        Groups[at] = name;
        foreach (var id in RepoGroups.Where(p => p.Value.Equals(from, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key).ToList())
        {
            RepoGroups[id] = name;
        }
    }

    /// 계약: 저장소 목록에서 접어 둔 그룹 이름 — 「그룹 없음」은 빈 문자열로 적는다
    public List<string> CollapsedGroups { get; set; } = [];

    public bool GitHubDesktopEnabled { get; set; } = true;

    /// 계약: 비어 있으면 자동 탐지(%APPDATA%\GitHub Desktop) — 다른 곳에 깔았을 때만 적는다
    public string? GitHubDesktopDataPath { get; set; }

    public bool GitHubAccountEnabled { get; set; } = false;

    /// 계약: 비어 있으면 로그인한 본인 저장소 — 조직을 적으면 그 조직 것도 가져온다
    public List<string> GitHubOwners { get; set; } = [];

    /// 계약: 아직 clone 하지 않은 계정 저장소도 목록에 둘지 — 끄면 로컬에 있는 것만 보인다
    public bool IncludeUnclonedAccountRepos { get; set; } = true;

    public List<CommandSourceDef> CommandSources { get; set; } = [];

    // ── 동기화 ───────────────────────────────────────────────
    /// 계약: 목록 다시 훑기 주기(분). 0 이면 수동으로만 한다
    public int RescanMinutes { get; set; } = 30;

    /// 계약: 상태 자동 새로 읽기 주기(분). 0 이면 수동으로만 한다 — fetch 는 하지 않는다
    public int StatusRefreshMinutes { get; set; } = 5;

    /// 계약: 자동 fetch 주기(분). 0 이면 하지 않는다 — 원격을 읽을 뿐 작업 트리는 건드리지 않는다
    public int AutoFetchMinutes { get; set; } = 0;

    /// 계약: 끊어진 저장소(경로 없음)를 이름으로 루트에서 다시 찾아 경로를 고칠지
    public bool AutoRelinkBroken { get; set; } = true;

    // ── git 동작 ──────────────────────────────────────────────
    public string RemoteName { get; set; } = "origin";

    /// 계약: 동시에 돌릴 저장소 수 — 올리면 빨라지지만 디스크·원격이 몰린다
    /// 왜: 코어 수의 절반을 2~8 로 조인다 — 4코어 노트북에서 4개는 git 끼리 디스크를 두고 다툰다
    public int Concurrency { get; set; } = DefaultConcurrency;

    public static int DefaultConcurrency => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// 계약: 한 저장소의 git 한 번에 줄 제한 시간(초)
    public int GitTimeoutSeconds { get; set; } = 120;

    /// 계약: pull 은 언제나 --ff-only 다 — 끄면 merge 커밋을 허용한다. force push 는 어느 설정으로도 열지 않는다
    public bool FastForwardOnly { get; set; } = true;

    /// 계약: 내가 일하는 브랜치(공통) — 비우면 지금 체크아웃된 브랜치를 그대로 쓴다. 저장소별 값(RepoBranches)이 이것을 덮는다
    public string WorkBranch { get; set; } = string.Empty;

    /// 계약: 코드가 모이는 브랜치(공통) — 「병합」이 여기서 받아 오고 「올리기」가 여기로 올린다. 비우면 받기는 기본 브랜치, 올리기는 지금 브랜치다
    public string UploadBranch { get; set; } = "main";

    /// 계약: 저장소 Id → 그 저장소만의 작업·업로드 브랜치. 여기 없거나 빈 칸이면 공통 값(WorkBranch · UploadBranch)을 따른다
    public Dictionary<string, RepoBranchRule> RepoBranches { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// 계약: 옛 이름이다 — 값이 남은 PC 만 JsonSettingsStore.WithDefaults 가 WorkBranch · UploadBranch 로 한 번 옮기고 비운다
    public string? ReceiveBranch { get; set; }

    public string? PushBranch { get; set; }

    /// <summary>저장소 하나의 브랜치 규칙을 적는다 — 둘 다 비면 지워서 공통 값을 따르게 한다.</summary>
    public void AssignBranches(string repoId, string? work, string? upload)
    {
        var rule = new RepoBranchRule { Work = work?.Trim() ?? string.Empty, Upload = upload?.Trim() ?? string.Empty };
        if (rule.IsEmpty)
        {
            RepoBranches.Remove(repoId);
            return;
        }

        RepoBranches[repoId] = rule;
    }

    /// 계약: 사용자가 「작업 중」으로 찍어 둔 저장소 Id — 목록 거르개와 「작업 중만 선택」이 읽는다
    public HashSet<string> WorkingRepos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// 계약: 「공통 무시 목록」에 적어 둔 줄이다 — 저장소의 .git/info/exclude 에 덧붙일 후보일 뿐, 적용은 미리 보기를 거친다
    public List<string> CommonExcludes { get; set; } = [];

    public const string DefaultCommitSubject = "JHJ Git Center {기능} 처리 - {그룹}";

    public const string DefaultCommitBody = "같이 처리한 그룹: {그룹} (저장소 {저장소}개)";

    /// 계약: 예전 기본값이다 — 이 값을 그대로 쥔 PC 만 JsonSettingsStore.WithDefaults 가 새 기본값으로 옮긴다
    public const string LegacyCommitSubject = "chore: 일괄 반영";

    /// 계약: 일괄 커밋의 제목(첫 줄) 기본값 — 자리표 {기능} · {그룹} · {저장소} 는 실행할 때 채운다
    public string CommitMessageTemplate { get; set; } = DefaultCommitSubject;

    /// 계약: 일괄 커밋의 메시지(본문) 기본값 — 비우면 제목만 들어간다. 자리표는 제목과 같다
    public string CommitBodyTemplate { get; set; } = DefaultCommitBody;

    /// 계약: 작업 전에 변경이 있으면 자동으로 stash 했다가 끝나고 되돌릴지
    public bool AutoStashBeforePull { get; set; } = false;

    // ── 화면 ─────────────────────────────────────────────────
    /// "system" | "light" | "dark"
    public string Theme { get; set; } = "system";

    /// "bull" | "navy" | "emerald" | "violet" | "slate"
    public string Palette { get; set; } = "violet";

    /// 계약: "en" | "ko" | "zh" — 기본값은 Core 의 Loc.Default 와 같은 글자다(이 층은 Jhj.Core.Wpf 를 못 문다)
    public string Language { get; set; } = "en";

    /// 계약: 창 전체의 글자·여백 배율 — 0.9 · 1.0 · 1.1 · 1.25 중 하나. 적용하면 바로 반영된다
    public double UiScale { get; set; } = 1.0;

    /// 계약: 저장소 목록·변경 파일 목록의 줄 높이를 줄여 한 화면에 더 담는다
    public bool CompactRows { get; set; } = false;

    /// 계약: 오른쪽 작업 로그 단을 접는다 — 좁은 화면에서 가운데 상세를 넓게 쓴다
    public bool ShowActivityPanel { get; set; } = true;

    /// 계약: 전역 단축키 자리표는 JHJ_DEV/000.AGENTS_MD/060.단축키/060.단축키-대장.md 다 — Git Center 는 3번이다.
    // 왜: App 이 부팅 때 이 값을 HotkeyRules.Default 에도 넣는다 — 층이 반대라 Core 가 이 상수를 못 읽는다
    public const string DefaultHotkey = "Ctrl+Alt+3";

    public string Hotkey { get; set; } = DefaultHotkey;

    /// 계약: 「단축키 2」부터의 추가 칸 — Hotkey 와 합쳐 넷까지다(Core HotkeyService.MaxCount)
    public List<string> ExtraHotkeys { get; set; } = [];

    /// 계약: 창을 열 때마다 저장소 상태를 다시 읽는다 — 로컬만 읽고 원격에 닿지 않는다
    public bool RefreshOnOpen { get; set; } = false;

    public bool StartAtLogin { get; set; } = false;

    public bool MinimizeToTray { get; set; } = true;

    public bool PinTrayIcon { get; set; } = true;

    /// 계약: 아이콘 — ""(내장) · "preset:&lt;키&gt;"(앱이 그리는 모양) · 그림 파일 경로. 창 머리·작업 표시줄·트레이가 함께 바뀐다
    public string AppIcon { get; set; } = "";

    /// 계약: 0 이면 「아직 정한 적 없음」이다 — 창이 사용자 화면의 작업 영역 비율로 잡는다(Core WindowFit)
    public double WindowWidth { get; set; } = 0;

    public double WindowHeight { get; set; } = 0;

    /// 계약: 처음에는 최대화로 연다 — 그 뒤로는 닫을 때의 상태를 따른다
    public bool WindowMaximized { get; set; } = true;

    /// 계약: 창 위치 — NaN 이면 아직 한 번도 안 옮긴 것이라 화면 가운데에 띄운다
    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    /// 계약: 대시보드 「UI 편집」이 저장한 칸 비율 — 열쇠는 격자 이름(rows · top · middle · bottom), 값은 별(*) 무게다
    public Dictionary<string, double[]> DashboardLayout { get; set; } = new(StringComparer.Ordinal);

    /// 계약: 통계 카드마다 고른 표시 방식 — 열쇠는 카드 이름(state · group · change · top · age · branch · lines · calendar)
    /// 계약: 기간이 있는 카드는 「카드 이름.range」 열쇠에 기간을 함께 적는다(calendar.range)
    public Dictionary<string, string> DashboardChartKinds { get; set; } = new(StringComparer.Ordinal);

    /// 계약: 저장소 목록에서 마지막으로 고른 저장소 Id — 다시 열 때 그 줄을 고른다
    public string? LastSelectedRepoId { get; set; }

    /// 계약: 마지막으로 본 화면 — 0 대시보드 · 1 저장소 관리. 다시 열 때 그 화면부터 보인다
    public int StartPage { get; set; } = 0;

    /// 계약: 그룹 안 정렬 — "name" · "state" · "recent". 보던 상태라 「기본값으로」가 건드리지 않는다
    public string RepoSort { get; set; } = "name";

    /// 계약: 저장소 줄을 두 줄로 줄여 보는 「요약 보기」 — 보던 상태라 「기본값으로」가 건드리지 않는다
    public bool RepoSummaryView { get; set; }

    public bool DiagnosticsEnabled { get; set; } = true;

    /// 계약: 예약 자동 업데이트 세 칸 — 화면은 Core 의 UpdatePanel, 규격은 JHJ_CS_CORE/300.Docs/030.설정-창-기본-규격.md §2 다
    public bool AutoUpdateEnabled { get; set; } = true;

    public string AutoUpdateTime { get; set; } = "04:00";

    public bool AutoUpdateNotify { get; set; } = true;

    public void ResetToDefaults(SettingsGroup group)
    {
        var d = new AppSettings();

        if (group is SettingsGroup.Sources or SettingsGroup.All)
        {
            GitHubDesktopEnabled = d.GitHubDesktopEnabled;
            GitHubDesktopDataPath = d.GitHubDesktopDataPath;
            GitHubAccountEnabled = d.GitHubAccountEnabled;
            GitHubOwners = [];
            IncludeUnclonedAccountRepos = d.IncludeUnclonedAccountRepos;
            CommandSources = [];
            RescanMinutes = d.RescanMinutes;
            StatusRefreshMinutes = d.StatusRefreshMinutes;
            AutoFetchMinutes = d.AutoFetchMinutes;
            AutoRelinkBroken = d.AutoRelinkBroken;
        }

        if (group is SettingsGroup.Git or SettingsGroup.All)
        {
            RemoteName = d.RemoteName;
            Concurrency = d.Concurrency;
            GitTimeoutSeconds = d.GitTimeoutSeconds;
            FastForwardOnly = d.FastForwardOnly;
            CommitMessageTemplate = d.CommitMessageTemplate;
            CommitBodyTemplate = d.CommitBodyTemplate;
            WorkBranch = d.WorkBranch;
            UploadBranch = d.UploadBranch;
            AutoStashBeforePull = d.AutoStashBeforePull;
        }

        if (group is SettingsGroup.Display or SettingsGroup.All)
        {
            Theme = d.Theme;
            Palette = d.Palette;
            Language = d.Language;
            UiScale = d.UiScale;
            CompactRows = d.CompactRows;
            ShowActivityPanel = d.ShowActivityPanel;
            Hotkey = d.Hotkey;
            ExtraHotkeys = [.. d.ExtraHotkeys];
            RefreshOnOpen = d.RefreshOnOpen;
            MinimizeToTray = d.MinimizeToTray;
            PinTrayIcon = d.PinTrayIcon;
            AppIcon = d.AppIcon;
        }

        if (group is SettingsGroup.All)
        {
            // 함정: 루트·수동 추가·제외는 사용자가 손으로 모은 값이라 「전부 되돌리기」에서도 지우지 않는다
            StartAtLogin = d.StartAtLogin;
            DiagnosticsEnabled = d.DiagnosticsEnabled;
        }
    }

    /// <summary>저장소·그룹에 손으로 매긴 것을 전부 비운다 — 루트와 출처 설정은 남는다.</summary>
    /// 계약: 「기본값으로」가 일부러 남기는 값(수동 추가 · 숨김 · 그룹)을 지우는 유일한 길이다 — 화면이 먼저 확인을 받는다
    public void ClearMapping()
    {
        Groups = [];
        RepoGroups = new(StringComparer.OrdinalIgnoreCase);
        CollapsedGroups = [];
        ManualPaths = [];
        ExcludedIds = [];
        RepoBranches = new(StringComparer.OrdinalIgnoreCase);
        WorkingRepos = new(StringComparer.OrdinalIgnoreCase);
        LastSelectedRepoId = null;
    }

    /// <summary>모든 칸을 처음 설치한 값으로 되돌린다 — 루트까지 비운다.</summary>
    /// 왜: 칸을 하나씩 적으면 새 설정을 더할 때 빠진다 — 쓸 수 있는 속성을 통째로 옮긴다
    public void ResetEverything()
    {
        var d = new AppSettings { Language = Language };
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p is { CanRead: true, CanWrite: true }))
        {
            property.SetValue(this, property.GetValue(d));
        }
    }

    /// <summary>폴더 하나를 기준 루트로 삼는다 — 그 폴더와 겹치는 루트를 갈아 끼우고, 그 아래 저장소에 손으로 매긴 그룹을 푼다.</summary>
    /// 계약: 폴더 밖 저장소의 그룹과 다른 루트는 건드리지 않는다. 비게 된 사용자 그룹은 함께 지운다
    public void RemapToFolder(string folder, int groupLevel, int depth)
    {
        var rootKey = Services.RepoKey.ForPath(folder);

        ScanRoots.RemoveAll(r =>
        {
            var key = Services.RepoKey.ForPath(r.Path);
            return key == rootKey || Services.RepoKey.IsUnder(rootKey, key);
        });
        ScanRoots.Add(new ScanRoot { Path = folder, Depth = depth, GroupLevel = groupLevel });

        foreach (var id in RepoGroups.Keys.Where(id => Services.RepoKey.IsUnder(rootKey, id)).ToList())
        {
            RepoGroups.Remove(id);
        }

        Groups.RemoveAll(g => !RepoGroups.Values.Contains(g, StringComparer.OrdinalIgnoreCase));
        CollapsedGroups = [];
    }
}

public enum SettingsGroup
{
    Sources,
    Git,
    Display,
    All
}

/// <summary>저장소 하나의 브랜치 규칙 — 빈 칸은 「공통 값을 따른다」는 뜻이다.</summary>
public sealed class RepoBranchRule
{
    public string Work { get; set; } = string.Empty;

    public string Upload { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Work.Length == 0 && Upload.Length == 0;
}

/// <summary>스캔할 루트 폴더 한 줄.</summary>
public sealed class ScanRoot
{
    public required string Path { get; set; }

    /// 계약: 루트에서 몇 단계까지 .git 을 찾을지 — c:\DEV\Github 는 2 면 충분하다(ATIS\ATIS_X\.git)
    public int Depth { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    /// 계약: 이 루트에서 찾은 저장소에 붙일 묶음 이름 — 비면 바로 위 폴더 이름을 쓴다
    public string? Group { get; set; }

    /// 계약: 루트에서 몇 단계 아래 폴더까지를 묶음 이름으로 쓸지 — 0 은 「바로 위 폴더」(옛 동작), 1·2 는 RepoKey.GroupUnder 다
    public int GroupLevel { get; set; }
}

/// <summary>목록을 명령 출력으로 채우는 출처(예: gh repo list · dir /b).</summary>
public sealed class CommandSourceDef
{
    public required string Name { get; set; }

    /// 계약: 실행 파일 이름 — cmd 를 쓰려면 "cmd.exe" 와 Arguments "/c ..." 로 나눠 적는다
    public required string FileName { get; set; }

    public string Arguments { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    /// 계약: 한 줄에 경로 하나를 낸다고 본다 — 줄 앞뒤 공백·따옴표는 떼고 읽는다
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 60;
}
