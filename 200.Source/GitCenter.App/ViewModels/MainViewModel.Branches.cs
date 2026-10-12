using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 작업·업로드 브랜치(공통 + 저장소별)와 병합해 올 브랜치 고르개, 그리고 줄 하나를 고치는 쪽지(그룹 · 브랜치).
// 계약: 값은 전부 「있는 것 중에서 고른다」 — 브랜치 이름을 손으로 적는 칸은 없다. 빈 값("")은 「따로 정하지 않음」이다.
// 계약: 저장소별 값(Settings.RepoBranches)이 공통 값(Settings.WorkBranch · UploadBranch)을 덮는다 — 일괄 작업이 저장소마다 풀어 쓴다.
// 함정: 고르는 상자는 목록(ItemsSource)이 비워질 때 null 을 되돌려 보낸다 — 공통 목록은 더하기만 하고, 받는 쪽은 null 을 버린다.

/// <summary>고르는 상자의 한 줄 — Value 가 저장되는 값이고 Label 이 보이는 글이다.</summary>
public sealed record BranchOption(string Value, string Label)
{
    // 함정: 이 앱의 고르는 상자 틀은 고른 줄을 ToString 으로 그린다 — 안 덮으면 레코드 모양이 그대로 보인다
    public override string ToString() => Label;
}

public sealed partial class MainViewModel
{
    private readonly HashSet<string> _branchNames = new(StringComparer.Ordinal);
    private DateTimeOffset _branchNamesLoadedAt = DateTimeOffset.MinValue;
    private bool _editLoading;

    public ObservableCollection<BranchOption> WorkBranchOptions { get; } = [new(string.Empty, Loc.T("지금 브랜치 그대로"))];

    public ObservableCollection<BranchOption> UploadBranchOptions { get; } = [new(string.Empty, Loc.T("저장소 기본 브랜치"))];

    public ObservableCollection<BranchOption> MergeSourceOptions { get; } = [new(string.Empty, Loc.T("업로드 브랜치"))];

    /// 계약: 공통 작업 브랜치 — 고르는 즉시 설정에 남는다
    [ObservableProperty]
    private string? _workBranch = string.Empty;

    /// 계약: 공통 업로드 브랜치 — 고르는 즉시 설정에 남는다
    [ObservableProperty]
    private string? _uploadBranch = string.Empty;

    /// 계약: 「병합」이 가져올 브랜치 — 빈 값이면 저장소마다 업로드 브랜치다. 저장하지 않는다(다시 열면 업로드 브랜치)
    [ObservableProperty]
    private string? _mergeSource = string.Empty;

    partial void OnWorkBranchChanged(string? value)
    {
        if (value is null || Settings.WorkBranch == value)
        {
            return;
        }

        Settings.WorkBranch = value;
        _ = SaveSettingsAsync();
    }

    partial void OnUploadBranchChanged(string? value)
    {
        if (value is null || Settings.UploadBranch == value)
        {
            return;
        }

        Settings.UploadBranch = value;
        _ = SaveSettingsAsync();
    }

    private void InitBranchOptions()
    {
        AddBranchNames(Settings.RepoBranches.Values.SelectMany(r => new[] { r.Work, r.Upload })
            .Concat([Settings.UploadBranch, Settings.WorkBranch, "main"]));
        WorkBranch = Settings.WorkBranch;
        UploadBranch = Settings.UploadBranch;
    }

    /// 계약: 아는 브랜치 이름을 세 고르개에 더한다 — 지우지 않는다(고른 값이 풀리지 않게)
    private void AddBranchNames(IEnumerable<string?> names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('(') || !_branchNames.Add(name))
            {
                continue;
            }

