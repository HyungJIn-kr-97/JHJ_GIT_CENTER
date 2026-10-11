using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Services;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

/// <summary>
/// 가운데 화면 전체를 끌고 가는 모델 — 목록·상태·일괄 작업·로그가 여기 모인다.
/// 계약: 원격에 닿는 작업은 사용자가 단추를 눌렀을 때만 돈다. 자동으로 도는 것은 「상태 읽기」와 설정에서 켠 자동 Fetch 뿐이다.
/// 함정: 긴 작업 중에 목록을 다시 훑으면 줄이 바뀌어 결과가 엉뚱한 줄에 붙는다 — IsBusy 동안 다시 훑기를 막는다.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly RepoRegistry _registry;
    private readonly IGitService _git;
    private readonly BulkOperationRunner _runner;
    private readonly IGitRunner _gitRunner;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };

    private readonly List<RepoRow> _all = [];
    private CancellationTokenSource? _work;
    private DateTimeOffset _lastRescan = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStatus = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private int _detailGeneration;

    public MainViewModel(
        ISettingsStore settingsStore,
        AppSettings settings,
        RepoRegistry registry,
        IGitService git,
        BulkOperationRunner runner,
        IGitRunner gitRunner)
    {
        _settingsStore = settingsStore;
        _registry = registry;
        _git = git;
        _runner = runner;
        _gitRunner = gitRunner;
        Settings = settings;
        CommitMessage = settings.CommitMessageTemplate;
        CommitBody = settings.CommitBodyTemplate;
        ReceiveBranch = settings.ReceiveBranch;
        PushBranch = settings.PushBranch;
        PageIndex = Math.Clamp(settings.StartPage, 0, 1);
        SortMode = settings.RepoSort is "state" or "recent" ? settings.RepoSort : "name";
        InitDashboardCharts();

        // 계약: 목록은 RepoRow.GroupNode 객체로 묶인다 — 그룹 머리가 그 객체(접힘·건수)를 그대로 그린다
        CollectionViewSource.GetDefaultView(Rows).GroupDescriptions
            .Add(new PropertyGroupDescription(nameof(RepoRow.GroupNode)));

        _timer.Tick += async (_, _) => await OnTimerAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public AppSettings Settings { get; }

    /// <summary>창이 직접 그려야 하는 설정(배율 · 조밀 모드 · 작업 로그 단)이 바뀌었다.</summary>
    /// 계약: 설정 창의 「적용」이 NotifyDisplaySettingsChanged 로 알린다 — MainWindow 가 받아 다시 맞춘다
    public event Action? DisplaySettingsChanged;

    public void NotifyDisplaySettingsChanged()
    {
        // 왜: Settings 는 자체 알림이 없는 보통 객체다 — 경로 바인딩({Binding Settings.CompactRows})을 다시 읽히려면 여기서 알려야 한다
        OnPropertyChanged(nameof(Settings));
        DisplaySettingsChanged?.Invoke();
    }

    public ObservableCollection<RepoRow> Rows { get; } = [];

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public ObservableCollection<FileChange> Changes { get; } = [];

    public ObservableCollection<BranchInfo> Branches { get; } = [];

    public ObservableCollection<ConflictFileVm> Conflicts { get; } = [];

    /// 계약: 고른 변경 파일의 diff 줄 — 오른쪽 「변경 내용」 단이 그린다
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    /// 계약: 사용자가 만든 그룹 + 「그룹 없음」 — 거르개 칩과 옮기기 메뉴가 함께 쓴다
    public ObservableCollection<GroupChip> GroupChips { get; } = [];

    /// 계약: 도구줄의 현황 칩 — 저장소 · 변경 · 받을 것 · 올릴 것 · 충돌
    public ObservableCollection<StatChip> Stats { get; } = [];

    /// 계약: 저장소가 하나라도 든 그룹 — 목록의 그룹 머리와 대시보드의 그룹 카드가 같은 객체를 쓴다
    public ObservableCollection<GroupNode> Groups { get; } = [];

    /// 계약: 대시보드 「확인 필요」 — 문제·받을 것·올릴 것·변경 차례로 12개까지
    public ObservableCollection<RepoRow> Attention { get; } = [];

    public ObservableCollection<RepoRow> RecentCommits { get; } = [];

    private readonly Dictionary<string, GroupNode> _groupNodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _bulkSelecting;

    // ── 화면 틀 ──────────────────────────────────────────────
    // 계약: 0 대시보드 · 1 저장소 관리 — 제목 띠의 탭이 바꾼다
    [ObservableProperty]
    private int _pageIndex;

    // 계약: 다시 열 때 되살리는 화면은 0·1 뿐이다 — 「커밋 내역」(2)은 누른 것이 있어야 뜻이 있다
    partial void OnPageIndexChanged(int value)
    {
        if (value <= 1)
        {
            Settings.StartPage = value;
        }
    }

    /// 계약: 오른쪽 단(변경 내용·작업 로그)은 접힌 채로 시작한다 — 변경 파일을 누르거나 가장자리 단추로 편다
    [ObservableProperty]
    private bool _isRightPanelOpen;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    /// <summary>접힌 오른쪽 단을 펴면서 그 탭(0 변경 내용 · 1 작업 로그)을 고른다.</summary>
    [RelayCommand]
    private void ShowRightTab(string? index)
    {
        RightTabIndex = index == "1" ? 1 : 0;
        IsRightPanelOpen = true;
    }

    [ObservableProperty]
    private string _lastStatusText = "아직 상태를 읽지 않았습니다";

    // ── 거르개 · 정렬 ────────────────────────────────────────
    /// 계약: null 이면 전체 — 「변경」·「받을 것」·「올릴 것」·「충돌」 중 하나. 현황 칩을 누르면 바뀐다
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveFilterText))]
    private string? _stateFilter;

    partial void OnStateFilterChanged(string? value)
    {
        ApplyFilter();
        UpdateStatusLine();
    }

    // 계약: "name" · "state" · "recent" — 그룹 안에서만 줄을 세운다(그룹 차례는 안 바뀐다)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    private string _sortMode = "name";

    public string SortLabel => SortMode switch
    {
        "state" => "상태순",
        "recent" => "최근 커밋순",
        _ => "이름순"
    };

    public bool HasActiveFilter => GroupFilter is not null || StateFilter is not null || MineFilter is not null || BranchFilter is not null;

    public string ActiveFilterText => string.Join(" · ", new[]
    {
        GroupFilter is null ? null : "그룹 " + (GroupFilter.Length == 0 ? "그룹 없음" : GroupFilter),
        StateFilter is null ? null : "상태 " + StateFilter,
        MineFilter,
        BranchFilter is null ? null : "브랜치 " + BranchFilter
    }.Where(part => part is not null));

    [RelayCommand]
    private void PickStat(StatChip? chip) =>
        StateFilter = chip is null || chip.Tone.Length == 0 || chip.IsActive ? null : chip.Tone;

    [RelayCommand]
    private void ClearFilters()
    {
        GroupFilter = null;
        StateFilter = null;
        MineFilter = null;
        BranchFilter = null;
    }

    [RelayCommand]
    private async Task CycleSortAsync()
    {
        SortMode = SortMode switch { "name" => "state", "state" => "recent", _ => "name" };
        Settings.RepoSort = SortMode;
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ExpandAllGroups() => SetExpanded(true);

    [RelayCommand]
    private void CollapseAllGroups() => SetExpanded(false);

    private void SetExpanded(bool value)
    {
        foreach (var node in Groups)
        {
            node.IsExpanded = value;
        }
    }

    // ── 대시보드에서 건너가기 ────────────────────────────────
    [RelayCommand]
    private void GoToState(StatChip? chip)
    {
        ResetFilters();
        StateFilter = chip is null || chip.Tone.Length == 0 ? null : chip.Tone;
        PageIndex = 1;
    }

    // 왜: 남아 있던 검색어·거르개와 겹치면 건너간 곳이 빈 목록으로 보인다 — 건너갈 때는 그 하나만 건다
    private void ResetFilters()
    {
        FilterText = string.Empty;
        ChangedOnly = false;
        ClearFilters();
    }

    [RelayCommand]
    private void GoToGroup(GroupNode? node)
    {
        if (node is null)
        {
            return;
        }

        ResetFilters();
        node.IsExpanded = true;
        GroupFilter = node.Key;
        PageIndex = 1;
    }

    [RelayCommand]
    private void GoToRepo(RepoRow? row)
    {
        if (row is null)
        {
            return;
        }

        // 왜: 거르개에 걸려 안 보이는 줄로 건너가면 아무것도 안 고른 것처럼 보인다 — 거르개를 풀고 간다
        if (!Rows.Contains(row))
        {
            ResetFilters();
        }

        if (row.GroupNode is { } node)
        {
            node.IsExpanded = true;
        }

        SelectedRow = row;
        PageIndex = 1;
    }

    [ObservableProperty]
    private RepoRow? _selectedRow;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _changedOnly;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private int _progressDone;

    [ObservableProperty]
    private int _progressTotal;

    [ObservableProperty]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    private string _commitBody = string.Empty;

    /// 계약: 하단에서 고치는 즉시 설정에 남는다 — 비우면 받기는 기본 브랜치, 올리기는 지금 브랜치다
    [ObservableProperty]
    private string _receiveBranch = string.Empty;

    [ObservableProperty]
    private string _pushBranch = string.Empty;

    partial void OnReceiveBranchChanged(string value)
    {
        if (Settings.ReceiveBranch != value.Trim())
        {
            Settings.ReceiveBranch = value.Trim();
            _ = SaveSettingsAsync();
        }
    }

    partial void OnPushBranchChanged(string value)
    {
        if (Settings.PushBranch != value.Trim())
        {
            Settings.PushBranch = value.Trim();
            _ = SaveSettingsAsync();
        }
    }

    /// 함정: 중괄호가 든 글은 XAML 마크업 확장 안에 못 적는다 — 말풍선 문구를 여기서 건넨다
    public string CommitTokenHint => "자리표 — {기능} 누른 작업 · {그룹} 고른 저장소의 그룹 · {저장소} 고른 저장소 수";

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private string? _sourceNotes;

    // 계약: 0 변경 파일 · 1 브랜치 · 2 충돌 — 충돌이 있으면 그 탭을 먼저 연다
    [ObservableProperty]
    private int _detailTabIndex;

    // ── 변경 내용(diff) ──────────────────────────────────────
    // 계약: 0 변경 내용 · 1 작업 로그 — 변경 파일을 누르면 0 으로 옮긴다
    [ObservableProperty]
    private int _rightTabIndex;

    [ObservableProperty]
    private FileChange? _selectedChange;

    [ObservableProperty]
    private string _diffTitle = string.Empty;

    [ObservableProperty]
    private string _diffSummary = string.Empty;

    [ObservableProperty]
    private string? _diffNote;

    [ObservableProperty]
    private bool _isDiffLoading;

    private int _diffGeneration;

    partial void OnSelectedChangeChanged(FileChange? value)
    {
        if (value is null)
        {
            ClearDiff();
            return;
        }

        // 왜: 파일을 누른 사람은 그 내용을 보려는 것이다 — 탭을 찾아 누르게 두지 않는다
        RightTabIndex = 0;
        IsRightPanelOpen = true;
        _ = LoadDiffAsync(SelectedRow, value);
    }

    private void ClearDiff()
    {
        _diffGeneration++;
        DiffLines.Clear();
        DiffTitle = string.Empty;
        DiffSummary = string.Empty;
        DiffNote = null;
        IsDiffLoading = false;
    }

    private async Task LoadDiffAsync(RepoRow? row, FileChange change)
    {
        // 함정: 저장소를 바꾸면 선택이 잠깐 엇갈린다 — 세대 번호로 늦게 온 결과를 버린다
        var generation = ++_diffGeneration;

        DiffLines.Clear();
        DiffTitle = change.Path;
        DiffSummary = string.Empty;
        DiffNote = null;

        if (row is null || row.Entry.Health != RepoHealth.Ok || !_gitRunner.IsGitAvailable)
        {
            DiffNote = "이 저장소는 변경 내용을 읽을 수 없습니다.";
            return;
        }

        IsDiffLoading = true;
        try
        {
            var diff = await _git.ReadDiffAsync(row.Entry, change, CancellationToken.None).ConfigureAwait(true);
            if (generation != _diffGeneration)
            {
                return;
            }

            foreach (var line in diff.Lines)
            {
                DiffLines.Add(line);
            }

            DiffSummary = diff.Lines.Count == 0 ? string.Empty : $"+{diff.Added} · -{diff.Removed}";
            DiffNote = diff.Note;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "변경 내용 읽기 실패 {Path}", change.Path);
            if (generation == _diffGeneration)
            {
                DiffNote = ex.Message;
            }
        }
        finally
        {
            if (generation == _diffGeneration)
            {
                IsDiffLoading = false;
            }
        }
    }

    // ── 그룹 ─────────────────────────────────────────────────

    /// 계약: null 이면 전체 — 칩을 누르면 이 값이 바뀌고 거르기가 다시 돈다
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveFilterText))]
    private string? _groupFilter;

    [ObservableProperty]
    private string _newGroupName = string.Empty;

    partial void OnGroupFilterChanged(string? value)
    {
        ApplyFilter();
        RefreshGroupChips();
    }

    /// <summary>칩을 눌러 그 그룹만 본다 — 눌린 칩을 다시 누르면 전체로 돌아간다.</summary>
    [RelayCommand]
    private void PickGroup(GroupChip? chip) =>
        GroupFilter = chip is null || chip.IsActive ? null : chip.Key;

    /// <summary>고른(체크한) 저장소를 그룹으로 옮긴다. 빈 이름이면 그룹에서 뺀다.</summary>
    [RelayCommand]
    private async Task MoveSelectedToGroupAsync(string? group)
    {
        var targets = SelectedShown();
        if (targets.Count == 0)
        {
            AddActivity("그룹", StepOutcome.Skipped, "먼저 옮길 저장소를 고르십시오.");
            return;
        }

        foreach (var row in targets)
        {
            Settings.AssignGroup(row.Id, group);
            row.UserGroup = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        }

        var where = string.IsNullOrWhiteSpace(group) ? "그룹 없음" : group.Trim();
        AddActivity("그룹", StepOutcome.Success, $"저장소 {targets.Count}개를 「{where}」(으)로 옮겼습니다.");

        RefreshGroupChips();
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>적어 넣은 이름으로 새 그룹을 만들고 고른 저장소를 거기에 넣는다.</summary>
    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        NewGroupName = string.Empty;
        await MoveSelectedToGroupAsync(name).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return;
        }

        Settings.RemoveGroup(group);
        foreach (var row in _all.Where(r => string.Equals(r.UserGroup, group, StringComparison.OrdinalIgnoreCase)))
        {
            row.UserGroup = null;
        }

        if (string.Equals(GroupFilter, group, StringComparison.OrdinalIgnoreCase))
        {
            GroupFilter = null;
        }

        AddActivity("그룹", StepOutcome.Success, $"그룹 「{group}」을(를) 지웠습니다 — 저장소는 그대로입니다.");
        RefreshGroupChips();
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// 계약: 저장소에 붙은 그룹과 설정의 그룹 목록을 합쳐 칩을 다시 만든다
    private void RefreshGroupChips()
    {
        var counts = _all.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var names = Settings.Groups
            .Concat(_all.Select(r => r.Group).Where(g => g.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Settings.Groups.FindIndex(x => x.Equals(g, StringComparison.OrdinalIgnoreCase)) is var at && at >= 0 ? at : int.MaxValue)
            .ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        GroupChips.Clear();
        GroupChips.Add(new GroupChip(null, "전체", _all.Count, GroupFilter is null, false));
        foreach (var name in names)
        {
            var isUser = Settings.Groups.Contains(name, StringComparer.OrdinalIgnoreCase);
            GroupChips.Add(new GroupChip(
                name, name, counts.TryGetValue(name, out var n) ? n : 0,
                string.Equals(GroupFilter, name, StringComparison.OrdinalIgnoreCase), isUser));
        }
    }

    public string GitVersionText => _gitRunner.IsGitAvailable
        ? _gitRunner.GitVersion ?? string.Empty
        : "git 을 찾지 못했습니다 — PATH 에 git 이 있어야 합니다.";

    public bool IsGitMissing => !_gitRunner.IsGitAvailable;

    public int SelectedCount => Rows.Count(r => r.IsSelected && !r.Entry.Excluded);

    /// 계약: 일괄 작업·옮기기·숨기기는 「보이는 줄」 중 고른 것에만 돈다 — 거르개에 가려진 저장소는 체크돼 있어도 건드리지 않는다
    private List<RepoRow> SelectedShown() => Rows.Where(r => r.IsSelected).ToList();

    // ── 목록 ─────────────────────────────────────────────────

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "저장소 목록을 훑는 중…";

        try
        {
            var refresh = await _registry.RefreshAsync(Settings, CancellationToken.None).ConfigureAwait(true);
            _lastRescan = DateTimeOffset.Now;

            var selected = _all.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousStatus = _all.ToDictionary(r => r.Id, r => r.Status, StringComparer.OrdinalIgnoreCase);
            var previousRefreshed = _all.ToDictionary(r => r.Id, r => r.RefreshedAt, StringComparer.OrdinalIgnoreCase);
            var keepId = SelectedRow?.Id ?? Settings.LastSelectedRepoId;

            _all.Clear();
            foreach (var entry in refresh.Entries.Where(e => !e.Excluded))
            {
                var row = new RepoRow(entry)
                {
                    IsSelected = selected.Count == 0 || selected.Contains(entry.Id)
                };

                // 계약: 사용자가 지정한 그룹이 스캔이 매긴 폴더 이름을 덮는다
                if (Settings.RepoGroups.TryGetValue(entry.Id, out var group))
                {
                    row.UserGroup = group;
                }

                row.IsMine = Settings.WorkingRepos.Contains(entry.Id);

                if (previousStatus.TryGetValue(entry.Id, out var status))
                {
                    row.Status = status;
                    previousRefreshed.TryGetValue(entry.Id, out var at);
                    row.RefreshedAt = at;
                }

                row.PropertyChanged += OnRowPropertyChanged;
                _all.Add(row);
            }

            RefreshGroupChips();

            SourceNotes = refresh.Notes.Count == 0
                ? null
                : string.Join("  ·  ", refresh.Notes.Select(n => $"{Describe(n.Key)}: {n.Value}"));

            if (refresh.Relinked.Count > 0)
            {
                Log.Information("다시 이은 저장소 {Count}개", refresh.Relinked.Count);
                AddActivity("목록", StepOutcome.Success, $"경로가 바뀐 저장소 {refresh.Relinked.Count}개를 다시 이었습니다: {string.Join(", ", refresh.Relinked)}");
            }

            ApplyFilter();
            SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId) ?? Rows.FirstOrDefault();
            UpdateStatusLine();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "목록 훑기 실패");
            AddActivity("목록", StepOutcome.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        await RefreshStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        if (!_gitRunner.IsGitAvailable || _all.Count == 0)
        {
            return;
        }

        var rows = _all.Where(r => r.Entry.Health is RepoHealth.Ok).ToList();
        ProgressTotal = rows.Count;
        ProgressDone = 0;
        BusyText = "상태를 읽는 중…";

        using var gate = new SemaphoreSlim(Math.Clamp(Settings.Concurrency, 1, 16));
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                row.IsWorking = true;
                var status = await _git.ReadStatusAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
                row.Status = status;
                row.CommitDays = await _git.ReadCommitDaysAsync(row.Entry, CalendarDays, CancellationToken.None).ConfigureAwait(true);
                // 계약: 줄마다 「언제 읽었나」를 남긴다 — 목록에 경과로, 상세에 정확한 시각으로 보인다
                row.RefreshedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "상태 읽기 실패 {Repo}", row.Name);
            }
            finally
            {
                row.IsWorking = false;
                ProgressDone++;
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);

        _lastStatus = DateTimeOffset.Now;
        LastStatusText = $"상태를 읽은 때 {_lastStatus:HH:mm:ss}";
        BusyText = string.Empty;
        ProgressTotal = 0;
        ApplyFilter();
        UpdateStatusLine();

        if (SelectedRow is not null)
        {
            await LoadDetailAsync(SelectedRow).ConfigureAwait(true);
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnChangedOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedRowChanged(RepoRow? value)
    {
        Settings.LastSelectedRepoId = value?.Id;
        if (value is not null)
        {
            _ = LoadDetailAsync(value);
        }
        else
        {
            Changes.Clear();
            Branches.Clear();
            Conflicts.Clear();
            SelectedChange = null;
        }
    }

    private void ApplyFilter()
    {
        var text = FilterText.Trim();

        // 계약: Rows 에 넣기 전에 줄마다 GroupNode 를 채운다 — 목록이 그 객체로 묶는다
        foreach (var row in _all)
        {
            row.GroupNode = NodeOf(row.Group);
        }

        // 함정: 묶인 목록의 그룹 차례는 「처음 나온 줄」 차례다 — 그룹별로 모아 세워 넣어야 머리가 흩어지지 않는다
        var keep = Sorted(_all.Where(row =>
            (!ChangedOnly || row.HasChanges || row.NeedsAttention) &&
            (GroupFilter is null || row.Group.Equals(GroupFilter, StringComparison.OrdinalIgnoreCase)) &&
            MatchesState(row) &&
            MatchesFacets(row) &&
            (text.Length == 0
             || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
             || row.Group.Contains(text, StringComparison.OrdinalIgnoreCase)
             || row.Path.Contains(text, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        RefreshGroups(keep);
        RefreshFacets();

        // 함정: Rows 를 비우면 ListBox 가 SelectedItem 을 null 로 되돌려 보낸다 — 거르기·상태 갱신마다 선택이 풀렸다
        var previous = SelectedRow;

        Rows.Clear();
        foreach (var row in keep)
        {
            Rows.Add(row);
        }

        SelectedRow = previous is not null && keep.Contains(previous) ? previous : keep.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void UpdateStatusLine()
    {
        var dirty = _all.Count(r => r.Status?.IsDirty ?? false);
        var behind = _all.Count(r => (r.Status?.Behind ?? 0) > 0);
        var ahead = _all.Count(r => (r.Status?.Ahead ?? 0) > 0);
        var conflict = _all.Count(r => r.Status?.HasConflict ?? false);

        StatusLine = $"저장소 {_all.Count} · 변경 {dirty} · 받을 것 {behind} · 올릴 것 {ahead} · 충돌 {conflict}";

        // 왜: 한 줄 문장보다 칩이 빨리 읽힌다 — 0 인 칸은 색 점을 빼 조용히 둔다
        Stats.Clear();
        Stats.Add(new StatChip("저장소", _all.Count, string.Empty, false, StateFilter is null));
        Stats.Add(new StatChip("변경", dirty, "변경", dirty > 0, StateFilter == "변경"));
        Stats.Add(new StatChip("받을 것", behind, "받을 것", behind > 0, StateFilter == "받을 것"));
        Stats.Add(new StatChip("올릴 것", ahead, "올릴 것", ahead > 0, StateFilter == "올릴 것"));
        Stats.Add(new StatChip("충돌", conflict, "충돌", conflict > 0, StateFilter == "충돌"));

        RefreshDashboard();
    }

    private bool MatchesState(RepoRow row) => StateFilter switch
    {
        "변경" => row.Status?.IsDirty ?? false,
        "받을 것" => row.Behind > 0,
        "올릴 것" => row.Ahead > 0,
        "충돌" => row.Status?.HasConflict ?? false,
        _ => true
    };

    private IEnumerable<RepoRow> Sorted(IEnumerable<RepoRow> rows)
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        var byGroup = rows.OrderBy(r => GroupRank(r.Group)).ThenBy(r => r.Group, names);

        return SortMode switch
        {
            "state" => byGroup.ThenBy(r => r.Severity).ThenBy(r => r.Name, names),
            "recent" => byGroup.ThenByDescending(r => r.LastCommitWhen ?? DateTimeOffset.MinValue),
            _ => byGroup.ThenBy(r => r.Name, names)
        };
    }

    // 계약: 사용자가 만든 그룹이 적은 차례대로 먼저, 스캔이 매긴 이름이 가나다순으로 그 뒤, 「그룹 없음」이 맨 끝
    private int GroupRank(string group)
    {
        if (group.Length == 0)
        {
            return int.MaxValue;
        }

        var at = Settings.Groups.FindIndex(g => g.Equals(group, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? at : int.MaxValue - 1;
    }

    private GroupNode NodeOf(string group)
    {
        if (_groupNodes.TryGetValue(group, out var node))
        {
            return node;
        }

        node = new GroupNode(group)
        {
            IsExpanded = !Settings.CollapsedGroups.Contains(group, StringComparer.OrdinalIgnoreCase)
        };

        node.SelectionRequested += OnGroupSelectionRequested;
        node.PropertyChanged += OnGroupNodeChanged;
        _groupNodes[group] = node;
        return node;
    }

    private void OnGroupSelectionRequested(GroupNode node, bool value)
    {
        _bulkSelecting = true;
        foreach (var row in _all.Where(r => ReferenceEquals(r.GroupNode, node)))
        {
            row.IsSelected = value;
        }

        _bulkSelecting = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void OnGroupNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GroupNode.IsExpanded) || sender is not GroupNode node)
        {
            return;
        }

        Settings.CollapsedGroups.RemoveAll(g => g.Equals(node.Key, StringComparison.OrdinalIgnoreCase));
        if (!node.IsExpanded)
        {
            Settings.CollapsedGroups.Add(node.Key);
        }

        _ = SaveSettingsAsync();
    }

    // 왜: 줄의 체크 상자는 RepoRow 를 직접 바꾼다 — 여기서 받아야 선택 수와 그룹 체크가 따라온다
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkSelecting || e.PropertyName != nameof(RepoRow.IsSelected) || sender is not RepoRow row)
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        if (row.GroupNode is { } node)
        {
            node.SyncAllSelected(_all.Where(r => ReferenceEquals(r.GroupNode, node)).All(r => r.IsSelected));
        }
    }

    private void RefreshGroups(IReadOnlyCollection<RepoRow> shown)
    {
        var live = new List<GroupNode>();
        foreach (var members in Sorted(_all).GroupBy(r => r.GroupNode!))
        {
            var node = members.Key;
            node.IsUserGroup = Settings.Groups.Contains(node.Key, StringComparer.OrdinalIgnoreCase);
            node.Total = members.Count();
            node.Shown = shown.Count(r => ReferenceEquals(r.GroupNode, node));
            node.Dirty = members.Count(r => r.Status?.IsDirty ?? false);
            node.Behind = members.Count(r => r.Behind > 0);
            node.Ahead = members.Count(r => r.Ahead > 0);
            node.Problem = members.Count(r => r.NeedsAttention);
            node.SyncAllSelected(members.All(r => r.IsSelected));
            live.Add(node);
        }

        if (!Groups.SequenceEqual(live))
        {
            Groups.Clear();
            foreach (var node in live)
            {
                Groups.Add(node);
            }
        }
    }

    private void RefreshDashboard()
    {
        var names = StringComparer.CurrentCultureIgnoreCase;

        Attention.Clear();
        foreach (var row in _all.Where(r => r.Severity <= 3).OrderBy(r => r.Severity).ThenBy(r => r.Name, names).Take(12))
        {
            Attention.Add(row);
        }

        RecentCommits.Clear();
        foreach (var row in _all.Where(r => r.LastCommitWhen is not null).OrderByDescending(r => r.LastCommitWhen).Take(10))
        {
            RecentCommits.Add(row);
        }

        RefreshCharts();
    }

    private async Task LoadDetailAsync(RepoRow row)
    {
        // 함정: 선택 바뀜과 상태 갱신이 동시에 이 함수를 부르면 Clear 와 Add 가 엇갈려 목록이 두 벌로 들어간다 — 세대 번호로 늦게 온 쪽을 버린다
        var generation = ++_detailGeneration;

        // 계약: 저장소가 바뀌면 보던 diff 는 더 이상 그 저장소 것이 아니다 — 먼저 비운다
        SelectedChange = null;
        Changes.Clear();
        Branches.Clear();
        Conflicts.Clear();

        if (row.Entry.Health != RepoHealth.Ok || !_gitRunner.IsGitAvailable)
        {
            return;
        }

        try
        {
            var changes = await _git.ReadChangesAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
            var branches = await _git.ReadBranchesAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);

            if (generation != _detailGeneration || !ReferenceEquals(SelectedRow, row))
            {
                return;
            }

            Changes.Clear();
            Branches.Clear();
            Conflicts.Clear();

            foreach (var change in changes)
            {
                Changes.Add(change);
            }

            foreach (var branch in branches)
            {
                Branches.Add(branch);
            }

            DetailTabIndex = 0;

            if (row.Status?.HasConflict ?? false)
            {
                var conflicts = await _git.ReadConflictsAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
                if (generation != _detailGeneration)
                {
                    return;
                }

                foreach (var conflict in conflicts)
                {
                    Conflicts.Add(new ConflictFileVm(conflict));
                }

                // 왜: 충돌은 다른 무엇보다 먼저 봐야 한다 — 사용자가 탭을 찾아 누르게 두지 않는다
                DetailTabIndex = 2;
            }

            OnPropertyChanged(nameof(CanConcludeMerge));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "상세 읽기 실패 {Repo}", row.Name);
        }
    }

    // ── 선택 ─────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll() => SetSelection(Rows, true);

    [RelayCommand]
    private void SelectNone() => SetSelection(_all, false);

    [RelayCommand]
    private void SelectChanged()
    {
        _bulkSelecting = true;
        foreach (var row in _all)
        {
            row.IsSelected = row.HasChanges || row.NeedsAttention;
        }

        _bulkSelecting = false;
        SyncGroupSelection();
    }

    private void SetSelection(IEnumerable<RepoRow> rows, bool value)
    {
        _bulkSelecting = true;
        foreach (var row in rows)
        {
            row.IsSelected = value;
        }

        _bulkSelecting = false;
        SyncGroupSelection();
    }

    private void SyncGroupSelection()
    {
        foreach (var node in Groups)
        {
            node.SyncAllSelected(_all.Where(r => ReferenceEquals(r.GroupNode, node)).All(r => r.IsSelected));
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private async Task HideSelectedAsync()
    {
        var hidden = SelectedShown();
        if (hidden.Count == 0)
        {
            return;
        }

        foreach (var row in hidden)
        {
            if (!Settings.ExcludedIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
            {
                Settings.ExcludedIds.Add(row.Id);
            }
        }

        await SaveSettingsAsync().ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);
    }

    // ── 일괄 작업 ────────────────────────────────────────────

    [RelayCommand]
    private Task FetchAsync() => RunBulkAsync(BulkOperationKind.Fetch);

    [RelayCommand]
    private Task UpdateDefaultAsync() => RunBulkAsync(BulkOperationKind.UpdateDefaultBranch);

    [RelayCommand]
    private Task MergeDefaultAsync() => RunBulkAsync(BulkOperationKind.MergeDefaultIntoCurrent);

    [RelayCommand]
    private Task PullCurrentAsync() => RunBulkAsync(BulkOperationKind.PullCurrent);

    [RelayCommand]
    private Task UpdateAllBranchesAsync() => RunBulkAsync(BulkOperationKind.UpdateAllTrackedBranches);

    [RelayCommand]
    private Task CommitAsync() => RunBulkAsync(BulkOperationKind.CommitAll);

    [RelayCommand]
    private Task PushAsync() => RunBulkAsync(BulkOperationKind.Push);

    [RelayCommand]
    private Task CommitAndPushAsync() => RunBulkAsync(BulkOperationKind.CommitAndPush);

    [RelayCommand]
    private Task AbortAsync() => RunBulkAsync(BulkOperationKind.AbortOperation);

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    /// 계약: 작업 로그는 화면에만 쌓인다 — 지워도 파일 로그(logs\)는 그대로 남는다
    [RelayCommand]
    private void ClearActivity() => Activity.Clear();

    private async Task RunBulkAsync(BulkOperationKind kind)
    {
        if (IsBusy || !_gitRunner.IsGitAvailable)
        {
            return;
        }

        var targets = SelectedShown();
        if (targets.Count == 0)
        {
            AddActivity("일괄", StepOutcome.Skipped, "고른 저장소가 없습니다.");
            return;
        }

        if (kind is BulkOperationKind.CommitAll or BulkOperationKind.CommitAndPush && string.IsNullOrWhiteSpace(CommitMessage))
        {
            AddActivity("일괄", StepOutcome.Skipped, "커밋 메시지를 적어 주십시오.");
            return;
        }

        _work = new CancellationTokenSource();
        IsBusy = true;
        BusyText = $"{Describe(kind)} — 저장소 {targets.Count}개";
        ProgressTotal = targets.Count;
        ProgressDone = 0;

        foreach (var row in targets)
        {
            row.LastResult = null;
            row.LastOutcome = null;
            row.IsWorking = true;
        }

        var options = new BulkOperationOptions
        {
            CommitMessage = FillCommitText(CommitMessage, kind, targets),
            CommitBody = FillCommitText(CommitBody, kind, targets),
            FastForwardOnly = Settings.FastForwardOnly,
            AutoStash = Settings.AutoStashBeforePull,
            RemoteName = Settings.RemoteName,
            ReceiveBranch = ReceiveBranch.Trim(),
            PushBranch = PushBranch.Trim(),
            TimeoutSeconds = Settings.GitTimeoutSeconds
        };

        var byId = targets.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var progress = new Progress<BulkStepResult>(step =>
        {
            ProgressDone++;
            if (byId.TryGetValue(step.RepoId, out var row))
            {
                row.IsWorking = false;
                row.LastResult = step.Message;
                row.LastOutcome = step.Outcome;
            }

            Activity.Insert(0, ActivityEntry.From(step));
            TrimActivity();
        });

        try
        {
            var results = await _runner
                .RunAsync(targets.Select(r => r.Entry).ToList(), kind, options, Settings.Concurrency, progress, _work.Token)
                .ConfigureAwait(true);

            var ok = results.Count(r => r.Outcome == StepOutcome.Success);
            var conflict = results.Count(r => r.Outcome == StepOutcome.Conflict);
            var failed = results.Count(r => r.Outcome == StepOutcome.Failed);

            AddActivity(Describe(kind),
                failed > 0 ? StepOutcome.Failed : conflict > 0 ? StepOutcome.Conflict : StepOutcome.Success,
                $"끝 — 성공 {ok} · 충돌 {conflict} · 실패 {failed} · 건너뜀 {results.Count - ok - conflict - failed}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "일괄 작업 실패 {Kind}", kind);
            AddActivity(Describe(kind), StepOutcome.Failed, ex.Message);
        }
        finally
        {
            foreach (var row in targets)
            {
                row.IsWorking = false;
            }

            IsBusy = false;
            BusyText = string.Empty;
            ProgressTotal = 0;
            _work?.Dispose();
            _work = null;
        }

        await RefreshStatusAsync().ConfigureAwait(true);
    }

    // ── 바깥 도구로 열기 ─────────────────────────────────────

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedRow is { Path.Length: > 0 } row && !ShellOpener.OpenFolder(row.Path))
        {
            AddActivity(row.Name, StepOutcome.Failed, "폴더를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (SelectedRow is { Path.Length: > 0 } row && !ShellOpener.OpenTerminal(row.Path))
        {
            AddActivity(row.Name, StepOutcome.Failed, "터미널을 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void OpenDesktop()
    {
        if (SelectedRow is { Path.Length: > 0 } row && !ShellOpener.OpenInGitHubDesktop(row.Path))
        {
            AddActivity(row.Name, StepOutcome.Failed, "GitHub Desktop 을 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void OpenCode()
    {
        if (SelectedRow is { Path.Length: > 0 } row && !ShellOpener.OpenInVsCode(row.Path))
        {
            AddActivity(row.Name, StepOutcome.Failed, "VS Code 를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void OpenRemote()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var url = ShellOpener.ToBrowserUrl(row.Entry.RemoteUrl);
        if (url is null || !ShellOpener.OpenUrl(url))
        {
            AddActivity(row.Name, StepOutcome.Failed, "원격 주소를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void CopyPath() => CopyText(SelectedRow?.Path, "경로");

    [RelayCommand]
    private void CopyBranch() => CopyText(SelectedRow?.Status?.Branch, "브랜치 이름");

    // 함정: 다른 프로그램이 클립보드를 쥐고 있으면 SetText 가 예외를 던진다 — 삼키고 로그에만 남긴다
    private void CopyText(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            AddActivity(SelectedRow?.Name ?? "복사", StepOutcome.Success, $"{what}을(를) 복사했습니다: {text}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "클립보드 복사 실패");
        }
    }

    // ── 거들개 ────────────────────────────────────────────────

    public async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(Settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "설정 저장 실패");
        }
    }

    public void AddActivity(string scope, StepOutcome outcome, string message)
    {
        Activity.Insert(0, new ActivityEntry
        {
            When = DateTimeOffset.Now,
            RepoName = scope,
            Outcome = outcome,
            Message = message
        });

        TrimActivity();
    }

    /// 함정: 로그를 끝없이 쌓으면 긴 세션에서 화면이 느려진다 — 500줄에서 자른다
    private void TrimActivity()
    {
        while (Activity.Count > 500)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }

    private async Task OnTimerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var now = DateTimeOffset.Now;

        // 왜: 「3분 전」은 값이 그대로여도 시계가 흐르면 틀린 글자가 된다 — 1분마다 다시 그린다
        foreach (var row in _all)
        {
            row.TouchElapsed();
        }

        if (Settings.RescanMinutes > 0 && now - _lastRescan >= TimeSpan.FromMinutes(Settings.RescanMinutes))
        {
            await RescanAsync().ConfigureAwait(true);
            return;
        }

        if (Settings.StatusRefreshMinutes > 0 && now - _lastStatus >= TimeSpan.FromMinutes(Settings.StatusRefreshMinutes))
        {
            await RefreshStatusAsync().ConfigureAwait(true);
        }

        // 계약: 자동 Fetch 는 설정에서 켠 사람만 쓴다 — 기본은 꺼져 있어 원격에 저절로 닿지 않는다
        if (Settings.AutoFetchMinutes > 0 && now - _lastFetch >= TimeSpan.FromMinutes(Settings.AutoFetchMinutes))
        {
            _lastFetch = now;
            await RunBulkAsync(BulkOperationKind.Fetch).ConfigureAwait(true);
        }
    }

    /// 계약: 자리표 {기능} · {그룹} · {저장소} 를 누른 작업과 고른 저장소로 채운다 — 글상자의 원문은 건드리지 않는다
    private static string FillCommitText(string text, BulkOperationKind kind, IReadOnlyList<RepoRow> targets)
    {
        var groups = targets.Select(r => r.GroupNode?.Label).Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.Ordinal);
        return text
            .Replace("{기능}", Describe(kind).Replace(" + ", "&", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{그룹}", string.Join(", ", groups), StringComparison.Ordinal)
            .Replace("{저장소}", targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Trim();
    }

    public static string Describe(BulkOperationKind kind) => kind switch
    {
        BulkOperationKind.Fetch => "Fetch",
        BulkOperationKind.UpdateDefaultBranch => "main 받기",
        BulkOperationKind.MergeDefaultIntoCurrent => "현재로 병합",
        BulkOperationKind.PullCurrent => "현재 브랜치 Pull",
        BulkOperationKind.UpdateAllTrackedBranches => "모든 추적 브랜치 갱신",
        BulkOperationKind.CommitAll => "커밋",
        BulkOperationKind.Push => "올리기",
        BulkOperationKind.CommitAndPush => "커밋 + 올리기",
        _ => "병합 되돌리기"
    };

    private static string Describe(RepoSourceKind kind) => kind switch
    {
        RepoSourceKind.RootScan => "루트 스캔",
        RepoSourceKind.Manual => "수동 추가",
        RepoSourceKind.GitHubDesktop => "GitHub Desktop",
        RepoSourceKind.GitHubAccount => "GitHub 계정",
        _ => "명령 출처"
    };
}

/// <summary>도구줄의 현황 칩 하나.</summary>
/// 계약: Tone 은 StateBrushConverter 가 아는 한 단어다 — 「변경」·「받을 것」·「올릴 것」·「충돌」
/// 계약: IsActive 는 그 상태로 목록을 거르는 중이라는 뜻이다 — Tone 이 빈 「저장소」 칩은 거르개가 없을 때 켜진다
public sealed record StatChip(string Label, int Value, string Tone, bool ShowDot, bool IsActive);

/// <summary>저장소 목록 위의 그룹 거르개 칩 하나.</summary>
/// 계약: Key 가 null 이면 「전체」다. IsUserGroup 이 true 일 때만 지울 수 있다(스캔이 매긴 이름은 못 지운다)
public sealed record GroupChip(string? Key, string Label, int Count, bool IsActive, bool IsUserGroup);

/// <summary>오른쪽 작업 로그의 한 줄.</summary>
public sealed class ActivityEntry
{
    public required DateTimeOffset When { get; init; }

    public required string RepoName { get; init; }

    public required StepOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public string Detail { get; init; } = string.Empty;

    public string TimeText => When.ToString("HH:mm:ss");

    public bool HasDetail => Detail.Trim().Length > 0;

    public static ActivityEntry From(BulkStepResult step) => new()
    {
        When = step.StartedAt,
        RepoName = step.RepoName,
        Outcome = step.Outcome,
        Message = $"{MainViewModel.Describe(step.Kind)} — {step.Message}",
        Detail = step.ConflictPaths.Count > 0
            ? string.Join(Environment.NewLine, step.ConflictPaths) + Environment.NewLine + step.Detail
            : step.Detail
    };
}
