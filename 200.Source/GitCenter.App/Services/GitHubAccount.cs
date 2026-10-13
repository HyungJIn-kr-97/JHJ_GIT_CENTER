using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Infrastructure.Accounts;
using GitCenter.Infrastructure.Tools;
using Jhj.Core.Processes;

namespace GitCenter.App.Services;

// GitHub 계정 — 시작 때 세 출처(gh CLI → GitHub Desktop → git 자격 증명)를 로컬에서 읽어 제목 띠 칩에 띄우고, 로그인·로그아웃 콘솔을 띄우고, 저장소를 만들어 올린다.
// 계약: 토큰은 이 앱이 쥐지 않는다 — gh 가 보관하고 앱은 계정 이름만 읽는다(JHJ 규약 §4: 로그인은 gh auth login --web 으로 사람이 한다).
// 계약: 원격에 닿는 것은 단추를 눌렀을 때뿐이다 — 시작(ResolveLocalAsync)은 hosts.yml · 자격 증명 관리자 · git config 만 읽는다(Infrastructure/Accounts/GitHubAccountSources).
// 계약: IsLoggedIn 은 gh CLI 로그인이다 — 올리기·공유(gh api)가 이것을 본다. 다른 출처에서만 읽힌 계정은 칩에 보이되(HasAccount) 그 일은 못 한다.
// 계약: gh 가 없으면 칩을 누를 때 winget 콘솔로 먼저 깐다(InstallGhAsync) — 설치 화면 「필수 도구」를 끄고 깐 PC 를 위해서다.
// 함정: gh auth login 은 콘솔에 기기 코드를 보여 주고 Enter 를 기다린다 — 창 없이 돌리면 영영 끝나지 않아 보이는 콘솔로 띄운다.
public sealed partial class GitHubAccount : ObservableObject
{
    private const string Host = "github.com";
    private const string GhWingetId = "GitHub.cli";

    /// 계약: 칩에 보이는 계정 — 출처 차례의 첫 것. gh 로그인이면 Source 가 GhCli 다
    [ObservableProperty]
    private string _login = string.Empty;

    [ObservableProperty]
    private AccountSourceKind? _source;

    [ObservableProperty]
    private IReadOnlyList<AccountHit> _hits = [];

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private string _authorEmail = string.Empty;

    [ObservableProperty]
    private bool _isGhInstalled = true;

    [ObservableProperty]
    private bool _isBusy;

    /// 계약: 마지막 실패·안내 한 줄 — 비면 할 말이 없다
    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>gh CLI 로 로그인돼 있나 — gh api 를 쓰는 일(올리기 · 공유)의 조건이다.</summary>
    public bool IsLoggedIn => Login.Length > 0 && Source == AccountSourceKind.GhCli;

    /// <summary>어느 출처에서든 계정을 찾았나 — 칩 표시의 조건이다.</summary>
    public bool HasAccount => Login.Length > 0;

    public string SourceText => SourceName(Source);

    /// 제목 띠 칩의 글 — 계정이 있으면 계정을, 없으면 할 일을 적는다
    public string ChipText => HasAccount ? Login : !IsGhInstalled ? Loc.T("gh 설치") : Loc.T("GitHub 로그인");

    public string ChipToolTip
    {
        get
        {
            var lines = new List<string>();
            if (!HasAccount)
            {
                lines.Add(!IsGhInstalled
                    ? Loc.T("gh CLI 가 설치돼 있지 않습니다 — 누르면 winget 으로 설치한 뒤 로그인 콘솔을 엽니다.")
                    : Loc.T("gh CLI 로 GitHub 에 로그인합니다 — 콘솔이 열리고 브라우저에서 승인하면 됩니다."));
            }
            else if (IsLoggedIn)
            {
                lines.Add(Loc.T("GitHub 계정 {0} ({1}) — 누르면 로그아웃합니다.", Login, SourceText));
            }
            else
            {
                lines.Add(Loc.T("GitHub 계정 {0} ({1}) — gh CLI 에는 로그인돼 있지 않습니다. 누르면 gh 로그인 콘솔을 엽니다.", Login, SourceText));
            }

            var others = OtherHits();
            if (others.Length > 0)
            {
                lines.Add(Loc.T("다른 출처: {0}", others));
            }

            lines.Add(AuthorText);
            return string.Join('\n', lines);
        }
    }

