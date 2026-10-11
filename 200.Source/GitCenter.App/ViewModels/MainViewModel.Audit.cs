using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 「점검」 화면(PageIndex 3) — 전체 저장소의 git 설정을 읽어 어긋난 것을 한 표로 보여 준다.
// 계약: 읽기뿐이다 — 저장소마다 git config 한 번과 파일 확인만 하고, 아무것도 고치지 않으며 원격에 닿지 않는다.
// 계약: 「다름」은 다수와 견준 것이다 — 작성자 메일·autocrlf 는 가장 많이 쓰인 값을 기준으로 삼는다(틀렸다는 뜻이 아니다).

/// <summary>점검 표의 한 줄.</summary>
public sealed record AuditRow(RepoRow Repo, RepoAudit Audit, IReadOnlyList<string> Findings)
{
    public string RepoName => Repo.Name;

    public string Group => Repo.Group.Length == 0 ? "그룹 없음" : Repo.Group;

    public string BranchText => Repo.BranchText;

    public string Author => Audit.UserEmail.Length == 0 ? Audit.UserName : $"{Audit.UserName} <{Audit.UserEmail}>";

    public string RemoteText => Audit.RemoteUrl ?? "—";

    public string AutoCrlfText => Audit.AutoCrlf.Length == 0 ? "—" : Audit.AutoCrlf;

    public string IgnoreText => $"{(Audit.HasGitignore ? "있음" : "없음")} · {Audit.ExcludeLines}";

    public string FetchText => Audit.LastFetch?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    public bool HasFindings => Findings.Count > 0;

    public string FindingText => HasFindings ? string.Join(" · ", Findings) : "이상 없음";
}

/// <summary>작성자 일괄 적용의 미리 보기 한 줄 — 실제로 바뀔 저장소만 담는다.</summary>
public sealed record AuthorChange(RepoRow Repo, string Before, string After)
{
    public string RepoName => Repo.Name;
}

public sealed partial class MainViewModel
{
    public ObservableCollection<AuditRow> AuditRows { get; } = [];

    [ObservableProperty]
    private string _auditCaption = "「점검 실행」을 누르면 전체 저장소의 작성자 · 원격 · 추적 브랜치 · 무시 목록 · 줄바꿈 설정을 읽습니다. 아무것도 고치지 않습니다.";

    [ObservableProperty]
    private bool _isAuditing;

    [RelayCommand]
    private async Task RunAuditAsync()
    {
        if (IsAuditing)
        {
            return;
        }

        IsAuditing = true;
        AuditCaption = "읽는 중입니다…";
        AuditRows.Clear();

        try
        {
            var read = new List<(RepoRow Repo, RepoAudit Audit)>();
            foreach (var repo in _all.Where(r => r.Entry.Health == RepoHealth.Ok).ToList())
            {
                read.Add((repo, await _git.ReadAuditAsync(repo.Entry, CancellationToken.None).ConfigureAwait(true)));
            }

            var email = Majority(read.Select(r => r.Audit.UserEmail));
            var autoCrlf = Majority(read.Select(r => r.Audit.AutoCrlf));

            // 왜: 대개 다수 값으로 맞추려는 것이다 — 비어 있을 때만 채워 적어 둔 값을 덮지 않는다
            if (AuthorName.Length == 0 && AuthorEmail.Length == 0)
            {
                AuthorName = Majority(read.Select(r => r.Audit.UserName));
                AuthorEmail = email;
            }

            var rows = read.Select(r => new AuditRow(r.Repo, r.Audit, Judge(r.Repo, r.Audit, email, autoCrlf))).ToList();
            foreach (var row in rows.OrderByDescending(r => r.Findings.Count).ThenBy(r => r.RepoName, StringComparer.CurrentCultureIgnoreCase))
            {
                AuditRows.Add(row);
            }

            AuditCaption = $"저장소 {rows.Count}개 · 확인할 것이 있는 저장소 {rows.Count(r => r.HasFindings)}개 · 기준 작성자 {email} · 기준 autocrlf {(autoCrlf.Length == 0 ? "(없음)" : autoCrlf)}";
        }
        finally
        {
            IsAuditing = false;
        }
    }

    [RelayCommand]
    private void GoToAuditRepo(AuditRow? row) => GoToRepo(row?.Repo);

    // ── 작성자 일괄 적용 ─────────────────────────────────────
    // 계약: 「미리 보기」가 고른 저장소 중 실제로 바뀔 것만 추리고, 「적용」은 그 목록에만 쓴다 — 미리 보지 않은 것은 쓰지 않는다.
    // 계약: 쓰는 곳은 저장소 로컬 설정뿐이다 — autocrlf 등 다른 키는 여기서 쓰지 않는다(줄바꿈이 통째로 바뀔 수 있다).

