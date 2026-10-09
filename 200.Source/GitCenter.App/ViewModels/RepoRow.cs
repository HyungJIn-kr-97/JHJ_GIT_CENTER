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

    public string Group => Entry.DisplayGroup;

    public string Path => Entry.Path;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private RepoStatus? _status;

    [ObservableProperty]
    private bool _isWorking;

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
    }

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
