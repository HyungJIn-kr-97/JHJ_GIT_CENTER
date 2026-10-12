using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 저장소 목록 왼쪽의 조건 거르개 단 — 그룹 · 상태 · 작업 중 · 브랜치를 조건마다 하나씩 골라 겹쳐 건다.
// 계약: 그룹·상태는 기존 거르개(GroupFilter · StateFilter)를 그대로 쓴다 — 현황 칩·대시보드와 같은 값을 본다.
// 계약: 건수는 거르기 전 전체(_all)에서 센다 — 조건을 걸어도 다른 조건의 수가 줄지 않는다.

/// <summary>거르개 단의 한 줄 — Value 가 null 이면 「전체」다.</summary>
/// 계약: Scope 는 「브랜치」 묶음만 쓴다(공통 설정 · 저장소별 설정) — Level 1 은 그 아래 들여 쓴 줄이다
public sealed record FacetItem(string Facet, string? Value, string Label, int Count, bool IsActive, string? Scope = null, int Level = 0)
{
    public string Mark => IsActive ? "◉" : "○";
}

/// <summary>거르개 단의 한 묶음(접고 펴는 머리 + 줄들).</summary>
public sealed partial class FacetGroup(string key, string title) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<FacetItem> Items { get; } = [];
}

public sealed partial class MainViewModel
{
    private const string MineYes = "작업 중";
    private const string MineNo = "그 외";

    public ObservableCollection<FacetGroup> Facets { get; } =
    [
        new("group", "그룹"), new("state", "상태"), new("mine", "작업"), new("branch", "브랜치")
    ];

    /// 계약: 거르개 단을 접으면 목록이 그 자리까지 넓어진다 — 걸어 둔 조건은 그대로 남는다
    [ObservableProperty]
    private bool _isFacetOpen = true;

    [RelayCommand]
    private void ToggleFacets() => IsFacetOpen = !IsFacetOpen;

    /// 계약: 저장소 줄을 두 줄(이름 · 브랜치와 마지막 결과)로 줄여 한 화면에 더 많이 보인다 — 고르는 즉시 설정에 남는다
    [ObservableProperty]
    private bool _isSummaryView;

    partial void OnIsSummaryViewChanged(bool value)
    {
        Settings.RepoSummaryView = value;
        _ = SaveSettingsAsync();
    }

