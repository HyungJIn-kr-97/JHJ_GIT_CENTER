using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Application.Services;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 「커밋 내역」 화면(PageIndex 2) — 대시보드 커밋 캘린더의 칸·막대를 누르면 그 날(들)의 커밋을 저장소마다 읽어 보여 준다.
// 계약: 읽기뿐이다 — git log 만 부르고 원격에는 닿지 않는다. 웹으로 여는 것은 사용자가 단추를 눌렀을 때만이다.
// 계약: 캘린더가 이미 센 날(CommitDays)에 걸리는 저장소만 다시 읽는다 — 나머지는 git 을 부르지 않는다.

/// <summary>커밋 내역의 한 줄.</summary>
public sealed record CommitRow(RepoRow Repo, CommitInfo Commit)
{
    public string RepoName => Repo.Name;

    public string Group => Repo.Group.Length == 0 ? "그룹 없음" : Repo.Group;

    public string ShortSha => Commit.Sha.Length >= 7 ? Commit.Sha[..7] : Commit.Sha;

    public string WhenText => Commit.When is { } when
        ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : Commit.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// 계약: 원격 주소를 웹 주소로 못 바꾸면 null 이다 — 그 줄의 「웹에서 보기」는 눌러도 로그만 남긴다
    public string? WebUrl => ShellOpener.ToBrowserUrl(Repo.Entry.RemoteUrl) is { } root ? $"{root.TrimEnd('/')}/commit/{Commit.Sha}" : null;
}

public sealed partial class MainViewModel
{
    public ObservableCollection<CommitRow> HistoryCommits { get; } = [];

    [ObservableProperty]
    private string _historyTitle = "커밋 내역";

    [ObservableProperty]
    private string _historyCaption = "대시보드의 커밋 캘린더에서 날(칸·막대)을 누르면 그때의 커밋이 여기에 나옵니다.";

    // 함정: 칸을 잇달아 누르면 읽기가 겹친다 — 세대 번호로 늦게 온 쪽을 버린다
    private int _historyGeneration;

    /// <summary>캘린더의 한 줄(날 · 주 · 달)을 받아 그 기간의 커밋을 읽고 「커밋 내역」으로 건너간다.</summary>
    [RelayCommand]
    private async Task OpenCommitsAsync(ChartRow? row)
    {
        var parts = row?.Tag.Split("..") ?? [];
        if (parts.Length != 2
            || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out var from)
            || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var to))
        {
            return;
        }

        var generation = ++_historyGeneration;
        var span = from == to ? $"{from:yyyy-MM-dd (ddd)}" : $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";
        HistoryTitle = $"커밋 내역 — {span}";
        HistoryCaption = "읽는 중입니다…";
        HistoryCommits.Clear();
        PageIndex = 2;

        var found = new List<CommitRow>();
        foreach (var repo in _all.Where(r => r.CommitDays.Any(d => d >= from && d <= to)).ToList())
        {
            var commits = await _git.ReadCommitsAsync(repo.Entry, from, to, CancellationToken.None).ConfigureAwait(true);
            if (generation != _historyGeneration)
            {
                return;
            }

            found.AddRange(commits.Select(c => new CommitRow(repo, c)));
        }

        foreach (var commit in found.OrderByDescending(c => c.Commit.When ?? DateTimeOffset.MinValue))
        {
            HistoryCommits.Add(commit);
        }

        HistoryCaption = $"커밋 {found.Count:N0}건 · 저장소 {found.Select(c => c.Repo).Distinct().Count()}개 · 지금 브랜치 기준";
    }

    [RelayCommand]
    private void OpenCommitWeb(CommitRow? commit)
    {
        if (commit is null)
        {
            return;
        }

        if (commit.WebUrl is not { } url || !ShellOpener.OpenUrl(url))
        {
            AddActivity(commit.RepoName, StepOutcome.Failed, "원격 주소를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void GoToCommitRepo(CommitRow? commit) => GoToRepo(commit?.Repo);
}
