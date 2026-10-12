using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Application.Abstractions;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 커밋·Push 앞뒤의 확인 층 둘(그림은 Views/OffBranchLayer · PushGapLayer).
// ① 앞 — 지금 브랜치가 작업 브랜치가 아닌 저장소를 모아 「이 브랜치 그대로 진행할지」 저장소별로 묻는다.
// ② 뒤 — 커밋은 됐는데 Push 되지 않은 저장소를 저장소별로 보인다(단추로도 연다).
// 계약: 둘 다 읽기뿐이다 — 층이 git 을 바꾸지 않고, 체크한 저장소만 BulkOperationOptions.ConfirmedBranches 로 넘긴다.
// 계약: 체크는 기본이 꺼짐이다 — 그대로 「진행」하면 예전처럼 건너뛴다.

/// <summary>작업 브랜치가 아닌 저장소 한 줄 — IsChecked 면 지금 브랜치 그대로 커밋·Push 한다.</summary>
public sealed partial class OffBranchRow(RepoRow row, string current, string work) : ObservableObject
{
    public RepoRow Row { get; } = row;

    public string Name => Row.Name;

    public string Group => Row.Group;

    public string Current { get; } = current;

    public string Work { get; } = work;

    public string StateText { get; } = MainViewModel.PushStateText(row);

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>Push 되지 않은 저장소 한 줄.</summary>
/// 계약: IsConflict 면 병합이 멈춰 있는 저장소다 — 「받아서 Merge + Push」가 건드리지 않고, 누르면 「충돌」 탭으로 간다
public sealed record PushGapRow(RepoRow Row, string StateText, bool IsConflict = false)
{
    public string Name => Row.Name;

    public string Group => Row.Group;

    public string Branch => Row.BranchText;

    public string Result => Row.LastResult ?? string.Empty;
}

public sealed partial class MainViewModel
{
    private TaskCompletionSource<bool>? _offBranchAnswer;
    private IReadOnlyDictionary<string, string> _lastConfirmed = new Dictionary<string, string>();

    /// 계약: 층에 충돌 난 저장소가 있으면 안내 줄을 보인다
    [ObservableProperty]
    private bool _pushGapHasConflict;

    /// 계약: 받아서 올릴 수 있는(충돌이 아닌) 저장소가 있을 때만 「받아서 Merge + Push」를 보인다
    [ObservableProperty]
    private bool _pushGapCanSync;

    public ObservableCollection<OffBranchRow> OffBranchRows { get; } = [];

    public ObservableCollection<PushGapRow> PushGapRows { get; } = [];

    [ObservableProperty]
    private bool _isOffBranchOpen;

    [ObservableProperty]
    private string _offBranchCaption = string.Empty;

    [ObservableProperty]
    private bool _isPushGapOpen;

    [ObservableProperty]
    private string _pushGapCaption = string.Empty;

    /// 계약: 커밋은 있는데 원격에 없는 저장소 수 — 띠의 「Push 전」 단추가 보인다
    public int PushGapCount => _all.Count(IsPushGap);

    // 왜: 원격이 없는 로컬 전용 저장소는 올릴 곳이 없다 — 「Push 전」으로 세지 않는다
    private static bool IsPushGap(RepoRow row) =>
        row.Status is { IsDetached: false } status && row.Entry.Health is not RepoHealth.NoRemote
        && (status.Ahead > 0 || status.Upstream is null);

    internal static string PushStateText(RepoRow row)
    {
        if (row.Status is not { } status)
        {
            return string.Empty;
        }

        var changed = status.Staged + status.Unstaged + status.Untracked;
        return string.Join(" · ", new[]
        {
            changed > 0 ? Loc.T("변경 {0}", changed) : null,
            status.Upstream is null ? Loc.T("{0} 를 올린 적 없음", status.Branch)
                : status.Ahead > 0 ? Loc.T("{0} 로 Push 전 {1}", status.Upstream, status.Ahead) : null
        }.Where(t => t is not null));
    }