    /// 계약: null 이면 전체 — 「작업 중」 · 「그 외」
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveFilterText))]
    private string? _mineFilter;

    /// 계약: null 이면 전체 — 줄에 보이는 브랜치 글자(BranchText)와 같은 값이다
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveFilterText))]
    private string? _branchFilter;

    private const string ScopeCommon = "common";
    private const string ScopeOwn = "own";

    /// 계약: null 이면 전체 — 「공통 설정을 따르는 저장소」(common)와 「저장소별 브랜치를 따로 정한 저장소」(own)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveFilterText))]
    private string? _branchScope;

    partial void OnMineFilterChanged(string? value) => ApplyFilter();

    partial void OnBranchFilterChanged(string? value) => ApplyFilter();

    partial void OnBranchScopeChanged(string? value) => ApplyFilter();

    private bool HasOwnBranchRule(RepoRow row) =>
        Settings.RepoBranches.TryGetValue(row.Id, out var rule) && !rule.IsEmpty;

    private static string ScopeLabel(string scope) => scope == ScopeOwn ? Loc.T("저장소별 설정") : Loc.T("공통 설정");

    /// <summary>줄을 누르면 그 조건만 건다 — 「전체」나 이미 걸린 줄을 누르면 푼다.</summary>
    [RelayCommand]
    private void PickFacet(FacetItem? item)
    {
        if (item is null)
        {
            return;
        }

        var value = item.IsActive ? null : item.Value;
        switch (item.Facet)
        {
            case "group": GroupFilter = value; break;
            case "state": StateFilter = value; break;
            case "mine": MineFilter = value; break;
            default:
                // 계약: 브랜치 줄은 묶음(공통 · 저장소별)과 이름을 함께 건다 — 묶음 머리는 묶음만 건다
                BranchScope = item.IsActive ? null : item.Scope;
                BranchFilter = value;
                break;
        }
    }

    private bool MatchesFacets(RepoRow row) =>
        (MineFilter is null || row.IsMine == (MineFilter == MineYes)) &&
        (BranchScope is null || HasOwnBranchRule(row) == (BranchScope == ScopeOwn)) &&
        (BranchFilter is null || row.BranchText == BranchFilter);

    private void RefreshFacets()
    {
        // 계약: 저장소가 아직 없는 빈 그룹도 줄로 세운다 — 만들자마자 안 보이면 거기로 끌어다 놓을 수가 없다
        var counts = _all.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in Settings.Groups)
        {
            counts.TryAdd(name, 0);
        }

        Fill("group", GroupFilter, counts
            .OrderBy(g => GroupRank(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, g.Key.Length == 0 ? "그룹 없음" : g.Key, g.Value)));

        Fill("state", StateFilter, new[]
        {
            ("변경", "변경", _all.Count(r => r.Status?.IsDirty ?? false)),
            ("받을 것", "받을 것", _all.Count(r => r.Behind > 0)),
            ("올릴 것", "올릴 것", _all.Count(r => r.Ahead > 0)),
            ("충돌", "충돌", _all.Count(r => r.Status?.HasConflict ?? false))
        });

        var mine = _all.Count(r => r.IsMine);
        Fill("mine", MineFilter, new[] { (MineYes, MineYes, mine), (MineNo, MineNo, _all.Count - mine) });

        FillBranches();
        OnPropertyChanged(nameof(PushGapCount));
    }

    /// 계약: 「브랜치」는 설정 기준으로 둘로 나눈다 — 공통 값을 따르는 저장소와 저장소별 값(Settings.RepoBranches)을 따로 정한 저장소
    private void FillBranches()
    {
        var group = Facets.First(f => f.Key == "branch");
        group.Items.Clear();
        group.Items.Add(new FacetItem("branch", null, "전체", _all.Count, BranchScope is null && BranchFilter is null));

        foreach (var scope in new[] { ScopeCommon, ScopeOwn })
        {
            var rows = _all.Where(r => HasOwnBranchRule(r) == (scope == ScopeOwn)).ToList();
            group.Items.Add(new FacetItem("branch", null, ScopeLabel(scope), rows.Count,
                BranchScope == scope && BranchFilter is null, scope));

            foreach (var branch in rows.GroupBy(r => r.BranchText, StringComparer.Ordinal)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                group.Items.Add(new FacetItem("branch", branch.Key, branch.Key, branch.Count(),
                    BranchFilter == branch.Key && (BranchScope is null || BranchScope == scope), scope, 1));
            }
        }
    }

    private void Fill(string facet, string? active, IEnumerable<(string Value, string Label, int Count)> rows)
    {
        var group = Facets.First(f => f.Key == facet);
        group.Items.Clear();
        group.Items.Add(new FacetItem(facet, null, "전체", _all.Count, active is null));
        foreach (var (value, label, count) in rows)
        {
            group.Items.Add(new FacetItem(facet, value, label, count, string.Equals(active, value, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ── 작업 중 표시 ─────────────────────────────────────────
    /// <summary>고른 저장소를 「작업 중」으로 찍는다 — 이미 모두 찍혀 있으면 푼다.</summary>
    [RelayCommand]
    private async Task ToggleMineAsync()
    {
        var targets = SelectedShown();
        if (targets.Count == 0)
        {
            AddActivity("작업 중", StepOutcome.Skipped, "먼저 저장소를 고르십시오.");
            return;
        }

        var value = !targets.All(r => r.IsMine);
        foreach (var row in targets)
        {
            row.IsMine = value;
            if (value)
            {
                Settings.WorkingRepos.Add(row.Id);
            }
            else
            {
                Settings.WorkingRepos.Remove(row.Id);
            }
        }

        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectMine()
    {
        _bulkSelecting = true;
        foreach (var row in _all)
        {
            row.IsSelected = row.IsMine;
        }

        _bulkSelecting = false;
        SyncGroupSelection();
    }
}