    /// 설정 「일반」 · 올리기 층 · 공유 탭의 상태 한 줄
    public string StatusText => IsLoggedIn
        ? Loc.T("로그인됨 — {0} ({1})", Login, SourceText)
        : HasAccount
            ? Loc.T("{0} ({1}) — gh CLI 로그인은 없습니다.", Login, SourceText)
            : !IsGhInstalled ? Loc.T("gh CLI 가 설치돼 있지 않습니다.") : Loc.T("로그인돼 있지 않습니다.");

    /// 커밋 작성자 한 줄 — git 전역 설정(user.name · user.email)
    public string AuthorText => AuthorName.Length + AuthorEmail.Length == 0
        ? Loc.T("커밋 작성자 — git 전역 설정(user.name · user.email)이 없습니다")
        : Loc.T("커밋 작성자 — {0}", AuthorEmail.Length == 0 ? AuthorName : AuthorName.Length == 0 ? AuthorEmail : $"{AuthorName} <{AuthorEmail}>");

    public string ActionText => IsLoggedIn ? Loc.T("로그아웃") : !IsGhInstalled ? Loc.T("gh 설치 + 로그인") : Loc.T("로그인");

    partial void OnLoginChanged(string value) => NotifyTexts();

    partial void OnSourceChanged(AccountSourceKind? value) => NotifyTexts();

    partial void OnHitsChanged(IReadOnlyList<AccountHit> value) => NotifyTexts();

    partial void OnAuthorNameChanged(string value) => NotifyTexts();

    partial void OnAuthorEmailChanged(string value) => NotifyTexts();

    partial void OnIsGhInstalledChanged(bool value) => NotifyTexts();

