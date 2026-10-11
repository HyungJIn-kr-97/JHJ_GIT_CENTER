using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 저장소 목록 왼쪽의 조건 거르개 단 — 그룹 · 상태 · 작업 중 · 브랜치를 조건마다 하나씩 골라 겹쳐 건다.
// 계약: 그룹·상태는 기존 거르개(GroupFilter · StateFilter)를 그대로 쓴다 — 현황 칩·대시보드와 같은 값을 본다.
// 계약: 건수는 거르기 전 전체(_all)에서 센다 — 조건을 걸어도 다른 조건의 수가 줄지 않는다.

/// <summary>거르개 단의 한 줄 — Value 가 null 이면 「전체」다.</summary>
public sealed record FacetItem(string Facet, string? Value, string Label, int Count, bool IsActive)
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

    partial void OnMineFilterChanged(string? value) => ApplyFilter();

    partial void OnBranchFilterChanged(string? value) => ApplyFilter();

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
            default: BranchFilter = value; break;
        }
    }

    private bool MatchesFacets(RepoRow row) =>
        (MineFilter is null || row.IsMine == (MineFilter == MineYes)) &&
        (BranchFilter is null || row.BranchText == BranchFilter);

    private void RefreshFacets()
    {
        Fill("group", GroupFilter, _all.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => GroupRank(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, g.Key.Length == 0 ? "그룹 없음" : g.Key, g.Count())));

        Fill("state", StateFilter, new[]
        {
            ("변경", "변경", _all.Count(r => r.Status?.IsDirty ?? false)),
            ("받을 것", "받을 것", _all.Count(r => r.Behind > 0)),
            ("올릴 것", "올릴 것", _all.Count(r => r.Ahead > 0)),
            ("충돌", "충돌", _all.Count(r => r.Status?.HasConflict ?? false))
        });

        var mine = _all.Count(r => r.IsMine);
        Fill("mine", MineFilter, new[] { (MineYes, MineYes, mine), (MineNo, MineNo, _all.Count - mine) });

        Fill("branch", BranchFilter, _all.GroupBy(r => r.BranchText, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Key, g.Count())));
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