    // ── ① 작업 브랜치가 아닌 저장소 확인 ─────────────────────

    /// <summary>커밋·Push 전에 부른다 — 취소하면 null, 아니면 「저장소 Id → 확인한 브랜치」를 준다(없으면 빈 것).</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ConfirmOffBranchAsync(
        BulkOperationKind kind, IReadOnlyList<RepoRow> targets, BulkOperationOptions options, bool sync = false)
    {
        var confirmed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!sync && kind is not (BulkOperationKind.CommitAll or BulkOperationKind.Push or BulkOperationKind.CommitAndPush))
        {
            return confirmed;
        }

        // 왜: 할 일이 없는 저장소까지 물으면 목록이 길어진다 — 커밋할 변경이나 올릴 커밋이 있는 것만 묻는다
        // 계약: sync(층의 「받아서 Merge + Push」)는 층이 고른 저장소 전부를 묻는다 — 받은 답은 이어지는 Push 가 그대로 쓴다
        var suspects = targets.Where(r => r.Status is { IsDetached: false } s
            && options.WorkBranchFor(r.Entry) is { Length: > 0 } work && work != s.Branch
            && (sync || (kind is not BulkOperationKind.Push && s.IsDirty) || (kind is not BulkOperationKind.CommitAll && IsPushGap(r))));

        OffBranchRows.Clear();
        foreach (var row in suspects)
        {
            if (await _git.ReadOffWorkBranchAsync(row.Entry, options, CancellationToken.None).ConfigureAwait(true) is { } off)
            {
                OffBranchRows.Add(new OffBranchRow(row, off.Current, off.Work));
            }
        }

        if (OffBranchRows.Count == 0)
        {
            return confirmed;
        }

        OffBranchCaption = Loc.T("{0} — 작업 브랜치가 아닌 저장소 {1}개",
            sync ? Loc.T("받아서 Merge + Push") : Describe(kind), OffBranchRows.Count);
        _offBranchAnswer = new TaskCompletionSource<bool>();
        IsOffBranchOpen = true;
        var go = await _offBranchAnswer.Task.ConfigureAwait(true);
        IsOffBranchOpen = false;
        _offBranchAnswer = null;

        if (!go)
        {
            return null;
        }

        foreach (var row in OffBranchRows.Where(r => r.IsChecked))
        {
            confirmed[row.Row.Id] = row.Current;
        }