    private void NotifyTexts()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(ChipToolTip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AuthorText));
        OnPropertyChanged(nameof(ActionText));
    }

    public static string SourceName(AccountSourceKind? source) => source switch
    {
        AccountSourceKind.GhCli => Loc.T("gh CLI"),
        AccountSourceKind.GitHubDesktop => Loc.T("GitHub Desktop"),
        AccountSourceKind.GitCredential => Loc.T("git 자격 증명"),
        _ => string.Empty
    };

    /// <summary>시작 때 한 번 — 세 출처와 git 작성자를 로컬에서 읽어 칩을 채운다. 원격에는 닿지 않는다.</summary>
    /// 왜: 앱을 켜면 바로 계정이 보여야 한다(2026-10-14 사용자 지시) — 전에는 hosts.yml 만 읽어 gh 가 없으면 「gh 없음」으로 끝났다
    public async Task ResolveLocalAsync(CancellationToken cancellationToken)
    {
        IsGhInstalled = ToolLocator.IsGhInstalled;
        var hits = await Task.Run(GitHubAccountSources.ReadLocal, cancellationToken).ConfigureAwait(true);
        Apply(hits);

        var (name, email) = await GitHubAccountSources.ReadGitAuthorAsync(cancellationToken).ConfigureAwait(true);
        AuthorName = name;
        AuthorEmail = email;
    }

    private void Apply(IReadOnlyList<AccountHit> hits)
    {
        Hits = hits;
        var first = hits.OrderBy(h => h.Source).FirstOrDefault();
        Login = first?.Login ?? string.Empty;
        Source = first?.Source;
    }

    private string OtherHits() => string.Join(" · ",
        Hits.Where(h => h.Source != Source || !h.Login.Equals(Login, StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Login} ({SourceName(h.Source)})"));

    /// <summary>gh 로 로그인 계정을 확인한다 — 성공이면 Source 가 GhCli 가 되고, 실패면 다른 출처 값을 그대로 둔다.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var (output, error) = await RunGhAsync(["api", "user", "--jq", ".login"], null, 30, cancellationToken).ConfigureAwait(false);
        var login = output?.Trim() ?? string.Empty;
        if (login.Length > 0)
        {
            var others = Hits.Where(h => h.Source != AccountSourceKind.GhCli).ToList();
            others.Insert(0, new AccountHit(AccountSourceKind.GhCli, login));
            Apply(others);
            Note = string.Empty;
            return;
        }

        Apply(Hits.Where(h => h.Source != AccountSourceKind.GhCli).ToList());
        Note = output is null ? error : string.Empty;
    }

    /// <summary>gh 의 로그인 콘솔을 띄우고 닫힐 때까지 기다린 뒤 계정을 다시 읽는다 — gh 가 없으면 winget 콘솔로 먼저 깐다.</summary>
    public async Task<bool> LoginAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            if (!ToolLocator.IsGhInstalled && !await InstallGhAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var info = new ProcessStartInfo(ToolLocator.Gh) { UseShellExecute = true };
            foreach (var arg in (string[])["auth", "login", "--hostname", Host, "--git-protocol", "https", "--web"])
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                Note = Loc.T("gh 로그인 콘솔을 열지 못했습니다.");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return IsLoggedIn;
        }
        catch (Win32Exception)
        {
            MarkMissing();
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// 계약: winget 은 보이는 콘솔로 띄운다 — UAC·진행 표시를 사용자가 봐야 하고, 끝나면 PATH 를 다시 읽어(ToolLocator.Invalidate) gh 를 찾는다
    private async Task<bool> InstallGhAsync(CancellationToken cancellationToken)
    {
        Note = Loc.T("winget 으로 gh CLI 를 설치하는 콘솔을 열었습니다 — 끝나면 로그인 콘솔이 이어서 열립니다.");
        try
        {
            var info = new ProcessStartInfo("winget.exe") { UseShellExecute = true };
            foreach (var arg in (string[])["install", "--id", GhWingetId, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"])
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Win32Exception)
        {
            // winget 자체가 없는 PC — 아래 안내로 끝낸다
        }

        ToolLocator.Invalidate();
        IsGhInstalled = ToolLocator.IsGhInstalled;
        if (!IsGhInstalled)
        {
            Note = Loc.T("gh CLI 설치가 끝나지 않았습니다 — winget install GitHub.cli 로 설치한 뒤 다시 눌러 주십시오.");
            return false;
        }

        Note = string.Empty;
        return true;
    }

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !IsLoggedIn)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var (output, error) = await RunGhAsync(["auth", "logout", "--hostname", Host, "--user", Login], null, 30, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                Note = error;
                return false;
            }

            Apply(Hits.Where(h => h.Source != AccountSourceKind.GhCli).ToList());
            Note = string.Empty;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public sealed record CreateResult(bool Ok, string Message, string? Url);

    /// <summary>GitHub 에 저장소를 만들고 그 폴더의 원격으로 묶은 뒤 지금 브랜치를 올린다 — gh repo create 한 번이다.</summary>
    /// 계약: 호출 전에 커밋이 있고 원격이 없는 것을 확인한다 — 둘 다 gh 가 알아듣기 어려운 오류로 끝낸다
    public async Task<CreateResult> CreateRepositoryAsync(
        string path, string name, bool isPrivate, string description, string remoteName, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return new CreateResult(false, Loc.T("다른 GitHub 작업이 끝나기를 기다려 주십시오."), null);
        }

        IsBusy = true;
        try
        {
            var args = new List<string>
            {
                "repo", "create", name, isPrivate ? "--private" : "--public",
                "--source", path, "--remote", remoteName, "--push"
            };
            if (description.Length > 0)
            {
                args.AddRange(["--description", description]);
            }

            var (output, error) = await RunGhAsync(args, path, 300, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                return new CreateResult(false, DescribeCreateFailure(error), null);
            }

            var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            return new CreateResult(true, string.Empty, url);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribeCreateFailure(string error)
    {
        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("같은 이름의 저장소가 이 계정에 이미 있습니다 — 다른 이름을 적어 주십시오.");
        }

        if (error.Contains("not logged into", StringComparison.OrdinalIgnoreCase)
            || error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("GitHub 로그인이 풀렸습니다 — 다시 로그인해 주십시오.");
        }

        return error;
    }

    /// 계약: 성공이면 (stdout, "") · 실패면 (null, 첫 오류 줄). gh 가 없으면 IsGhInstalled 를 끄고 설치 안내를 돌려준다
    private async Task<(string? Output, string Error)> RunGhAsync(
        IReadOnlyList<string> args, string? workingDirectory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var env = new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1" };
            var result = await ProcessRunner.RunAsync(ToolLocator.Gh, args, workingDirectory, timeoutSeconds, env, cancellationToken).ConfigureAwait(false);
            IsGhInstalled = true;
            if (result.TimedOut)
            {
                return (null, Loc.T("gh 가 {0}초 안에 끝나지 않았습니다.", timeoutSeconds));
            }

            if (result.ExitCode != 0)
            {
                var line = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => !l.StartsWith("hint:", StringComparison.Ordinal)) ?? Loc.T("gh 가 실패했습니다.");
                return (null, line);
            }

            return (result.StdOut, string.Empty);
        }
        catch (Win32Exception)
        {
            MarkMissing();
            return (null, Note);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }

    private void MarkMissing()
    {
        IsGhInstalled = false;
        Apply(Hits.Where(h => h.Source != AccountSourceKind.GhCli).ToList());
        Note = Loc.T("gh CLI 가 설치돼 있지 않습니다 — 누르면 winget 으로 설치한 뒤 로그인 콘솔을 엽니다.");
    }
}
