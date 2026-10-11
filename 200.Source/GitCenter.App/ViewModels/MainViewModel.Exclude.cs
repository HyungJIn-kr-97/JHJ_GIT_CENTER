using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 공통 무시 목록 — 적어 둔 줄을 고른 저장소의 .git/info/exclude 나 .gitignore 에 덧붙인다(「점검」 화면).
// 계약: 기본은 exclude(커밋되지 않는 로컬 파일)다 — .gitignore 는 「.gitignore 에 쓰기」를 켰을 때만 고치고 스테이지·커밋은 하지 않는다.
// 함정: 어느 쪽이든 이미 추적 중인 파일에는 효과가 없다.
// 계약: 없는 줄만 덧붙인다 — 「미리 보기」가 저장소마다 더해질 줄을 추리고 「적용」은 그 목록에만 쓴다.

/// <summary>무시 목록 미리 보기의 한 줄 — 그 저장소에 새로 더해질 줄만 담는다.</summary>
public sealed record ExcludeChange(RepoRow Repo, IReadOnlyList<string> Missing, bool ToGitignore = false)
{
    public string RepoName => Repo.Name;

    public string MissingText => string.Join("   ", Missing);
}

public sealed partial class MainViewModel
{
    // 왜: 이 줄들은 추적 전 파일을 통째로 가려 「새 파일이 안 보인다」로 이어진다 — 한꺼번에 여러 저장소에 넣게 두지 않는다
    private static readonly HashSet<string> BlanketRules = new(StringComparer.Ordinal) { "*", "**", "/", "/*", "*.*", "**/*", "/**" };

    public ObservableCollection<ExcludeChange> ExcludePreview { get; } = [];

    /// 계약: 한 줄에 규칙 하나다 — 빈 줄과 # 으로 시작하는 줄은 버린다
    [ObservableProperty]
    private string _excludeText = string.Empty;

    [ObservableProperty]
    private string _excludeCaption = Loc.T("한 줄에 하나씩 적습니다. 고른 저장소의 .git/info/exclude 에 없는 줄만 덧붙입니다 — 커밋되지 않고, 이미 추적 중인 파일에는 효과가 없습니다.");

    /// 계약: 켜면 .git/info/exclude 대신 작업 트리 맨 위 .gitignore 에 쓴다 — 저장소마다 미커밋 변경이 하나 생긴다
    [ObservableProperty]
    private bool _excludeToGitignore;

    private string ExcludeTarget => ExcludeToGitignore ? ".gitignore" : "exclude";

    // 왜: 미리 본 뒤 쓰는 곳을 바꾸면 목록이 다른 파일 기준이다 — 바꾸는 순간 비워 다시 보게 한다
    partial void OnExcludeToGitignoreChanged(bool value)
    {
        ExcludePreview.Clear();
        ExcludeCaption = Loc.T(value
            ? "고른 저장소의 .gitignore 에 없는 줄만 덧붙입니다(없으면 만듭니다) — 저장소마다 미커밋 변경이 하나 생기고, 커밋은 직접 하셔야 합니다."
            : "고른 저장소의 .git/info/exclude 에 없는 줄만 덧붙입니다 — 커밋되지 않고, 이미 추적 중인 파일에는 효과가 없습니다.");
    }

    // 왜: 미리 본 뒤 줄을 고치면 목록이 옛 줄 기준이다 — 고치는 순간 비워 다시 보게 한다
    partial void OnExcludeTextChanged(string value) => ExcludePreview.Clear();

    private List<string> ExcludeRules() =>
        [.. ExcludeText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct(StringComparer.Ordinal)];

    [RelayCommand]
    private async Task PreviewExcludeAsync()
    {
        ExcludePreview.Clear();
        var rules = ExcludeRules();

        if (rules.Count == 0)
        {
            ExcludeCaption = Loc.T("덧붙일 줄을 적어 주십시오.");
            return;
        }

        if (rules.FirstOrDefault(BlanketRules.Contains) is { } blanket)
        {
            ExcludeCaption = Loc.T("「{0}」 는 새 파일을 통째로 가립니다 — 일괄로 넣지 않습니다.", blanket);
            return;
        }

        var toGitignore = ExcludeToGitignore;
        var targets = SelectedShown().Where(r => r.Entry.Health == RepoHealth.Ok).ToList();
        if (targets.Count == 0)
        {
            ExcludeCaption = Loc.T("고른 저장소가 없습니다 — 저장소 관리에서 먼저 고르십시오.");
            return;
        }

        foreach (var repo in targets)
        {
            var read = toGitignore
                ? _git.ReadGitignoreAsync(repo.Entry, CancellationToken.None)
                : _git.ReadExcludeAsync(repo.Entry, CancellationToken.None);
            var have = (await read.ConfigureAwait(true)).ToHashSet(StringComparer.Ordinal);
            var missing = rules.Where(r => !have.Contains(r)).ToList();
            if (missing.Count > 0)
            {
                ExcludePreview.Add(new ExcludeChange(repo, missing, toGitignore));
            }
        }

        // 계약: 적어 둔 줄은 미리 볼 때 남긴다 — 다음에 열어도 같은 목록에서 시작한다
        Settings.CommonExcludes = rules;
        await SaveSettingsAsync().ConfigureAwait(true);

        ExcludeCaption = ExcludePreview.Count == 0
            ? Loc.T("고른 저장소 {0}개에 이미 다 들어 있습니다 — 쓸 것이 없습니다.", targets.Count)
            : Loc.T("고른 저장소 {0}개 중 {1}개의 {2} 에 줄이 더해집니다. 맞으면 「적용」을 누르십시오.", targets.Count, ExcludePreview.Count, ExcludeTarget);
    }

    [RelayCommand]
    private async Task ApplyExcludeAsync()
    {
        var changes = ExcludePreview.ToList();
        if (changes.Count == 0)
        {
            ExcludeCaption = Loc.T("먼저 「미리 보기」를 눌러 더해질 줄을 확인하십시오.");
            return;
        }

        var failed = 0;
        foreach (var change in changes)
        {
            // 계약: 쓰는 곳은 미리 볼 때 정해진 값이다 — 미리 본 파일과 다른 파일에 쓰지 않는다
            var write = change.ToGitignore
                ? _git.AppendGitignoreAsync(change.Repo.Entry, change.Missing, CancellationToken.None)
                : _git.AppendExcludeAsync(change.Repo.Entry, change.Missing, CancellationToken.None);
            var error = await write.ConfigureAwait(true);
            failed += error is null ? 0 : 1;

            // 계약: 무엇을 더했는지 작업 로그에 남긴다 — 되돌리려면 그 줄을 그 파일에서 지우면 된다
            AddActivity(change.RepoName, error is null ? StepOutcome.Success : StepOutcome.Failed,
                error ?? $"{(change.ToGitignore ? ".gitignore" : "exclude")} 에 더함: {string.Join(", ", change.Missing)}");
        }

        ExcludePreview.Clear();
        ExcludeCaption = Loc.T("적용 {0}개 · 실패 {1}개 — 더한 줄은 작업 로그에 있습니다. 「상태 새로 고침」을 하면 목록에 비칩니다.", changes.Count - failed, failed);
    }
}
