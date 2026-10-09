using System.Text.Json;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Services;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;
using GitCenter.Infrastructure.Git;
using GitCenter.Infrastructure.GitHubDesktop;

namespace GitCenter.Infrastructure.Sources;

/// <summary>사용자가 손으로 더한 경로.</summary>
public sealed class ManualSource : IRepoSource
{
    public RepoSourceKind Kind => RepoSourceKind.Manual;

    public string DisplayName => "수동 추가";

    public bool IsEnabled(AppSettings settings) => settings.ManualPaths.Count > 0;

    public Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var result = new RepoSourceResult { Kind = Kind };

        foreach (var path in settings.ManualPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.Entries.Add(new RepoEntry
            {
                Id = RepoKey.ForPath(path),
                Name = RepoKey.NameOf(path),
                Path = path,
                Sources = [Kind],
                DisplayGroup = RepoKey.GroupOf(path),
                Health = Directory.Exists(path)
                    ? GitHubDesktopReader.IsGitRepository(path) ? RepoHealth.Ok : RepoHealth.NotGitRepo
                    : RepoHealth.PathMissing,
                LastSeenUtc = DateTimeOffset.UtcNow
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>GitHub Desktop 이 들고 있는 목록.</summary>
public sealed class GitHubDesktopSource : IRepoSource
{
    public RepoSourceKind Kind => RepoSourceKind.GitHubDesktop;

    public string DisplayName => "GitHub Desktop";

    public bool IsEnabled(AppSettings settings) => settings.GitHubDesktopEnabled;

    public Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var result = new RepoSourceResult { Kind = Kind };

        if (!GitHubDesktopReader.IsInstalled)
        {
            result.Note = "GitHub Desktop 이 설치돼 있지 않습니다.";
            return Task.FromResult(result);
        }

        var (paths, note) = GitHubDesktopReader.ReadRepositoryPaths(settings.GitHubDesktopDataPath);
        result.Note = note;
        result.Failed = note is not null;

        foreach (var path in paths)
        {
            result.Entries.Add(new RepoEntry
            {
                Id = RepoKey.ForPath(path),
                Name = RepoKey.NameOf(path),
                Path = path,
                Sources = [Kind],
                DisplayGroup = RepoKey.GroupOf(path),
                LastSeenUtc = DateTimeOffset.UtcNow
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// gh CLI 로 계정·조직의 저장소를 가져온다.
/// 계약: 로컬에 clone 돼 있으면 그 경로와 합쳐지고, 없으면 NotCloned 로 목록에만 남는다.
/// 함정: gh 가 로그인돼 있지 않으면 note 만 남기고 빈 목록을 돌려준다 — 예외로 앱을 세우지 않는다.
/// </summary>
public sealed class GitHubAccountSource : IRepoSource
{
    public RepoSourceKind Kind => RepoSourceKind.GitHubAccount;

    public string DisplayName => "GitHub 계정(gh)";

    public bool IsEnabled(AppSettings settings) => settings.GitHubAccountEnabled;

    public async Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var result = new RepoSourceResult { Kind = Kind };

        var auth = await ProcessRunner.RunAsync("gh", ["auth", "status"], null, 30, null, cancellationToken).ConfigureAwait(false);
        if (auth.ExitCode != 0)
        {
            result.Note = "gh 로그인이 필요합니다 — 콘솔에서 gh auth login 을 실행해 주십시오.";
            result.Failed = true;
            return result;
        }

        var owners = settings.GitHubOwners.Count > 0 ? settings.GitHubOwners : [string.Empty];

        foreach (var owner in owners)
        {
            var args = new List<string> { "repo", "list" };
            if (!string.IsNullOrWhiteSpace(owner))
            {
                args.Add(owner);
            }

            args.AddRange(["--limit", "300", "--json", "name,owner,url,sshUrl,defaultBranchRef,isArchived"]);

            var run = await ProcessRunner.RunAsync("gh", args, null, 120, null, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                result.Note = (result.Note ?? string.Empty) + $"[{owner}] {run.StdErr.Trim()} ";
                result.Failed = true;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(run.StdOut);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? string.Empty;
                    var login = item.TryGetProperty("owner", out var ownerNode) && ownerNode.TryGetProperty("login", out var loginNode)
                        ? loginNode.GetString() ?? string.Empty
                        : owner;
                    var url = item.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;

                    result.Entries.Add(new RepoEntry
                    {
                        Id = RepoKey.ForRemote($"{login}/{name}"),
                        Name = name,
                        Path = string.Empty,
                        Owner = login,
                        RemoteUrl = url,
                        Sources = [Kind],
                        DisplayGroup = login,
                        Health = RepoHealth.NotCloned,
                        LastSeenUtc = DateTimeOffset.UtcNow
                    });
                }
            }
            catch (JsonException ex)
            {
                result.Note = $"gh 출력 해석 실패: {ex.Message}";
                result.Failed = true;
            }
        }

        return result;
    }
}

/// <summary>
/// 사용자가 정한 명령의 출력을 경로 목록으로 읽는다(예: cmd /c dir /b /s .git).
/// 계약: 한 줄에 경로 하나다 — 따옴표와 앞뒤 공백은 떼고, .git 으로 끝나면 그 부모를 쓴다.
/// </summary>
public sealed class CommandSource : IRepoSource
{
    public RepoSourceKind Kind => RepoSourceKind.Command;

    public string DisplayName => "명령 출력";

    public bool IsEnabled(AppSettings settings) => settings.CommandSources.Any(c => c.Enabled);

    public async Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var result = new RepoSourceResult { Kind = Kind };

        foreach (var definition in settings.CommandSources.Where(c => c.Enabled))
        {
            var args = SplitArguments(definition.Arguments);
            var run = await ProcessRunner.RunAsync(
                definition.FileName, args, definition.WorkingDirectory, definition.TimeoutSeconds, null, cancellationToken)
                .ConfigureAwait(false);

            if (run.ExitCode != 0)
            {
                result.Note = (result.Note ?? string.Empty) + $"[{definition.Name}] {run.StdErr.Trim()} ";
                result.Failed = true;
                continue;
            }

            foreach (var raw in run.StdOut.Split('\n'))
            {
                var line = raw.Trim().Trim('"');
                if (line.Length < 3)
                {
                    continue;
                }

                if (line.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase))
                {
                    line = Path.GetDirectoryName(line) ?? line;
                }

                if (!GitHubDesktopReader.IsGitRepository(line))
                {
                    continue;
                }

                result.Entries.Add(new RepoEntry
                {
                    Id = RepoKey.ForPath(line),
                    Name = RepoKey.NameOf(line),
                    Path = line,
                    Sources = [Kind],
                    DisplayGroup = RepoKey.GroupOf(line),
                    LastSeenUtc = DateTimeOffset.UtcNow
                });
            }
        }

        return result;
    }

    /// 계약: 따옴표로 묶인 덩어리를 하나로 본다 — 셸을 거치지 않으므로 우리가 나눠야 한다
    private static List<string> SplitArguments(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
