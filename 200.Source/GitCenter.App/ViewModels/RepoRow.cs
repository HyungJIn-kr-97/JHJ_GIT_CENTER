using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

/// <summary>목록의 한 줄 — 저장소 신원(RepoEntry)과 지금 상태(RepoStatus)를 함께 들고 있다.</summary>
public sealed partial class RepoRow : ObservableObject
{
    public RepoRow(RepoEntry entry)
    {
        Entry = entry;
        IsSelected = entry.Selected;
    }

    public RepoEntry Entry { get; }

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    /// 계약: 사용자가 지정한 그룹이 있으면 그것, 없으면 스캔이 매긴 폴더 이름
    [ObservableProperty]
    private string? _userGroup;

    public string Group => string.IsNullOrWhiteSpace(UserGroup) ? Entry.DisplayGroup : UserGroup!;

    /// 계약: 사용자가 손으로 넣은 그룹인가 — 화면이 그 그룹만 「뺄 수 있는 것」으로 보인다
    public bool HasUserGroup => !string.IsNullOrWhiteSpace(UserGroup);

    /// 계약: 사용자가 「작업 중」으로 찍은 저장소다(Settings.WorkingRepos) — IsWorking(일괄 작업이 도는 중)과는 다른 뜻이다
    [ObservableProperty]
    private bool _isMine;

    /// 계약: 목록을 묶는 열쇠 — MainViewModel.ApplyFilter 가 Rows 에 넣기 전에 반드시 채운다(null 이면 그룹 머리가 못 그린다)
    [ObservableProperty]
    private GroupNode? _groupNode;

    public string Path => Entry.Path;

    /// 계약: 이 저장소의 상태를 마지막으로 읽은 때 — 「상태 새로 고침」이 줄마다 찍는다
    [ObservableProperty]
    private DateTimeOffset? _refreshedAt;