            var option = new BranchOption(name, name);
            WorkBranchOptions.Add(option);
            UploadBranchOptions.Add(option);
            MergeSourceOptions.Add(option);
        }
    }

    /// 계약: 상태를 읽을 때마다 지금 브랜치·기본 브랜치를 고르개에 더하고 줄마다 저장소별 규칙 글을 다시 적는다
    private void SyncBranchRules()
    {
        AddBranchNames(_all.SelectMany(r => new[] { r.Status?.IsDetached == true ? null : r.Status?.Branch, r.Status?.DefaultBranch }));

        foreach (var row in _all)
        {
            row.BranchRuleText = Settings.RepoBranches.TryGetValue(row.Id, out var rule) && !rule.IsEmpty
                ? string.Join(" · ", new[]
                {
                    rule.Work.Length == 0 ? null : $"작업 {rule.Work}",
                    rule.Upload.Length == 0 ? null : $"업로드 {rule.Upload}"
                }.Where(t => t is not null))
                : string.Empty;
        }
    }

    /// <summary>고르개를 펼 때 부른다 — 모든 저장소의 브랜치 이름(로컬 + 원격)을 읽어 고를 것을 채운다.</summary>
    /// 계약: 로컬만 읽는다(for-each-ref) — 원격에는 닿지 않는다. 2분 안에 다시 펴면 다시 읽지 않는다
    public async Task LoadBranchNamesAsync()
    {
        if (!_gitRunner.IsGitAvailable || DateTimeOffset.Now - _branchNamesLoadedAt < TimeSpan.FromMinutes(2))
        {
            return;
        }

        _branchNamesLoadedAt = DateTimeOffset.Now;
        var rows = _all.Where(r => r.Entry.Health is RepoHealth.Ok).ToList();
        using var gate = new SemaphoreSlim(Math.Clamp(Settings.Concurrency, 1, 16));

        var lists = await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadBranchNamesAsync(row).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);

        AddBranchNames(lists.SelectMany(l => l).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    /// 계약: 원격 브랜치는 원격 이름을 뗀 이름으로 준다(origin/main → main) — 다른 원격의 것은 뺀다
    private async Task<List<string>> ReadBranchNamesAsync(RepoRow row)
    {
        try
        {
            var prefix = Settings.RemoteName + "/";
            var branches = await _git.ReadBranchesAsync(row.Entry, CancellationToken.None).ConfigureAwait(false);
            return branches
                .Select(b => !b.IsRemote ? b.Name : b.Name.StartsWith(prefix, StringComparison.Ordinal) ? b.Name[prefix.Length..] : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "브랜치 읽기 실패 {Repo}", row.Name);
            return [];
        }
    }

    // ── 줄 하나 고치기(그룹 · 작업 브랜치 · 업로드 브랜치) ─────────────

    /// 계약: 쪽지가 고치는 줄 — 줄을 두 번 누르거나 줄의 톱니를 누르면 채워진다
    [ObservableProperty]
    private RepoRow? _editRow;

    public ObservableCollection<BranchOption> EditGroupOptions { get; } = [];

    public ObservableCollection<BranchOption> EditWorkOptions { get; } = [];

    public ObservableCollection<BranchOption> EditUploadOptions { get; } = [];

    [ObservableProperty]
    private string? _editGroup;

    [ObservableProperty]
    private string? _editWork;

    [ObservableProperty]
    private string? _editUpload;

    /// <summary>쪽지를 이 줄의 값으로 채운다 — 고를 것은 이 저장소에 실제로 있는 브랜치와 지금 있는 그룹이다.</summary>
    public async Task BeginEditAsync(RepoRow row)
    {
        _editLoading = true;
        try
        {
            EditRow = row;
            var names = row.Entry.Health is RepoHealth.Ok ? await ReadBranchNamesAsync(row).ConfigureAwait(true) : [];
            AddBranchNames(names);
            Settings.RepoBranches.TryGetValue(row.Id, out var rule);

            var commonWork = Settings.WorkBranch.Length == 0 ? Loc.T("지금 브랜치 그대로") : Settings.WorkBranch;
            var commonUpload = Settings.UploadBranch.Length == 0 ? Loc.T("저장소 기본 브랜치") : Settings.UploadBranch;
            Fill(EditWorkOptions, $"{Loc.T("공통 설정 따름")} — {commonWork}", names.Append(rule?.Work));
            Fill(EditUploadOptions, $"{Loc.T("공통 설정 따름")} — {commonUpload}", names.Append(rule?.Upload));

            var folder = row.Entry.DisplayGroup;
            Fill(EditGroupOptions,
                folder.Length == 0 ? Loc.T("그룹 없음") : $"{folder} — {Loc.T("폴더 이름 그대로")}",
                Settings.Groups.Concat(_all.Select(r => r.Group))
                    .Where(g => !g.Equals(folder, StringComparison.OrdinalIgnoreCase)));

            // 함정: 목록을 비울 때 상자가 null 을 되돌려 보낸 뒤라 같은 값을 넣어도 알림이 안 나갈 수 있다 — null 을 거쳐 넣는다
            EditWork = null;
            EditUpload = null;
            EditGroup = null;
            EditWork = rule?.Work ?? string.Empty;
            EditUpload = rule?.Upload ?? string.Empty;
            EditGroup = row.UserGroup ?? string.Empty;
        }
        finally
        {
            _editLoading = false;
        }
    }

    private static void Fill(ObservableCollection<BranchOption> target, string emptyLabel, IEnumerable<string?> names)
    {
        target.Clear();
        target.Add(new BranchOption(string.Empty, emptyLabel));
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            target.Add(new BranchOption(name!, name!));
        }
    }

    partial void OnEditWorkChanged(string? value) => ApplyEditBranches();

    partial void OnEditUploadChanged(string? value) => ApplyEditBranches();

    private void ApplyEditBranches()
    {
        if (_editLoading || EditRow is not { } row || EditWork is null || EditUpload is null)
        {
            return;
        }

        Settings.AssignBranches(row.Id, EditWork, EditUpload);
        SyncBranchRules();
        RefreshFacets();
        _ = SaveSettingsAsync();
    }

    partial void OnEditGroupChanged(string? value)
    {
        if (_editLoading || EditRow is not { } row || value is null || (row.UserGroup ?? string.Empty) == value)
        {
            return;
        }

        _ = MoveRowsToGroupAsync([row], value);
    }

    /// 계약: 끌기 시작한 줄이 체크돼 있으면 보이는 줄 중 체크한 것을 다 옮기고, 아니면 그 줄 하나만 옮긴다
    public IReadOnlyList<RepoRow> RowsForDrag(RepoRow row) => row.IsSelected ? SelectedShown() : [row];

    /// <summary>저장소들을 그룹으로 옮긴다 — 빈 이름이면 지정을 풀어 스캔이 매긴 폴더 이름으로 돌려보낸다.</summary>
    public async Task MoveRowsToGroupAsync(IReadOnlyList<RepoRow> rows, string? group)
    {
        var name = group?.Trim() ?? string.Empty;
        var moved = 0;

        foreach (var row in rows)
        {
            // 왜: 스캔이 매긴 폴더 그룹으로 돌려놓는 것은 지정이 아니라 지정을 푸는 것이다 — 폴더 이름이 사용자 그룹으로 굳지 않게 한다
            var back = name.Length == 0 || name.Equals(row.Entry.DisplayGroup, StringComparison.OrdinalIgnoreCase);
            if (back ? !row.HasUserGroup : string.Equals(row.UserGroup, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Settings.AssignGroup(row.Id, back ? null : name);
            row.UserGroup = back ? null : name;
            moved++;
        }

        if (moved == 0)
        {
            return;
        }

        AddActivity("그룹", StepOutcome.Success, $"저장소 {moved}개를 「{(name.Length == 0 ? "폴더 이름 그룹" : name)}」(으)로 옮겼습니다.");
        RefreshGroupChips();
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }
}
