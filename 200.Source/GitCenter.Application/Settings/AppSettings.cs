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
    public int Concurrency { get; set; } = 4;

    /// 계약: 한 저장소의 git 한 번에 줄 제한 시간(초)
    public int GitTimeoutSeconds { get; set; } = 120;

    /// 계약: pull 은 언제나 --ff-only 다 — 끄면 merge 커밋을 허용한다. force push 는 어느 설정으로도 열지 않는다
    public bool FastForwardOnly { get; set; } = true;

    /// 계약: 커밋 창에 처음 채워 넣는 문구
    public string CommitMessageTemplate { get; set; } = "chore: 일괄 반영";

    /// 계약: 작업 전에 변경이 있으면 자동으로 stash 했다가 끝나고 되돌릴지
    public bool AutoStashBeforePull { get; set; } = false;

    // ── 화면 ─────────────────────────────────────────────────
    /// "system" | "light" | "dark"
    public string Theme { get; set; } = "system";

    /// "bull" | "navy" | "emerald" | "violet" | "slate"
    public string Palette { get; set; } = "violet";

    /// "ko" | "en"
    public string Language { get; set; } = "ko";

    public string Hotkey { get; set; } = "Ctrl+Alt+G";

    public bool StartAtLogin { get; set; } = false;

    public bool MinimizeToTray { get; set; } = true;

    public bool PinTrayIcon { get; set; } = true;

    public double WindowWidth { get; set; } = 1480;

    public double WindowHeight { get; set; } = 900;

    /// 계약: 저장소 목록에서 마지막으로 고른 저장소 Id — 다시 열 때 그 줄을 고른다
    public string? LastSelectedRepoId { get; set; }

    public bool DiagnosticsEnabled { get; set; } = true;

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
            AutoStashBeforePull = d.AutoStashBeforePull;
        }

        if (group is SettingsGroup.Display or SettingsGroup.All)
        {
            Theme = d.Theme;
            Palette = d.Palette;
            Language = d.Language;
            Hotkey = d.Hotkey;
            MinimizeToTray = d.MinimizeToTray;
            PinTrayIcon = d.PinTrayIcon;
        }

        if (group is SettingsGroup.All)
        {
            // 함정: 루트·수동 추가·제외는 사용자가 손으로 모은 값이라 「전부 되돌리기」에서도 지우지 않는다
            StartAtLogin = d.StartAtLogin;
            DiagnosticsEnabled = d.DiagnosticsEnabled;
        }
    }
}

public enum SettingsGroup
{
    Sources,
    Git,
    Display,
    All
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
