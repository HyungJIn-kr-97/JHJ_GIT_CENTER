using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Jhj.Core.Processes;

namespace GitCenter.App.Services;

// GitHub 계정 — gh CLI 의 로그인을 읽고(hosts.yml → gh api user), 로그인·로그아웃 콘솔을 띄우고, 저장소를 만들어 올린다.
// 계약: 토큰은 이 앱이 쥐지 않는다 — gh 가 보관하고 앱은 계정 이름만 읽는다(JHJ 규약: 로그인은 gh auth login --web 으로 사람이 한다).
// 계약: 원격에 닿는 것은 단추를 눌렀을 때뿐이다 — 시작 때는 Core AppDeploy.GhAccount 처럼 hosts.yml 파일만 읽는다.
// 함정: gh auth login 은 콘솔에 기기 코드를 보여 주고 Enter 를 기다린다 — 창 없이 돌리면 영영 끝나지 않아 보이는 콘솔로 띄운다.
public sealed partial class GitHubAccount : ObservableObject
{
    private const string Host = "github.com";

    [ObservableProperty]
    private string _login = string.Empty;

    [ObservableProperty]
    private bool _isGhInstalled = true;

    [ObservableProperty]
    private bool _isBusy;

    /// 계약: 마지막 실패·안내 한 줄 — 비면 할 말이 없다
    [ObservableProperty]
    private string _note = string.Empty;

    public bool IsLoggedIn => Login.Length > 0;

    /// 제목 띠 칩의 글 — 로그인 전에는 할 일을, 뒤에는 계정을 적는다
    public string ChipText => !IsGhInstalled ? Loc.T("gh 없음") : IsLoggedIn ? Login : Loc.T("GitHub 로그인");

    public string ChipToolTip => !IsGhInstalled
        ? Loc.T("gh CLI 가 설치돼 있지 않습니다 — winget install GitHub.cli 로 설치한 뒤 다시 눌러 주십시오.")
        : IsLoggedIn
            ? Loc.T("GitHub 계정 {0} 으로 로그인돼 있습니다. 누르면 로그아웃합니다.", Login)
            : Loc.T("gh CLI 로 GitHub 에 로그인합니다 — 콘솔이 열리고 브라우저에서 승인하면 됩니다.");

    public string StatusText => !IsGhInstalled
        ? Loc.T("gh CLI 가 설치돼 있지 않습니다.")
        : IsLoggedIn ? Loc.T("로그인됨 — {0}", Login) : Loc.T("로그인돼 있지 않습니다.");

    public string ActionText => IsLoggedIn ? Loc.T("로그아웃") : Loc.T("로그인");

    partial void OnLoginChanged(string value) => NotifyTexts();

    partial void OnIsGhInstalledChanged(bool value) => NotifyTexts();

    private void NotifyTexts()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(ChipToolTip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
    }

    /// 왜: 시작 때 gh 를 부르지 않으려고 파일만 읽는다 — 정확한 값은 누를 때 gh 로 확인한다(RefreshAsync)
    public void ReadCached() => Login = Jhj.Core.App.AppDeploy.GhAccount;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var (output, error) = await RunGhAsync(["api", "user", "--jq", ".login"], null, 30, cancellationToken).ConfigureAwait(false);
        Login = output?.Trim() ?? string.Empty;
        Note = output is null ? error : string.Empty;
    }

    /// <summary>gh 의 로그인 콘솔을 띄우고 닫힐 때까지 기다린 뒤 계정을 다시 읽는다.</summary>
    public async Task<bool> LoginAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var info = new ProcessStartInfo("gh") { UseShellExecute = true };
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

            Login = string.Empty;
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
            var result = await ProcessRunner.RunAsync("gh", args, workingDirectory, timeoutSeconds, env, cancellationToken).ConfigureAwait(false);
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
        Login = string.Empty;
        Note = Loc.T("gh CLI 가 설치돼 있지 않습니다 — winget install GitHub.cli 로 설치한 뒤 다시 눌러 주십시오.");
    }
}
