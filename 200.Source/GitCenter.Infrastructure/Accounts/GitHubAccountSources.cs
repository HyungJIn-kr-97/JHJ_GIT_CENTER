using GitCenter.Infrastructure.Tools;

namespace GitCenter.Infrastructure.Accounts;

/// <summary>계정을 읽은 출처 — JHJ 규약 §4 의 차례(CLI → 전용 App → git 자격 증명)대로 값이 크다.</summary>
public enum AccountSourceKind
{
    GhCli = 0,
    GitHubDesktop = 1,
    GitCredential = 2
}

public sealed record AccountHit(AccountSourceKind Source, string Login);

/// <summary>
/// 이 PC 에 로그인된 GitHub 계정을 세 출처에서 읽는다 — 전부 로컬 파일·자격 증명 관리자다. 원격에 닿지 않는다.
/// 계약: gh CLI 는 hosts.yml(Core AppDeploy.GhAccount), GitHub Desktop 과 git 자격 증명(GCM)은 Windows 자격 증명 관리자의 항목 이름·사용자 이름이다 — 토큰은 읽지 않는다.
/// 함정: GitHub Desktop 의 항목 이름은 「GitHub - https://api.github.com/<login>」(하이픈 · 2026-10-14 실측) 이고 소스의 상수는 en dash 다 — 둘 다 거른다.
/// 함정: git 자격 증명(GCM)은 계정이 둘이면 「git:https://<login>@github.com」 꼴로 더 남는다 — 사용자 이름이 비면 항목 이름에서 꺼낸다.
/// 계약: gh 는 hosts.yml 에 user 가 없으면 자격 증명 「gh:github.com:<login>」 으로 보완한다 — 토큰을 자격 증명 관리자에 두는 설정(secure storage)이 그렇다.
/// </summary>
public static class GitHubAccountSources
{
    private const string GitHubHost = "github.com";

    public static IReadOnlyList<AccountHit> ReadLocal()
    {
        var hits = new List<AccountHit>();

        var gh = Jhj.Core.App.AppDeploy.GhAccount;
        if (gh.Length > 0)
        {
            hits.Add(new AccountHit(AccountSourceKind.GhCli, gh));
        }
        else
        {
            foreach (var entry in Safe(() => CredentialStoreReader.Enumerate("gh:" + GitHubHost + ":*")))
            {
                var login = entry.Target[(entry.Target.LastIndexOf(':') + 1)..];
                if (login.Length > 0)
                {
                    Add(hits, AccountSourceKind.GhCli, login);
                }
            }
        }

        foreach (var login in ReadDesktopLogins())
        {
            Add(hits, AccountSourceKind.GitHubDesktop, login);
        }

        foreach (var login in ReadGitCredentialLogins())
        {
            Add(hits, AccountSourceKind.GitCredential, login);
        }

        return hits;
    }

    /// <summary>git 전역 작성자(user.name · user.email) — 없으면 빈 문자열 둘. git 이 없으면 역시 빈 값이다.</summary>
    public static async Task<(string Name, string Email)> ReadGitAuthorAsync(CancellationToken cancellationToken)
    {
        if (!ToolLocator.IsGitInstalled)
        {
            return (string.Empty, string.Empty);
        }

        var name = await ConfigAsync("user.name", cancellationToken).ConfigureAwait(false);
        var email = await ConfigAsync("user.email", cancellationToken).ConfigureAwait(false);
        return (name, email);
    }

    private static async Task<string> ConfigAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0" };
            var result = await ProcessRunner.RunAsync(ToolLocator.Git, ["config", "--global", "--get", key], null, 10, env, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut.Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> ReadDesktopLogins()
    {
        foreach (var filter in new[] { "GitHub – https://api.github.com*", "GitHub - https://api.github.com*" })
        {
            foreach (var entry in Safe(() => CredentialStoreReader.Enumerate(filter)))
            {
                var login = entry.User.Length > 0 ? entry.User : entry.Target[(entry.Target.LastIndexOf('/') + 1)..];
                if (login.Length > 0)
                {
                    yield return login;
                }
            }
        }
    }

    private static IEnumerable<string> ReadGitCredentialLogins()
    {
        foreach (var entry in Safe(() => CredentialStoreReader.Enumerate("git:https://*")))
        {
            // 계약: github.com 항목만 — 다른 호스트(gitlab · bitbucket)의 계정은 GitHub 계정이 아니다
            if (!Uri.TryCreate(entry.Target["git:".Length..], UriKind.Absolute, out var uri)
                || !uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var login = entry.User.Length > 0 ? entry.User : Uri.UnescapeDataString(uri.UserInfo);
            if (login.Length > 0 && !login.Contains("PersonalAccessToken", StringComparison.OrdinalIgnoreCase))
            {
                yield return login;
            }
        }
    }

    private static void Add(List<AccountHit> hits, AccountSourceKind source, string login)
    {
        if (!hits.Any(h => h.Source == source && h.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            hits.Add(new AccountHit(source, login));
        }
    }

    private static IReadOnlyList<CredentialStoreReader.Entry> Safe(Func<IReadOnlyList<CredentialStoreReader.Entry>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException or AccessViolationException)
        {
            return [];
        }
    }
}
