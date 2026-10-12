using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 상세 「브랜치」 탭 — 브랜치를 고르면 그 브랜치의 커밋 이력을 옆에 보이고, 두 번 누르면 그 브랜치로 Switch 한다.
// 계약: 이력은 읽기뿐이다(git log) — 원격에는 닿지 않고, 「Push 됨」은 로컬이 아는 원격 추적 ref 로만 가른다.
// 계약: Switch 는 화면(MainWindow.Repos.cs)이 먼저 묻고 부른다 — 강제 옵션이 없어 겹치는 변경이 있으면 git 이 거부한다.

/// <summary>브랜치 이력의 한 줄 — Pushed 가 false 면 아직 원격에 없는 커밋이다.</summary>
public sealed record BranchCommitRow(CommitInfo Commit, bool Pushed)
{
    public string ShortSha => Commit.Sha.Length >= 7 ? Commit.Sha[..7] : Commit.Sha;

    public string WhenText => Commit.When is { } when
        ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : Commit.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed partial class MainViewModel
{
    private const int BranchLogMax = 200;

    private int _branchLogGeneration;

    public ObservableCollection<BranchCommitRow> BranchCommits { get; } = [];

    [ObservableProperty]
    private BranchInfo? _selectedBranch;

    [ObservableProperty]
    private string _branchLogCaption = string.Empty;

    partial void OnSelectedBranchChanged(BranchInfo? value) => _ = LoadBranchLogAsync(SelectedRow, value);

    // ── 이력의 커밋 → 그 커밋이 바꾼 파일 → 「변경 내용」 ──────────

    private int _commitFilesGeneration;

    public ObservableCollection<CommitFile> CommitFiles { get; } = [];

    [ObservableProperty]
    private BranchCommitRow? _selectedBranchCommit;

    [ObservableProperty]
    private CommitFile? _selectedCommitFile;

    [ObservableProperty]
    private string _commitFilesCaption = string.Empty;

    partial void OnSelectedBranchCommitChanged(BranchCommitRow? value) => _ = LoadCommitFilesAsync(SelectedRow, value);

    partial void OnSelectedCommitFileChanged(CommitFile? value)
    {
        if (value is null)
        {
            // 함정: 변경 파일을 고르면 이쪽 선택이 풀린다 — 그때 지우면 방금 띄운 변경 내용이 사라진다
            if (SelectedChange is null)
            {
                ClearDiff();
            }

            return;
        }

        SelectedChange = null;
        RightTabIndex = 0;
        IsRightPanelOpen = true;
        IsDiffShown = true;
        _ = LoadCommitDiffAsync(SelectedRow, value);
    }

    private async Task LoadCommitFilesAsync(RepoRow? row, BranchCommitRow? commit)
    {
        var generation = ++_commitFilesGeneration;
        CommitFiles.Clear();
        CommitFilesCaption = string.Empty;
        if (row is null || commit is null)
        {
            return;
        }

        try
        {
            var files = await _git.ReadCommitFilesAsync(row.Entry, commit.Commit.Sha, CancellationToken.None).ConfigureAwait(true);
            if (generation != _commitFilesGeneration)
            {
                return;
            }

            foreach (var file in files)
            {
                CommitFiles.Add(file);
            }

            CommitFilesCaption = files.Count == 0
                ? Loc.T("{0} · 바뀐 파일이 없습니다.", commit.ShortSha)
                : Loc.T("{0} · 바뀐 파일 {1}개 — 누르면 오른쪽 「변경 내용」에 보입니다", commit.ShortSha, files.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "커밋 파일 읽기 실패 {Repo} {Sha}", row.Name, commit.ShortSha);
        }
    }

    private async Task LoadCommitDiffAsync(RepoRow? row, CommitFile file)
    {
        var generation = ++_diffGeneration;
        DiffLines.Clear();
        DiffTitle = $"{file.Path} @ {(file.Sha.Length >= 7 ? file.Sha[..7] : file.Sha)}";
        DiffSummary = string.Empty;
        DiffNote = null;
        if (row is null)
        {
            return;
        }

        IsDiffLoading = true;
        try
        {
            var diff = await _git.ReadCommitDiffAsync(row.Entry, file, CancellationToken.None).ConfigureAwait(true);
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
            Log.Warning(ex, "커밋 변경 내용 읽기 실패 {Path}", file.Path);
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

    private async Task LoadBranchLogAsync(RepoRow? row, BranchInfo? branch)
    {
        // 함정: 브랜치를 빠르게 바꾸면 늦게 온 이력이 덮는다 — 세대 번호로 늦은 쪽을 버린다
        var generation = ++_branchLogGeneration;
        BranchCommits.Clear();
        BranchLogCaption = string.Empty;
        if (row is null || branch is null)
        {
            return;
        }

        try
        {
            var commits = await _git.ReadBranchLogAsync(row.Entry, branch.Name, BranchLogMax, CancellationToken.None).ConfigureAwait(true);
            var unpushed = branch.IsRemote
                ? new HashSet<string>(StringComparer.Ordinal)
                : await _git.ReadUnpushedAsync(row.Entry, branch.Name, BranchLogMax, CancellationToken.None).ConfigureAwait(true);
            if (generation != _branchLogGeneration)
            {
                return;
            }

            foreach (var commit in commits)
            {
                BranchCommits.Add(new BranchCommitRow(commit, !unpushed.Contains(commit.Sha)));
            }

            var waiting = BranchCommits.Count(c => !c.Pushed);
            BranchLogCaption = commits.Count == 0
                ? Loc.T("커밋이 없습니다.")
                : waiting == 0
                    ? Loc.T("{0} · 최근 {1}건 · 모두 Push 됨", branch.Name, commits.Count)
                    : Loc.T("{0} · 최근 {1}건 · Push 안 한 커밋 {2}건", branch.Name, commits.Count, waiting);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "브랜치 이력 읽기 실패 {Repo} {Branch}", row.Name, branch.Name);
        }
    }

    /// 계약: 지금 고른 저장소를 그 브랜치로 Switch 하고 상태·상세를 다시 읽는다 — 결과는 작업 로그에 남긴다
    public async Task SwitchToBranchAsync(BranchInfo branch)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var (ok, message) = await _git.SwitchBranchAsync(row.Entry, branch, CancellationToken.None).ConfigureAwait(true);
        AddActivity(row.Name, ok ? StepOutcome.Success : StepOutcome.Failed, $"Switch — {message}");

        row.Status = await _git.ReadStatusAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
        ApplyFilter();
        UpdateStatusLine();
        await LoadDetailAsync(row).ConfigureAwait(true);
        DetailTabIndex = 1;
    }
}
