using GitCenter.Application.Abstractions;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;

namespace GitCenter.Application.Services;

/// <summary>
/// 여러 출처가 가져온 목록을 한 벌로 합치고, 끊어진 저장소를 다시 잇는다.
/// 계약: 출처가 하나 실패해도 나머지로 목록을 만든다 — 실패는 Notes 한 줄로 남는다.
/// 계약: 사용자가 숨긴 저장소(ExcludedIds)는 어느 출처가 다시 찾아도 목록에 올리지 않는다.
/// </summary>
public sealed class RepoRegistry(IEnumerable<IRepoSource> sources)
{
    private readonly IReadOnlyList<IRepoSource> _sources = sources.ToList();

    public sealed class RefreshResult
    {
        public List<RepoEntry> Entries { get; } = [];

        /// 계약: 출처 이름 → 한 줄 설명. 설정 화면이 그대로 보여준다
        public Dictionary<RepoSourceKind, string> Notes { get; } = [];

        public List<string> Relinked { get; } = [];

        public int SourceFailures { get; set; }
    }

    public async Task<RefreshResult> RefreshAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var refresh = new RefreshResult();
        var merged = new Dictionary<string, RepoEntry>(StringComparer.OrdinalIgnoreCase);

        var tasks = _sources
            .Where(s => s.IsEnabled(settings))
            .Select(async source =>
            {
                try
                {
                    return await source.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new RepoSourceResult { Kind = source.Kind, Failed = true, Note = ex.Message };
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                refresh.Notes[result.Kind] = result.Note!;
            }

            if (result.Failed)
            {
                refresh.SourceFailures++;
            }

            foreach (var entry in result.Entries)
            {
                Merge(merged, entry);
            }
        }

        // 왜: 계정 출처가 준 「아직 clone 안 한 것」이 로컬에 이미 있으면 한 줄로 합친다 — 이름이 같은 것끼리 본다
        FoldRemoteOnlyEntries(merged);

        var excluded = new HashSet<string>(settings.ExcludedIds, StringComparer.OrdinalIgnoreCase);

        // 왜: 가장 깊은 루트가 먼저 맞아야 한다 — 루트가 겹치면 안쪽 루트의 기준이 이긴다
        var roots = settings.ScanRoots
            .Where(r => r.Enabled && r.GroupLevel > 0 && string.IsNullOrWhiteSpace(r.Group))
            .Select(r => (Root: r, Key: RepoKey.ForPath(r.Path)))
            .OrderByDescending(r => r.Key.Length)
            .ToList();

        foreach (var entry in merged.Values)
        {
            entry.Excluded = excluded.Contains(entry.Id);
            ApplyHealth(entry);

            if (settings.AutoRelinkBroken && entry.Health == RepoHealth.PathMissing && Relink(entry, settings))
            {
                refresh.Relinked.Add(entry.Name);
                ApplyHealth(entry);
            }

            ApplyRootGroup(entry, roots);

            refresh.Entries.Add(entry);
        }

        refresh.Entries.Sort((a, b) =>
        {
            var group = string.Compare(a.DisplayGroup, b.DisplayGroup, StringComparison.OrdinalIgnoreCase);
            return group != 0 ? group : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return refresh;
    }

    private static void Merge(Dictionary<string, RepoEntry> merged, RepoEntry entry)
    {
        if (!merged.TryGetValue(entry.Id, out var existing))
        {
            merged[entry.Id] = entry;
            return;
        }

        foreach (var kind in entry.Sources.Where(k => !existing.Sources.Contains(k)))
        {
            existing.Sources.Add(kind);
        }

        existing.RemoteUrl ??= entry.RemoteUrl;
        existing.Owner ??= entry.Owner;

        if (string.IsNullOrEmpty(existing.Path) && !string.IsNullOrEmpty(entry.Path))
        {
            existing.Path = entry.Path;
        }

        if (string.IsNullOrEmpty(existing.DisplayGroup))
        {
            existing.DisplayGroup = entry.DisplayGroup;
        }
    }

    /// 함정: 이름만 보고 합치므로 서로 다른 소유자의 같은 이름 저장소는 한 줄이 된다 — 그래서 로컬 경로가 있는 쪽을 남기고 출처만 더한다
    private static void FoldRemoteOnlyEntries(Dictionary<string, RepoEntry> merged)
    {
        var locals = merged.Values
            .Where(e => !string.IsNullOrEmpty(e.Path))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var remote in merged.Values.Where(e => string.IsNullOrEmpty(e.Path)).ToList())
        {
            if (!locals.TryGetValue(remote.Name, out var local))
            {
                continue;
            }

            foreach (var kind in remote.Sources.Where(k => !local.Sources.Contains(k)))
            {
                local.Sources.Add(kind);
            }

            local.RemoteUrl ??= remote.RemoteUrl;
            local.Owner ??= remote.Owner;
            merged.Remove(remote.Id);
        }
    }

    /// 함정: 같은 저장소를 GitHub Desktop·수동 추가가 먼저 들고 오면 「바로 위 폴더」 이름이 남는다 — 합친 뒤에 루트 기준으로 다시 매긴다
    private static void ApplyRootGroup(RepoEntry entry, List<(ScanRoot Root, string Key)> roots)
    {
        if (string.IsNullOrEmpty(entry.Path) || roots.Count == 0)
        {
            return;
        }

        var pathKey = RepoKey.ForPath(entry.Path);
        foreach (var (root, key) in roots)
        {
            if (RepoKey.IsUnder(key, pathKey))
            {
                entry.DisplayGroup = RepoKey.GroupUnder(root.Path, entry.Path, root.GroupLevel);
                return;
            }
        }
    }

    private static void ApplyHealth(RepoEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Path))
        {
            entry.Health = RepoHealth.NotCloned;
            return;
        }

        if (!Directory.Exists(entry.Path))
        {
            entry.Health = RepoHealth.PathMissing;
            return;
        }

        var dot = Path.Combine(entry.Path, ".git");
        entry.Health = Directory.Exists(dot) || File.Exists(dot) ? RepoHealth.Ok : RepoHealth.NotGitRepo;
    }

    /// <summary>경로가 사라진 저장소를 같은 이름으로 루트에서 다시 찾는다.</summary>
    private static bool Relink(RepoEntry entry, AppSettings settings)
    {
        foreach (var root in settings.ScanRoots.Where(r => r.Enabled && Directory.Exists(r.Path)))
        {
            try
            {
                foreach (var candidate in Directory.EnumerateDirectories(root.Path, entry.Name, SearchOption.AllDirectories))
                {
                    var dot = Path.Combine(candidate, ".git");
                    if (!Directory.Exists(dot) && !File.Exists(dot))
                    {
                        continue;
                    }

                    entry.Path = candidate;
                    entry.DisplayGroup = RepoKey.GroupOf(candidate);
                    return true;
                }
            }
            catch
            {
                // 계약: 접근할 수 없는 폴더는 건너뛴다 — 다시 잇기는 최선 노력이다
            }
        }

        return false;
    }
}