    public ObservableCollection<AuthorChange> AuthorPreview { get; } = [];

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private string _authorEmail = string.Empty;

    [ObservableProperty]
    private string _authorCaption = "저장소 관리에서 고른 저장소에만 적용합니다. 먼저 「미리 보기」로 바뀔 것을 확인하십시오.";

    // 왜: 미리 본 뒤 값을 고치면 목록이 옛 값 기준이다 — 고치는 순간 비워 다시 보게 한다
    partial void OnAuthorNameChanged(string value) => AuthorPreview.Clear();

    partial void OnAuthorEmailChanged(string value) => AuthorPreview.Clear();

    [RelayCommand]
    private async Task PreviewAuthorAsync()
    {
        AuthorPreview.Clear();
        var name = AuthorName.Trim();
        var email = AuthorEmail.Trim();

        if (name.Length == 0 || !email.Contains('@'))
        {
            AuthorCaption = "이름과 메일을 모두 적어 주십시오.";
            return;
        }

        var targets = SelectedShown().Where(r => r.Entry.Health == RepoHealth.Ok).ToList();
        if (targets.Count == 0)
        {
            AuthorCaption = "고른 저장소가 없습니다 — 저장소 관리에서 먼저 고르십시오.";
            return;
        }

        foreach (var repo in targets)
        {
            var audit = await _git.ReadAuditAsync(repo.Entry, CancellationToken.None).ConfigureAwait(true);
            if (audit.UserName != name || !string.Equals(audit.UserEmail, email, StringComparison.OrdinalIgnoreCase))
            {
                var before = audit.UserName.Length + audit.UserEmail.Length == 0 ? "(없음)" : $"{audit.UserName} <{audit.UserEmail}>";
                AuthorPreview.Add(new AuthorChange(repo, before, $"{name} <{email}>"));
            }
        }

        AuthorCaption = AuthorPreview.Count == 0
            ? $"고른 저장소 {targets.Count}개가 이미 같은 작성자입니다 — 쓸 것이 없습니다."
            : $"고른 저장소 {targets.Count}개 중 {AuthorPreview.Count}개가 바뀝니다. 맞으면 「적용」을 누르십시오.";
    }

    [RelayCommand]
    private async Task ApplyAuthorAsync()
    {
        var changes = AuthorPreview.ToList();
        if (changes.Count == 0)
        {
            AuthorCaption = "먼저 「미리 보기」를 눌러 바뀔 것을 확인하십시오.";
            return;
        }

        var name = AuthorName.Trim();
        var email = AuthorEmail.Trim();
        var failed = 0;
        foreach (var change in changes)
        {
            var error = await _git.WriteAuthorAsync(change.Repo.Entry, name, email, CancellationToken.None).ConfigureAwait(true);
            failed += error is null ? 0 : 1;

            // 계약: 바꾸기 전 값을 작업 로그에 남긴다 — 되돌릴 때 볼 유일한 기록이다
            AddActivity(change.RepoName, error is null ? StepOutcome.Success : StepOutcome.Failed,
                error ?? $"작성자 {change.Before} → {change.After}");
        }

        AuthorPreview.Clear();
        AuthorCaption = $"적용 {changes.Count - failed}개 · 실패 {failed}개 — 바꾸기 전 값은 작업 로그에 있습니다.";
        await RunAuditAsync().ConfigureAwait(true);
    }

    private static string Majority(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;

    private static List<string> Judge(RepoRow repo, RepoAudit audit, string email, string autoCrlf)
    {
        var findings = new List<string>();

        if (audit.UserName.Length == 0 || audit.UserEmail.Length == 0)
        {
            findings.Add("작성자 없음");
        }
        else if (!string.Equals(audit.UserEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("작성자가 다름");
        }

        if (audit.RemoteUrl is null)
        {
            findings.Add("원격 없음");
        }
        else if (repo.Status is { Upstream: null })
        {
            findings.Add("추적 브랜치 없음");
        }

        if (!audit.HasGitignore)
        {
            findings.Add(".gitignore 없음");
        }

        if (!string.Equals(audit.AutoCrlf, autoCrlf, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("autocrlf 가 다름");
        }

        if (repo.Status is { StashCount: > 0 } status)
        {
            findings.Add($"stash {status.StashCount}");
        }

        if (repo.Status is { Operation: not GitOperationState.None })
        {
            findings.Add("진행 중인 병합·리베이스");
        }

        return findings;
    }
}
