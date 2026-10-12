using GitCenter.Application.Abstractions;
using GitCenter.Application.Services;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;
using GitCenter.Infrastructure.GitHubDesktop;

namespace GitCenter.Infrastructure.Sources;

/// <summary>
/// 설정에 둔 루트 폴더를 지정 깊이까지 훑어 .git 을 찾는다.
/// 함정: 저장소를 찾으면 그 아래로는 더 내려가지 않는다 — node_modules 안의 서브모듈까지 들어가면 스캔이 분 단위가 된다.
/// </summary>
public sealed class RootScanSource : IRepoSource
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".idea", "venv", ".venv",
        "__pycache__", "packages", "publish", ".next", ".nuget", "AppData"
    };

    public RepoSourceKind Kind => RepoSourceKind.RootScan;

    public string DisplayName => "루트 폴더 스캔";

    public bool IsEnabled(AppSettings settings) => settings.ScanRoots.Any(r => r.Enabled);

    public Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var result = new RepoSourceResult { Kind = Kind };
        var missing = new List<string>();

        foreach (var root in settings.ScanRoots.Where(r => r.Enabled))
        {
            if (!Directory.Exists(root.Path))
            {
                missing.Add(root.Path);
                continue;
            }

            foreach (var path in Scan(root.Path, Math.Max(0, root.Depth), cancellationToken))
            {
                result.Entries.Add(new RepoEntry
                {
                    Id = RepoKey.ForPath(path),
                    Name = RepoKey.NameOf(path),
                    Path = path,
                    Sources = [Kind],
                    DisplayGroup = string.IsNullOrWhiteSpace(root.Group)
                        ? RepoKey.GroupUnder(root.Path, path, root.GroupLevel)
                        : root.Group!,
                    LastSeenUtc = DateTimeOffset.UtcNow
                });
            }
        }

        if (missing.Count > 0)
        {
            result.Note = "없는 루트: " + string.Join(", ", missing);
        }

        return Task.FromResult(result);
    }

    /// <summary>설정을 바꾸지 않고 폴더 하나만 훑는다 — 설정 화면의 「미리 보기」가 쓴다.</summary>
    public static IReadOnlyList<string> Find(string root, int depth, CancellationToken cancellationToken) =>
        Directory.Exists(root) ? Scan(root, Math.Max(0, depth), cancellationToken).ToList() : [];

    private static IEnumerable<string> Scan(string root, int depth, CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Level)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, level) = queue.Dequeue();

            if (GitHubDesktopReader.IsGitRepository(path))
            {
                yield return path;
                continue;
            }

            if (level >= depth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkipNames.Contains(name) || name.StartsWith('.') && name.Length > 1)
                {
                    continue;
                }

                queue.Enqueue((child, level + 1));
            }
        }
    }
}
