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
    private const string RepoTag = "repo:";
    private const string AgeTag = "age:";

    // 계약: 「상태 새로 고침」이 커밋 날을 읽는 기간(log --since=735.days.ago)과 같은 값이다
    private const int HistoryDays = 735;

    public ObservableCollection<CommitRow> HistoryCommits { get; } = [];

    [ObservableProperty]
    private string _historyTitle = "커밋 내역";

    [ObservableProperty]
    private string _historyCaption = Loc.T("대시보드의 커밋 캘린더(칸·막대)나 저장소 줄을 누르면 그 커밋이 여기에 나옵니다.");

    // 함정: 칸을 잇달아 누르면 읽기가 겹친다 — 세대 번호로 늦게 온 쪽을 버린다
    private int _historyGeneration;

    /// <summary>캘린더의 한 줄(날 · 주 · 달)을 받아 그 기간의 커밋을 읽고 「커밋 내역」으로 건너간다.</summary>
    [RelayCommand]
    private async Task OpenCommitsAsync(ChartRow? row)
    {
        var tag = row?.Tag ?? string.Empty;
        var today = DateOnly.FromDateTime(DateTime.Now);

        // 계약: 저장소·시점 줄은 캘린더가 읽어 둔 가장 긴 기간(HistoryDays)을 연다 — 그보다 옛 커밋은 나오지 않는다
        if (tag.StartsWith(RepoTag, StringComparison.Ordinal))
        {
            var repo = _all.FirstOrDefault(r => r.Id == tag[RepoTag.Length..]);
            await OpenRepoHistoryAsync(repo).ConfigureAwait(true);
            return;
        }

        if (tag.StartsWith(AgeTag, StringComparison.Ordinal))
        {
            var bounds = tag[AgeTag.Length..].Split("..");
            if (bounds.Length == 2 && double.TryParse(bounds[0], CultureInfo.InvariantCulture, out var min)
                && double.TryParse(bounds[1], CultureInfo.InvariantCulture, out var max))
            {
                var now = DateTimeOffset.Now;
                var repos = _all.Where(r => r.LastCommitWhen is { } when
                    && (now - when).TotalDays >= min && (now - when).TotalDays < max).ToList();
                await LoadHistoryAsync($"마지막 커밋 {row!.Label}", repos, today.AddDays(-HistoryDays), today).ConfigureAwait(true);
            }

            return;
        }

        var parts = tag.Split("..");
        if (parts.Length != 2
            || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out var from)
            || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var to))
        {
            return;
        }

        var span = from == to ? $"{from:yyyy-MM-dd (ddd)}" : $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";
        await LoadHistoryAsync(span, _all.Where(r => r.CommitDays.Any(d => d >= from && d <= to)).ToList(), from, to).ConfigureAwait(true);
    }

    /// <summary>저장소 하나의 커밋 내역을 연다 — 대시보드 「최근 커밋」 줄과 저장소 카드의 줄이 부른다.</summary>
    [RelayCommand]
    private async Task OpenRepoHistoryAsync(RepoRow? repo)
    {
        if (repo is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        await LoadHistoryAsync(repo.Name, [repo], today.AddDays(-HistoryDays), today).ConfigureAwait(true);
    }

    private async Task LoadHistoryAsync(string span, IReadOnlyList<RepoRow> repos, DateOnly from, DateOnly to)
    {
        var generation = ++_historyGeneration;
        HistoryTitle = Loc.T("커밋 내역 — {0}", span);
        HistoryCaption = Loc.T("읽는 중입니다…");
        HistoryCommits.Clear();
        PageIndex = 2;

        var found = new List<CommitRow>();
        foreach (var repo in repos)
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

        HistoryCaption = Loc.T("커밋 {0:N0}건 · 저장소 {1}개 · 지금 브랜치 기준", found.Count, found.Select(c => c.Repo).Distinct().Count());
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