    partial void OnUserGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(HasUserGroup));
    }

    partial void OnRefreshedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(RefreshedText));

    /// <summary>「방금 · 3분 전」처럼 사람이 읽는 경과.</summary>
    /// 왜: 절대 시각은 지금과 견주려면 머리를 써야 한다 — 목록에서는 경과가 바로 읽힌다
    public string RefreshedText
    {
        get
        {
            if (RefreshedAt is not { } at)
            {
                return string.Empty;
            }

            var span = DateTimeOffset.Now - at;
            if (span < TimeSpan.FromSeconds(45))
            {
                return "방금";
            }

            if (span < TimeSpan.FromHours(1))
            {
                return $"{(int)span.TotalMinutes}분 전";
            }

            if (span < TimeSpan.FromDays(1))
            {
                return $"{(int)span.TotalHours}시간 전";
            }

            return at.ToString("MM-dd HH:mm");
        }
    }

    /// 계약: 말풍선·상세에 쓰는 정확한 시각
    public string RefreshedExactText => RefreshedAt is { } at
        ? $"상태를 읽은 때 {at:yyyy-MM-dd HH:mm:ss}"
        : "아직 상태를 읽지 않았습니다";

    /// <summary>경과 글자를 다시 그리게 한다 — 시계만 흐르고 값은 그대로일 때 부른다.</summary>
    public void TouchElapsed()
    {
        OnPropertyChanged(nameof(RefreshedText));
        OnPropertyChanged(nameof(LastCommitLine));
    }

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        if (span < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)span.TotalMinutes)}분 전";
        }

        if (span < TimeSpan.FromDays(1))
        {
            return $"{(int)span.TotalHours}시간 전";
        }

        return span < TimeSpan.FromDays(60) ? $"{(int)span.TotalDays}일 전" : at.ToString("yyyy-MM-dd");
    }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private RepoStatus? _status;

    [ObservableProperty]
    private bool _isWorking;

    /// 계약: 이 저장소만의 브랜치 규칙을 한 줄로 — 공통 값을 따르면 빈 글이다(MainViewModel.SyncBranchRules 가 적는다)
    [ObservableProperty]
    private string _branchRuleText = string.Empty;

    /// 계약: 마지막 일괄 작업 결과 한 줄 — 다음 작업을 시작하면 지워진다
    [ObservableProperty]
    private string? _lastResult;

    [ObservableProperty]
    private StepOutcome? _lastOutcome;

    partial void OnIsSelectedChanged(bool value) => Entry.Selected = value;

    partial void OnStatusChanged(RepoStatus? value)
    {
        OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(AheadBehindText));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(LastCommitText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(Ahead));
        OnPropertyChanged(nameof(Behind));
        OnPropertyChanged(nameof(SyncNote));
        OnPropertyChanged(nameof(LastCommitLine));
        OnPropertyChanged(nameof(LastCommitWhen));
        OnPropertyChanged(nameof(Severity));
    }

    /// 계약: 최근 1년의 커밋 날짜(커밋 하나에 하나) — 「상태 새로 고침」이 채우고 대시보드 「커밋 캘린더」가 센다
    public IReadOnlyList<DateOnly> CommitDays { get; set; } = [];

    public int Ahead => Status?.Ahead ?? 0;

    public int Behind => Status?.Behind ?? 0;

    /// 계약: 올릴 것·받을 것 뱃지가 둘 다 없을 때 그 자리에 적는 한 단어 — 뱃지가 있으면 비운다
    public string SyncNote => Status is null
        ? string.Empty
        : Status.Upstream is null ? "추적 없음" : Ahead == 0 && Behind == 0 ? "최신" : string.Empty;

    public DateTimeOffset? LastCommitWhen => Status?.LastCommitWhen;

    /// <summary>「3일 전 · 커밋 제목」 — 목록과 대시보드가 함께 쓴다.</summary>
    public string LastCommitLine => Status?.LastCommitWhen is { } at
        ? $"{Ago(at)} · {Status.LastCommitSubject}"
        : string.Empty;

    /// 계약: 작을수록 먼저 봐야 한다 — 0 문제·충돌 · 1 받을 것 · 2 올릴 것 · 3 변경 · 4 깨끗함
    public int Severity => StateText switch
    {
        "깨끗함" => 4,
        "변경" => 3,
        "올릴 것" => 2,
        "받을 것" => 1,
        "읽는 중" => 5,
        _ => 0
    };

    public string SourceText => string.Join(" · ", Entry.Sources.Select(ShortSource));

    public string HealthText => Entry.Health switch
    {
        RepoHealth.Ok => string.Empty,
        RepoHealth.PathMissing => "경로 없음",
        RepoHealth.NotGitRepo => "git 아님",
        RepoHealth.NoRemote => "원격 없음",
        RepoHealth.NotCloned => "미 clone",
        _ => "읽기 실패"
    };

    public string BranchText => Status is null
        ? "…"
        : Status.IsDetached ? "(분리됨)" : Status.Branch;

    public string AheadBehindText
    {
        get
        {
            if (Status is null)
            {
                return string.Empty;
            }

            if (Status.Upstream is null)
            {
                return "추적 없음";
            }

            if (Status.Ahead == 0 && Status.Behind == 0)
            {
                return "최신";
            }

            var parts = new List<string>();
            if (Status.Ahead > 0)
            {
                parts.Add("올릴 것 " + Status.Ahead);
            }

            if (Status.Behind > 0)
            {
                parts.Add("받을 것 " + Status.Behind);
            }

            return string.Join(" · ", parts);
        }
    }

    public string ChangeText
    {
        get
        {
            if (Status is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (Status.Conflicted > 0)
            {
                parts.Add("충돌 " + Status.Conflicted);
            }

            if (Status.Staged > 0)
            {
                parts.Add("스테이지 " + Status.Staged);
            }

            if (Status.Unstaged > 0)
            {
                parts.Add("수정 " + Status.Unstaged);
            }

            if (Status.Untracked > 0)
            {
                parts.Add("새 파일 " + Status.Untracked);
            }

            if (Status.StashCount > 0)
            {
                parts.Add("stash " + Status.StashCount);
            }

            if (Status.Insertions + Status.Deletions > 0)
            {
                parts.Add($"+{Status.Insertions:N0} −{Status.Deletions:N0}줄");
            }

            return parts.Count == 0 ? "깨끗함" : string.Join(" · ", parts);
        }
    }

    public string LastCommitText => Status?.LastCommitWhen is null
        ? string.Empty
        : $"{Status.LastCommitWhen:yyyy-MM-dd} {Status.LastCommitSubject}";

    /// 계약: 줄 왼쪽의 상태 점이 쓰는 한 단어 — 색은 StateColorConverter 가 정한다
    public string StateText
    {
        get
        {
            if (Entry.Health != RepoHealth.Ok)
            {
                return HealthText;
            }

            if (Status is null)
            {
                return "읽는 중";
            }

            if (Status.Error is not null)
            {
                return "오류";
            }

            if (Status.HasConflict)
            {
                return "충돌";
            }

            if (Status.Behind > 0)
            {
                return "받을 것";
            }

            if (Status.Ahead > 0)
            {
                return "올릴 것";
            }

            return Status.IsDirty ? "변경" : "깨끗함";
        }
    }

    public bool HasChanges => Status is not null && (Status.IsDirty || Status.Ahead > 0 || Status.Behind > 0);

    public bool NeedsAttention => Entry.Health != RepoHealth.Ok || Status?.Error is not null || (Status?.HasConflict ?? false);

    private static string ShortSource(RepoSourceKind kind) => kind switch
    {
        RepoSourceKind.RootScan => "스캔",
        RepoSourceKind.Manual => "수동",
        RepoSourceKind.GitHubDesktop => "Desktop",
        RepoSourceKind.GitHubAccount => "GitHub",
        _ => "명령"
    };
}
