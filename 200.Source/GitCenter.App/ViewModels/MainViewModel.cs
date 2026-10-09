using System.Collections.ObjectModel;
using System.Windows;
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

        _timer.Tick += async (_, _) => await OnTimerAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public AppSettings Settings { get; }

    public ObservableCollection<RepoRow> Rows { get; } = [];

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public ObservableCollection<FileChange> Changes { get; } = [];

    public ObservableCollection<BranchInfo> Branches { get; } = [];

    public ObservableCollection<ConflictFile> Conflicts { get; } = [];

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
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private string? _sourceNotes;

    // 계약: 0 변경 파일 · 1 브랜치 · 2 충돌 — 충돌이 있으면 그 탭을 먼저 연다
    [ObservableProperty]
    private int _detailTabIndex;

    public string GitVersionText => _gitRunner.IsGitAvailable
        ? _gitRunner.GitVersion ?? string.Empty
        : "git 을 찾지 못했습니다 — PATH 에 git 이 있어야 합니다.";

    public bool IsGitMissing => !_gitRunner.IsGitAvailable;

    public int SelectedCount => _all.Count(r => r.IsSelected && !r.Entry.Excluded);

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
            var keepId = SelectedRow?.Id ?? Settings.LastSelectedRepoId;

            _all.Clear();
            foreach (var entry in refresh.Entries.Where(e => !e.Excluded))
            {
                var row = new RepoRow(entry)
                {
                    IsSelected = selected.Count == 0 || selected.Contains(entry.Id)
                };

                if (previousStatus.TryGetValue(entry.Id, out var status))
                {
                    row.Status = status;
                }

                _all.Add(row);
            }

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
            StatusLine = $"저장소 {_all.Count}개 · 출처 {refresh.Notes.Count} 알림";
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
        }
    }

    private void ApplyFilter()
    {
        var text = FilterText.Trim();
        var keep = _all.Where(row =>
            (!ChangedOnly || row.HasChanges || row.NeedsAttention) &&
            (text.Length == 0
             || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
             || row.Group.Contains(text, StringComparison.OrdinalIgnoreCase)
             || row.Path.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();

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
    }

    private async Task LoadDetailAsync(RepoRow row)
    {
        // 함정: 선택 바뀜과 상태 갱신이 동시에 이 함수를 부르면 Clear 와 Add 가 엇갈려 목록이 두 벌로 들어간다 — 세대 번호로 늦게 온 쪽을 버린다
        var generation = ++_detailGeneration;

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
                    Conflicts.Add(conflict);
                }

                // 왜: 충돌은 다른 무엇보다 먼저 봐야 한다 — 사용자가 탭을 찾아 누르게 두지 않는다
                DetailTabIndex = 2;
            }
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
        foreach (var row in _all)
        {
            row.IsSelected = row.HasChanges || row.NeedsAttention;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    private void SetSelection(IEnumerable<RepoRow> rows, bool value)
    {
        foreach (var row in rows)
        {
            row.IsSelected = value;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private async Task HideSelectedAsync()
    {
        var hidden = _all.Where(r => r.IsSelected).ToList();
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

    private async Task RunBulkAsync(BulkOperationKind kind)
    {
        if (IsBusy || !_gitRunner.IsGitAvailable)
        {
            return;
        }

        var targets = _all.Where(r => r.IsSelected).ToList();
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
            CommitMessage = CommitMessage,
            FastForwardOnly = Settings.FastForwardOnly,
            AutoStash = Settings.AutoStashBeforePull,
            RemoteName = Settings.RemoteName,
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