        return confirmed;
    }

    [RelayCommand]
    private void ProceedOffBranch() => _offBranchAnswer?.TrySetResult(true);

    [RelayCommand]
    private void CancelOffBranch() => _offBranchAnswer?.TrySetResult(false);

    /// <summary>모두 체크한다 — 이미 모두 체크돼 있으면 푼다.</summary>
    [RelayCommand]
    private void CheckAllOffBranch()
    {
        var value = !OffBranchRows.All(r => r.IsChecked);
        foreach (var row in OffBranchRows)
        {
            row.IsChecked = value;
        }
    }

    // ── ② Push 되지 않은 저장소 ──────────────────────────────

    [RelayCommand]
    private void OpenPushGap() => ShowPushGap(_all.Where(IsPushGap));

    [RelayCommand]
    private void ClosePushGap() => IsPushGapOpen = false;

    private void ShowPushGap(IEnumerable<RepoRow> rows)
    {
        PushGapRows.Clear();
        foreach (var row in rows.OrderBy(r => r.Group, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var conflict = row.Status?.HasConflict ?? false;
            PushGapRows.Add(new PushGapRow(row,
                conflict ? Loc.T("충돌 — 「충돌」 탭에서 풀어 주십시오") : PushStateText(row), conflict));
        }

        PushGapHasConflict = PushGapRows.Any(r => r.IsConflict);
        PushGapCanSync = PushGapRows.Any(r => !r.IsConflict);

        PushGapCaption = PushGapRows.Count == 0
            ? Loc.T("Push 되지 않은 저장소가 없습니다.")
            : Loc.T("커밋과 Push 상태가 다른 저장소 {0}개", PushGapRows.Count);
        IsPushGapOpen = true;
    }

    /// 계약: Push 가 낀 일괄 작업 뒤에만 저절로 뜬다 — 커밋만 했을 때는 「Push 전」이 당연해 띄우지 않는다
    private void ShowPushGapAfter(BulkOperationKind kind, IReadOnlyList<RepoRow> targets)
    {
        if (kind is not (BulkOperationKind.Push or BulkOperationKind.CommitAndPush))
        {
            return;
        }

        var left = targets.Where(r => IsPushGap(r)
            || (kind is BulkOperationKind.CommitAndPush && r.Status is { IsDetached: false, IsDirty: true })).ToList();
        if (left.Count > 0)
        {
            ShowPushGap(left);
        }
    }

    /// <summary>층의 저장소를 그 자리에서 받아(원격의 같은 브랜치를 Merge) 이어서 Push 한다 — 충돌 난 저장소는 올리지 않고 따로 보인다.</summary>
    /// 계약: Merge 가 충돌·실패한 저장소는 Push 로 넘기지 않는다 — 끝나면 남은 것과 충돌 난 것을 층에 다시 모아 안내한다
    [RelayCommand]
    private async Task SyncPushGapAsync()
    {
        var targets = PushGapRows.Where(r => !r.IsConflict).Select(r => r.Row).ToList();
        IsPushGapOpen = false;
        if (targets.Count == 0)
        {
            return;
        }

        var merged = await RunBulkAsync(BulkOperationKind.MergeDefaultIntoCurrent, targets, ownRemote: true, showGap: false).ConfigureAwait(true);
        if (merged is null)
        {
            return;
        }

        var ready = merged.Where(r => r.Outcome is StepOutcome.Success or StepOutcome.Skipped)
            .Select(r => r.RepoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pushable = targets.Where(r => ready.Contains(r.Id) && !(r.Status?.HasConflict ?? false)).ToList();
        if (pushable.Count > 0)
        {
            await RunBulkAsync(BulkOperationKind.Push, pushable, preset: _lastConfirmed, showGap: false).ConfigureAwait(true);
        }

        var conflicts = targets.Count(r => r.Status?.HasConflict ?? false);
        if (conflicts > 0)
        {
            NudgeAction = BulkNextAction.Conflict;
            AddActivity(Loc.T("받아서 Merge + Push"), StepOutcome.Conflict,
                $"충돌 {conflicts}개 저장소는 올리지 않았습니다 — 「충돌」 탭에서 풀고 「병합 마무리」 뒤 다시 Push 해 주십시오.");
        }

        var left = targets.Where(r => IsPushGap(r) || (r.Status?.HasConflict ?? false)).ToList();
        if (left.Count > 0)
        {
            ShowPushGap(left);
        }
    }

    [RelayCommand]
    private void ShowPushGapRepo(PushGapRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsPushGapOpen = false;
        GoToRepo(row.Row);
    }

    /// <summary>목록의 체크를 이 층의 저장소들로 바꾼다 — 바로 이어서 Push 를 누를 수 있게 한다.</summary>
    [RelayCommand]
    private void SelectPushGap()
    {
        var ids = PushGapRows.Select(r => r.Row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 왜: 일괄 작업은 보이는 줄에만 돈다 — 거르개에 가려진 저장소가 있으면 거르개를 푼다
        if (PushGapRows.Any(r => !Rows.Contains(r.Row)))
        {
            ResetFilters();
        }

        _bulkSelecting = true;
        foreach (var row in _all)
        {
            row.IsSelected = ids.Contains(row.Id);
        }

        _bulkSelecting = false;
        SyncGroupSelection();
        IsPushGapOpen = false;
    }
}
