using GitCenter.Application.Abstractions;
using GitCenter.Application.Services;
using GitCenter.Core.Models;

namespace GitCenter.Infrastructure.Git;

/// <summary>
/// 저장소 하나를 읽고 조작한다 — 모든 일이 git 명령 한두 번이다.
/// 계약: 되돌릴 수 없는 명령(force push · reset --hard · clean)은 어디에서도 부르지 않는다.
/// 함정: 원격을 건드리는 작업(fetch·pull·push)은 사용자가 단추를 눌렀을 때만 일어난다 — 상태 읽기는 절대 원격에 닿지 않는다.
/// </summary>
public sealed class GitService(IGitRunner runner) : IGitService
{
    private const char Unit = (char)0x1f;

    private readonly IGitRunner _runner = runner;

    public async Task<RepoStatus> ReadStatusAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var status = new RepoStatus();

        if (!Directory.Exists(repo.Path))
        {
            status.Error = "경로가 없습니다.";
            return status;
        }

        var porcelain = await _runner.RunAsync(repo.Path,
            ["status", "--porcelain=v2", "--branch", "--show-stash"], cancellationToken, 60).ConfigureAwait(false);

        if (!porcelain.Ok)
        {
            status.Error = porcelain.Text;
            return status;
        }

        var parsed = PorcelainV2.Parse(porcelain.StdOut);
        status.Branch = parsed.Branch;
        status.IsDetached = parsed.Detached;
        status.Upstream = parsed.Upstream;
        status.Ahead = parsed.Ahead;
        status.Behind = parsed.Behind;
        status.StashCount = parsed.Stash;
        status.Staged = parsed.Changes.Count(c => c.IsStaged && !c.IsConflict);
        status.Unstaged = parsed.Changes.Count(c => !c.IsUntracked && !c.IsConflict && c.HasWorkTreeChange);
        status.Untracked = parsed.Changes.Count(c => c.IsUntracked);
        status.Conflicted = parsed.Changes.Count(c => c.IsConflict);

        // 왜: 줄 수는 바뀐 것이 있을 때만 센다 — 깨끗한 저장소마다 diff 를 돌리면 새로 고침이 그만큼 느려진다
        if (status.Staged + status.Unstaged > 0)
        {
            var stat = await _runner.RunAsync(repo.Path, ["diff", "HEAD", "--shortstat"], cancellationToken, 30).ConfigureAwait(false);
            if (stat.Ok)
            {
                (status.Insertions, status.Deletions) = ParseShortStat(stat.StdOut);
            }
        }

        var log = await _runner.RunAsync(repo.Path,
            ["log", "-1", $"--format=%H{Unit}%s{Unit}%an{Unit}%cI"], cancellationToken, 30).ConfigureAwait(false);

        if (log.Ok)
        {
            var fields = log.StdOut.Trim().Split(Unit);
            if (fields.Length >= 4)
            {
                status.LastCommitSha = fields[0].Length >= 7 ? fields[0][..7] : fields[0];
                status.LastCommitSubject = fields[1];
                status.LastCommitAuthor = fields[2];
                status.LastCommitWhen = DateTimeOffset.TryParse(fields[3], out var when) ? when : null;
            }
        }

        var gitDir = await ResolveGitDirAsync(repo, cancellationToken).ConfigureAwait(false);
        status.Operation = DetectOperation(gitDir);
        status.FetchedAtUtc = ReadFetchTimeUtc(gitDir);

        var defaultBranch = await ReadDefaultBranchAsync(repo, cancellationToken).ConfigureAwait(false);
        status.DefaultBranch = defaultBranch;

        if (defaultBranch is not null && !status.IsDetached && status.Branch != defaultBranch)
        {
            var reference = await ResolveDefaultRefAsync(repo, defaultBranch, cancellationToken).ConfigureAwait(false);
            if (reference is not null)
            {
                var counts = await _runner.RunAsync(repo.Path,
                    ["rev-list", "--left-right", "--count", $"{reference}...HEAD"], cancellationToken, 30).ConfigureAwait(false);

                if (counts.Ok)
                {
                    var parts = counts.StdOut.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        status.BehindDefault = int.TryParse(parts[0], out var behind) ? behind : 0;
                        status.AheadDefault = int.TryParse(parts[1], out var ahead) ? ahead : 0;
                    }
                }
            }
        }

        return status;
    }

    /// 계약: 「3 files changed, 10 insertions(+), 2 deletions(-)」 에서 두 수만 뽑는다 — 한쪽이 없으면 그 낱말째 빠진다
    internal static (int Insertions, int Deletions) ParseShortStat(string text)
    {
        int Pick(string word)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var part = parts.FirstOrDefault(p => p.Contains(word, StringComparison.Ordinal));
            return part is not null && int.TryParse(part.Split(' ')[0], out var n) ? n : 0;
        }

        return (Pick("insertion"), Pick("deletion"));
    }

    public async Task<IReadOnlyList<DateOnly>> ReadCommitDaysAsync(RepoEntry repo, int days, CancellationToken cancellationToken)
    {
        // 계약: %cs 는 커밋한 날(yyyy-MM-dd)이다 — 커밋이 없는 새 저장소는 실패로 돌아오고 빈 목록이 된다
        var log = await _runner.RunAsync(repo.Path,
            ["log", $"--since={days}.days.ago", "--format=%cs"], cancellationToken, 60).ConfigureAwait(false);
        if (!log.Ok)
        {
            return [];
        }

        var found = new List<DateOnly>();
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParseExact(line, "yyyy-MM-dd", out var day))
            {
                found.Add(day);
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<CommitInfo>> ReadCommitsAsync(RepoEntry repo, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // 왜: --since·--until 은 시각까지 견줘 날 경계에서 어긋난다 — 이틀씩 넉넉히 받아 커밋한 날(%cs)로 다시 거른다
        // 계약: 제목(%s)을 맨 끝에 둔다 — 제목 안에 구분 글자가 있어도 앞 칸이 밀리지 않는다
        var log = await _runner.RunAsync(repo.Path,
        [
            "log", $"--since={from.AddDays(-2):yyyy-MM-dd}", $"--until={to.AddDays(2):yyyy-MM-dd}",
            $"--format=%H{Unit}%cs{Unit}%an{Unit}%cI{Unit}%s"
        ], cancellationToken, 60).ConfigureAwait(false);
        if (!log.Ok)
        {
            return [];
        }

        var found = new List<CommitInfo>();
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(Unit, 5);
            if (fields.Length == 5 && DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", out var day) && day >= from && day <= to)
            {
                found.Add(new CommitInfo(fields[0], day, fields[2], DateTimeOffset.TryParse(fields[3], out var when) ? when : null, fields[4]));
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<FileChange>> ReadChangesAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var porcelain = await _runner.RunAsync(repo.Path,
            ["status", "--porcelain=v2"], cancellationToken, 60).ConfigureAwait(false);

        if (!porcelain.Ok)
        {
            return [];
        }

        var changes = PorcelainV2.Parse(porcelain.StdOut).Changes;
        if (changes.Any(c => !c.IsUntracked))
        {
            // 함정: 한글 경로는 -z 없이는 따옴표·8진수로 나온다 — -z 로 받아야 status 의 경로와 맞는다
            var stat = await _runner.RunAsync(repo.Path, ["diff", "HEAD", "--numstat", "-z"], cancellationToken, 60).ConfigureAwait(false);
            if (stat.Ok)
            {
                var lines = ParseNumStat(stat.StdOut);
                foreach (var change in changes)
                {
                    if (lines.TryGetValue(change.Path, out var found))
                    {
                        (change.Insertions, change.Deletions, change.IsBinary) = found;
                    }
                }
            }
        }

        await Task.Run(() =>
        {
            foreach (var change in changes.Where(c => c.IsUntracked))
            {
                CountNewFile(repo.Path, change);
            }
        }, cancellationToken).ConfigureAwait(false);

        return changes;
    }

    /// 계약: 「추가⇥삭제⇥경로␀」 가 이어진다 — 이름이 바뀐 파일은 경로 칸이 비고 「옛 경로␀새 경로␀」 가 뒤따른다
    /// 계약: 바이너리는 두 수 자리에 '-' 가 온다
    internal static Dictionary<string, (int? Insertions, int? Deletions, bool IsBinary)> ParseNumStat(string text)
    {
        var found = new Dictionary<string, (int?, int?, bool)>(StringComparer.Ordinal);
        var tokens = text.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[2];
            if (path.Length == 0 && i + 2 < tokens.Length)
            {
                path = tokens[i + 2];
                i += 2;
            }

            found[path] = int.TryParse(parts[0], out var added) && int.TryParse(parts[1], out var removed)
                ? (added, removed, false)
                : (null, null, true);
        }

        return found;
    }

    // 왜: 추적 전 파일은 diff 에 안 나온다 — 파일을 읽어 줄 수를 센다. 큰 파일은 세지 않고 비워 둔다
    private const long NewFileCountLimit = 2 * 1024 * 1024;

    private static void CountNewFile(string root, FileChange change)
    {
        try
        {
            // 함정: status 는 통째로 새 폴더를 「폴더/」 한 줄로 적는다 — 파일이 아니면 세지 않는다
            var file = new FileInfo(Path.Combine(root, change.Path));
            if (!file.Exists || file.Length > NewFileCountLimit)
            {
                return;
            }

            var bytes = File.ReadAllBytes(file.FullName);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
            {
                change.IsBinary = true;
                return;
            }

            var count = bytes.Count(b => b == (byte)'\n');
            change.Insertions = bytes.Length > 0 && bytes[^1] != (byte)'\n' ? count + 1 : count;
            change.Deletions = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 왜: 잠긴 파일 하나 때문에 목록 전체를 못 보여 주면 안 된다 — 그 줄만 비워 둔다
        }
    }

    /// <summary>한 파일의 변경 내용을 줄 단위로 읽는다.</summary>
    /// 계약: 읽기뿐이다 — diff 는 작업 트리를 건드리지 않는다
    /// 함정: 아직 추적하지 않는 파일은 diff 가 빈 결과를 낸다 — --no-index 로 /dev/null 과 견준다
    public async Task<FileDiff> ReadDiffAsync(RepoEntry repo, FileChange change, CancellationToken cancellationToken)
    {
        var path = change.Path;
        var diff = new FileDiff { Path = path };

        List<string> args = change.IsUntracked
            ? ["diff", "--no-color", "--no-index", "--", NullDevice, path]
            : change.IsStaged && !change.HasWorkTreeChange
                ? ["diff", "--no-color", "--cached", "--", path]
                : ["diff", "--no-color", "--", path];

        var result = await _runner.RunAsync(repo.Path, args, cancellationToken, 60).ConfigureAwait(false);

        // 왜: --no-index 는 두 파일이 다르면 종료 코드 1 을 낸다 — 실패가 아니라 「다름」이다
        var text = result.StdOut;
        if (text.Length == 0 && !result.Ok && !change.IsUntracked)
        {
            diff.Note = result.StdErr.Length > 0 ? result.StdErr.Trim() : "변경 내용을 읽지 못했습니다.";
            return diff;
        }

        if (text.Contains("Binary files ", StringComparison.Ordinal) || text.Contains("GIT binary patch", StringComparison.Ordinal))
        {
            diff.IsBinary = true;
            diff.Note = "바이너리 파일이라 줄 단위로 견줄 수 없습니다.";
            return diff;
        }

        ParseUnifiedDiff(text, diff);

        if (diff.Lines.Count == 0)
        {
            // 함정: 스테이지에만 올라간 파일은 작업 트리 diff 가 비어 있다 — 그때는 --cached 로 한 번 더 본다
            if (change.IsStaged)
            {
                var staged = await _runner.RunAsync(repo.Path,
                    ["diff", "--no-color", "--cached", "--", path], cancellationToken, 60).ConfigureAwait(false);
                ParseUnifiedDiff(staged.StdOut, diff);
            }

            if (diff.Lines.Count == 0)
            {
                diff.Note = "보여줄 변경 줄이 없습니다 — 이름만 바뀌었거나 권한만 달라진 파일일 수 있습니다.";
            }
        }

        return diff;
    }

    // 계약: /dev/null 은 Windows git 에서도 그대로 통한다 — NUL 로 바꾸면 --no-index 가 못 읽는다
    private const string NullDevice = "/dev/null";

    private static void ParseUnifiedDiff(string text, FileDiff diff)
    {
        if (text.Length == 0)
        {
            return;
        }

        var oldNo = 0;
        var newNo = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldNo, newNo) = ParseHunkHeader(line);
                diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Hunk, Text = line });
                continue;
            }

            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
                || line.StartsWith("similarity", StringComparison.Ordinal) || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.StartsWith("old mode", StringComparison.Ordinal) || line.StartsWith("new mode", StringComparison.Ordinal))
            {
                continue;
            }

            // 계약: 「\ No newline at end of file」 는 내용이 아니라 알림이다
            if (line.StartsWith('\\'))
            {
                continue;
            }

            switch (line[0])
            {
                case '+':
                    diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = line[1..], NewNo = newNo++ });
                    diff.Added++;
                    break;
                case '-':
                    diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = line[1..], OldNo = oldNo++ });
                    diff.Removed++;
                    break;
                default:
                    diff.Lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = line.Length > 0 ? line[1..] : string.Empty,
                        OldNo = oldNo++,
                        NewNo = newNo++
                    });
                    break;
            }
        }
    }

    // 계약: "@@ -12,7 +12,9 @@" 에서 두 시작 줄 번호만 뽑는다
    private static (int Old, int New) ParseHunkHeader(string line)
    {
        var old = 1;
        var next = 1;
        var parts = line.Split(' ');
        foreach (var part in parts)
        {
            if (part.StartsWith('-'))
            {
                old = FirstNumber(part[1..]);
            }
            else if (part.StartsWith('+'))
            {
                next = FirstNumber(part[1..]);
            }
        }

        return (old, next);
    }

    private static int FirstNumber(string text)
    {
        var comma = text.IndexOf(',');
        var head = comma < 0 ? text : text[..comma];
        return int.TryParse(head, out var value) ? value : 1;
    }

    public async Task<IReadOnlyList<BranchInfo>> ReadBranchesAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var format = string.Join(Unit,
            "%(refname:short)", "%(upstream:short)", "%(upstream:track)",
            "%(committerdate:iso-strict)", "%(contents:subject)", "%(HEAD)", "%(refname)");

        var result = await _runner.RunAsync(repo.Path,
            ["for-each-ref", "--sort=-committerdate", $"--format={format}", "refs/heads", "refs/remotes"],
            cancellationToken, 60).ConfigureAwait(false);

        if (!result.Ok)
        {
            return [];
        }

        var list = new List<BranchInfo>();
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split(Unit);
            if (f.Length < 7)
            {
                continue;
            }

            var name = f[0];
            // 함정: refs/remotes/origin/HEAD 의 짧은 이름은 "origin" 이라 이름만 보면 브랜치처럼 보인다 — 전체 ref 로 가린다
            if (f[6].EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var (ahead, behind) = ParseTrack(f[2]);
            list.Add(new BranchInfo
            {
                Name = name,
                IsRemote = f[6].StartsWith("refs/remotes/", StringComparison.Ordinal),
                IsCurrent = f[5].Trim() == "*",
                Upstream = string.IsNullOrWhiteSpace(f[1]) ? null : f[1],
                Ahead = ahead,
                Behind = behind,
                LastCommitWhen = DateTimeOffset.TryParse(f[3], out var when) ? when : null,
                LastCommitSubject = f[4]
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<ConflictFile>> ReadConflictsAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var names = await _runner.RunAsync(repo.Path,
            ["diff", "--name-only", "--diff-filter=U"], cancellationToken, 60).ConfigureAwait(false);

        if (!names.Ok)
        {
            return [];
        }

        var files = new List<ConflictFile>();
        foreach (var raw in names.StdOut.Split('\n'))
        {
            var relative = raw.Trim();
            if (relative.Length == 0)
            {
                continue;
            }

            var full = Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var content = File.Exists(full) ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false) : string.Empty;
                var (merged, hasBase, note) = await BuildMergedAsync(repo, relative, cancellationToken).ConfigureAwait(false);
                if (merged is null || merged.Contains((byte)0))
                {
                    var plain = ConflictParser.Parse(relative, content);
                    plain.Note ??= note;
                    files.Add(plain);
                    continue;
                }

                var file = ConflictParser.Parse(relative, Decode(merged));
                file.HasBase = hasBase;
                file.CanResolve = file.Hunks.Count > 0;

                // 왜: 표식이 없는 작업 파일은 사용자가 이미 손으로 고친 것이다 — 덮어쓰기 전에 알려야 한다
                if (!content.Contains("<<<<<<<", StringComparison.Ordinal))
                {
                    file.Note = "작업 파일에는 표식이 없습니다 — 이미 손으로 고쳤다면 여기서 적용하지 마십시오(적용하면 지금 파일은 백업 후 덮어씁니다).";
                }

                files.Add(file);
            }
            catch (Exception ex)
            {
                files.Add(new ConflictFile { Path = relative, Note = $"읽지 못했습니다: {ex.Message}" });
            }
        }

        return files;
    }

    /// <summary>
    /// 인덱스의 세 단계(공통 조상 · 내 것 · 상대 것)로 diff3 표식이 든 내용을 새로 만든다.
    /// 왜: 작업 파일의 표식에는 공통 조상이 없을 수 있다(merge.conflictStyle 기본값) — 조상이 있어야 자동 판정을 한다.
    /// 계약: 작업 파일을 건드리지 않는다 — git 이 꺼내 준 임시 파일 셋 위에서만 돌리고 끝나면 지운다.
    /// 함정: 글자로 풀지 않고 바이트로 읽는다 — 인코딩을 한 번 거치면 UTF-8 이 아닌 파일이 조용히 깨진다.
    /// </summary>
    private async Task<(byte[]? Merged, bool HasBase, string? Note)> BuildMergedAsync(RepoEntry repo, string path, CancellationToken ct)
    {
        var temp = await _runner.RunAsync(repo.Path, ["checkout-index", "--stage=all", "--temp", "--", path], ct, 60).ConfigureAwait(false);
        var names = temp.StdOut.Split('\n')[0].Split('\t')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!temp.Ok || names.Length != 3)
        {
            return (null, false, "인덱스에서 양쪽 내용을 꺼내지 못했습니다.");
        }

        var paths = names.Select(n => n == "." ? null : Path.Combine(repo.Path, n)).ToArray();
        string? emptyBase = null;
        try
        {
            if (paths[1] is not { } ours || paths[2] is not { } theirs)
            {
                return (null, false, "한쪽이 지운 파일입니다 — 남길지 지울지는 편집기·터미널에서 정해 주십시오.");
            }

            if (paths[0] is null)
            {
                emptyBase = Path.Combine(repo.Path, ".merge_file_empty_" + Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(emptyBase, [], ct).ConfigureAwait(false);
            }

            // 계약: merge-file 은 결과를 첫 파일(내 것)에 쓰고, 남은 충돌 수를 종료 코드로 돌려준다 — 음수·128 이상은 실패다
            var merge = await _runner.RunAsync(repo.Path,
                ["merge-file", "--diff3", "-L", "HEAD", "-L", "base", "-L", "incoming", ours, paths[0] ?? emptyBase!, theirs],
                ct, 60).ConfigureAwait(false);
            if (merge.ExitCode is < 0 or >= 128)
            {
                return (null, false, "줄 단위로 견줄 수 없는 파일입니다(바이너리 등).");
            }

            return (await File.ReadAllBytesAsync(ours, ct).ConfigureAwait(false), paths[0] is not null, null);
        }
        finally
        {
            foreach (var file in paths.Append(emptyBase))
            {
                if (file is not null && File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    public async Task<ConflictActionResult> ResolveConflictAsync(RepoEntry repo, string path, IReadOnlyList<ConflictChoice> choices, CancellationToken cancellationToken)
    {
        var (merged, _, note) = await BuildMergedAsync(repo, path, cancellationToken).ConfigureAwait(false);
        if (merged is null)
        {
            return new(false, note ?? "충돌 내용을 읽지 못했습니다.");
        }

        // 계약: Latin1 은 바이트와 글자가 1:1 이다 — 표식은 ASCII 라 이대로 줄을 골라 내도 원래 바이트가 그대로 남는다
        var resolved = ConflictResolver.Apply(System.Text.Encoding.Latin1.GetString(merged), choices, out var error);
        if (resolved is null)
        {
            return new(false, error);
        }

        var full = Path.Combine(repo.Path, path.Replace('/', Path.DirectorySeparatorChar));
        var backup = string.Empty;
        if (File.Exists(full))
        {
            var gitDir = await _runner.RunAsync(repo.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken, 20).ConfigureAwait(false);
            if (!gitDir.Ok)
            {
                return new(false, "백업 자리를 찾지 못해 적용하지 않았습니다.");
            }

            // 계약: 덮어쓰기 전의 파일을 남긴다 — 이 앱은 되돌릴 수 없는 일을 하지 않는다
            var folder = Path.Combine(gitDir.StdOut.Trim(), "jhj-conflict-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            backup = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(full, backup, true);
        }

        await File.WriteAllBytesAsync(full, System.Text.Encoding.Latin1.GetBytes(resolved), cancellationToken).ConfigureAwait(false);
        var add = await _runner.RunAsync(repo.Path, ["add", "--", path], cancellationToken, 60).ConfigureAwait(false);
        return add.Ok
            ? new(true, $"{choices.Count}구간을 풀어 스테이지했습니다." + (backup.Length > 0 ? $" 이전 파일: {backup}" : string.Empty))
            : new(false, "파일은 다시 썼지만 스테이지하지 못했습니다: " + FirstLine(add.Text));
    }

    public async Task<ConflictActionResult> ConcludeMergeAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var gitDir = await _runner.RunAsync(repo.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken, 20).ConfigureAwait(false);
        if (!gitDir.Ok || DetectOperation(gitDir.StdOut.Trim()) != GitOperationState.Merging)
        {
            return new(false, "병합 중이 아닙니다 — 리베이스·체리픽은 터미널에서 --continue 로 이어 주십시오.");
        }

        var left = await _runner.RunAsync(repo.Path, ["diff", "--name-only", "--diff-filter=U"], cancellationToken, 60).ConfigureAwait(false);
        if (!left.Ok || left.StdOut.Trim().Length > 0)
        {
            return new(false, "아직 풀리지 않은 충돌이 있습니다.");
        }

        var commit = await _runner.RunAsync(repo.Path, ["commit", "--no-edit"], cancellationToken, 120).ConfigureAwait(false);
        return commit.Ok ? new(true, "병합을 커밋으로 맺었습니다.") : new(false, FirstLine(commit.Text));
    }

    /// 계약: origin/HEAD 가 있으면 그것, 없으면 main·master 순으로 본다 — 둘 다 없으면 null
    public async Task<string?> ReadDefaultBranchAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var head = await _runner.RunAsync(repo.Path,
            ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], cancellationToken, 20).ConfigureAwait(false);

        if (head.Ok)
        {
            var name = head.StdOut.Trim();
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < name.Length)
            {
                return name[(slash + 1)..];
            }
        }

        foreach (var candidate in new[] { "main", "master", "develop" })
        {
            var exists = await _runner.RunAsync(repo.Path,
                ["rev-parse", "--verify", "--quiet", candidate], cancellationToken, 20).ConfigureAwait(false);
            if (exists.Ok && exists.StdOut.Trim().Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<BulkStepResult> ExecuteAsync(
        RepoEntry repo,
        BulkOperationKind kind,
        BulkOperationOptions options,
        CancellationToken cancellationToken)
    {
        var step = new BulkStepResult
        {
            RepoId = repo.Id,
            RepoName = repo.Name,
            Kind = kind
        };

        var started = DateTimeOffset.Now;

        try
        {
            if (!Directory.Exists(repo.Path))
            {
                step.Outcome = StepOutcome.Failed;
                step.Message = "경로가 없습니다.";
                return step;
            }

            switch (kind)
            {
                case BulkOperationKind.Fetch:
                    await FetchAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.UpdateDefaultBranch:
                    await UpdateDefaultBranchAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.MergeDefaultIntoCurrent:
                    await MergeDefaultIntoCurrentAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.PullCurrent:
                    await PullCurrentAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.UpdateAllTrackedBranches:
                    await UpdateAllTrackedAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.CommitAll:
                    await CommitAllAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.Push:
                    await PushAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.CommitAndPush:
                    await CommitAllAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                    if (step.Outcome is StepOutcome.Success or StepOutcome.Skipped)
                    {
                        var commitMessage = step.Message;
                        await PushAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
                        step.Message = $"{commitMessage} · {step.Message}";
                    }

                    break;
                case BulkOperationKind.AbortOperation:
                    await AbortAsync(repo, step, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            step.Outcome = StepOutcome.Canceled;
            step.Message = "멈췄습니다.";
        }
        catch (Exception ex)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = ex.Message;
        }

        step.StartedAt = started;
        step.Elapsed = DateTimeOffset.Now - started;
        return step;
    }

    // ── 작업별 구현 ───────────────────────────────────────────

    private async Task FetchAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path,
            ["fetch", "--prune", "--tags", options.RemoteName], ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (!result.Ok)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = FirstLine(result.Text);
            step.Detail = result.Text;
            return;
        }

        var changed = result.Text.Length > 0;
        step.Outcome = changed ? StepOutcome.Success : StepOutcome.Skipped;
        step.Message = changed ? "원격을 받았습니다." : "바뀐 것이 없습니다.";
        step.Detail = result.Text;
    }

    /// 계약: 받는 브랜치를 적었으면 그것을, 비웠으면 저장소의 기본 브랜치를 쓴다
    private async Task<string?> ReceiveBranchAsync(RepoEntry repo, BulkOperationOptions options, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(options.ReceiveBranch)
            ? await ReadDefaultBranchAsync(repo, ct).ConfigureAwait(false)
            : options.ReceiveBranch.Trim();

    private async Task UpdateDefaultBranchAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var defaultBranch = await ReceiveBranchAsync(repo, options, ct).ConfigureAwait(false);
        if (defaultBranch is null)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "기본 브랜치를 찾지 못했습니다.";
            return;
        }

        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);

        if (current == defaultBranch)
        {
            await PullCurrentAsync(repo, options, step, ct).ConfigureAwait(false);
            step.Message = $"{defaultBranch}: {step.Message}";
            return;
        }

        // 왜: 체크아웃을 바꾸지 않고 로컬 기본 브랜치만 앞으로 당긴다 — 빨리감기가 안 되면 git 이 거부하고 그대로 남는다
        var result = await _runner.RunAsync(repo.Path,
            ["fetch", options.RemoteName, $"{defaultBranch}:{defaultBranch}"], ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var changed = result.Text.Length > 0;
            step.Outcome = changed ? StepOutcome.Success : StepOutcome.Skipped;
            step.Message = changed ? $"{defaultBranch} 를 갱신했습니다." : $"{defaultBranch} 는 이미 최신입니다.";
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = result.Text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            ? $"{defaultBranch} 가 갈라져 빨리감기로 갱신할 수 없습니다."
            : FirstLine(result.Text);
        step.Detail = result.Text;
    }

    private async Task MergeDefaultIntoCurrentAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var defaultBranch = await ReceiveBranchAsync(repo, options, ct).ConfigureAwait(false);
        if (defaultBranch is null)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "기본 브랜치를 찾지 못했습니다.";
            return;
        }

        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (current == defaultBranch)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = $"지금이 {defaultBranch} 입니다.";
            return;
        }

        var reference = await ResolveDefaultRefAsync(repo, defaultBranch, ct).ConfigureAwait(false);
        if (reference is null)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = $"{defaultBranch} 를 찾지 못했습니다 — 먼저 Fetch 해 주십시오.";
            return;
        }

        var result = await _runner.RunAsync(repo.Path,
            ["merge", "--no-edit", reference], ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var already = result.StdOut.Contains("Already up to date", StringComparison.OrdinalIgnoreCase);
            step.Outcome = already ? StepOutcome.Skipped : StepOutcome.Success;
            step.Message = already ? $"{reference} 와 이미 같습니다." : $"{reference} 를 {current} 로 병합했습니다.";
            step.Detail = result.Text;
            return;
        }

        var conflicts = await ReadConflictsAsync(repo, ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            step.Outcome = StepOutcome.Conflict;
            step.Message = $"충돌 {conflicts.Count}건 — 병합이 멈춰 있습니다.";
            step.ConflictPaths.AddRange(conflicts.Select(c => c.Path));
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = FirstLine(result.Text);
        step.Detail = result.Text;
    }

    private async Task PullCurrentAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var args = new List<string> { "pull" };
        if (options.FastForwardOnly)
        {
            args.Add("--ff-only");
        }
        else
        {
            args.Add("--no-rebase");
            args.Add("--no-edit");
        }

        var stashed = false;
        if (options.AutoStash && await IsDirtyAsync(repo, ct).ConfigureAwait(false))
        {
            var stash = await _runner.RunAsync(repo.Path,
                ["stash", "push", "-u", "-m", "git-center-auto"], ct, options.TimeoutSeconds).ConfigureAwait(false);
            stashed = stash.Ok;
        }

        var result = await _runner.RunAsync(repo.Path, args, ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (stashed)
        {
            var pop = await _runner.RunAsync(repo.Path, ["stash", "pop"], ct, options.TimeoutSeconds).ConfigureAwait(false);
            if (!pop.Ok)
            {
                step.Detail = "stash pop 실패: " + pop.Text;
            }
        }

        if (result.Ok)
        {
            var already = result.Text.Contains("Already up to date", StringComparison.OrdinalIgnoreCase);
            step.Outcome = already ? StepOutcome.Skipped : StepOutcome.Success;
            step.Message = already ? "이미 최신입니다." : "받았습니다.";
            step.Detail = string.IsNullOrEmpty(step.Detail) ? result.Text : step.Detail;
            return;
        }

        var conflicts = await ReadConflictsAsync(repo, ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            step.Outcome = StepOutcome.Conflict;
            step.Message = $"충돌 {conflicts.Count}건";
            step.ConflictPaths.AddRange(conflicts.Select(c => c.Path));
        }
        else
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = result.Text.Contains("no tracking information", StringComparison.OrdinalIgnoreCase)
                ? "추적 중인 원격 브랜치가 없습니다."
                : FirstLine(result.Text);
        }

        step.Detail = result.Text;
    }

    /// 계약: 체크아웃된 브랜치는 pull, 나머지는 refspec 으로 빨리감기만 한다 — 갈라진 브랜치는 건드리지 않는다
    private async Task UpdateAllTrackedAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var branches = await ReadBranchesAsync(repo, ct).ConfigureAwait(false);
        var local = branches.Where(b => !b.IsRemote && b.Upstream is not null).ToList();

        if (local.Count == 0)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "추적 중인 브랜치가 없습니다.";
            return;
        }

        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        foreach (var branch in local)
        {
            if (branch.IsCurrent)
            {
                var pull = new BulkStepResult { RepoId = repo.Id, RepoName = repo.Name, Kind = BulkOperationKind.PullCurrent };
                await PullCurrentAsync(repo, options, pull, ct).ConfigureAwait(false);
                switch (pull.Outcome)
                {
                    case StepOutcome.Success:
                        updated.Add(branch.Name);
                        break;
                    case StepOutcome.Skipped:
                        skipped.Add(branch.Name);
                        break;
                    default:
                        failed.Add($"{branch.Name}: {pull.Message}");
                        break;
                }

                continue;
            }

            if (branch.Behind == 0)
            {
                skipped.Add(branch.Name);
                continue;
            }

            if (branch.Ahead > 0)
            {
                failed.Add($"{branch.Name}: 갈라져 있어 건너뛰었습니다.");
                continue;
            }

            var upstream = branch.Upstream!;
            var slash = upstream.IndexOf('/');
            var remote = slash > 0 ? upstream[..slash] : options.RemoteName;
            var remoteBranch = slash > 0 ? upstream[(slash + 1)..] : branch.Name;

            var fetch = await _runner.RunAsync(repo.Path,
                ["fetch", remote, $"{remoteBranch}:{branch.Name}"], ct, options.TimeoutSeconds).ConfigureAwait(false);

            if (fetch.Ok)
            {
                updated.Add(branch.Name);
            }
            else
            {
                failed.Add($"{branch.Name}: {FirstLine(fetch.Text)}");
            }
        }

        step.Detail = string.Join(Environment.NewLine, failed);
        if (failed.Count > 0)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = $"갱신 {updated.Count} · 실패 {failed.Count}";
            return;
        }

        step.Outcome = updated.Count > 0 ? StepOutcome.Success : StepOutcome.Skipped;
        step.Message = updated.Count > 0
            ? $"갱신 {updated.Count}개: {string.Join(", ", updated)}"
            : $"모두 최신입니다({skipped.Count}개).";
    }

    private async Task CommitAllAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        if (!await IsDirtyAsync(repo, ct).ConfigureAwait(false))
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "커밋할 변경이 없습니다.";
            return;
        }

        var message = string.IsNullOrWhiteSpace(options.CommitMessage) ? "JHJ Git Center 일괄 처리" : options.CommitMessage;

        // 계약: -m 을 두 번 주면 git 이 둘째를 빈 줄 뒤의 본문으로 붙인다
        List<string> commitArgs = ["commit", "-m", message];
        if (!string.IsNullOrWhiteSpace(options.CommitBody))
        {
            commitArgs.AddRange(["-m", options.CommitBody]);
        }

        var add = await _runner.RunAsync(repo.Path, ["add", "-A"], ct, options.TimeoutSeconds).ConfigureAwait(false);
        if (!add.Ok)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = FirstLine(add.Text);
            step.Detail = add.Text;
            return;
        }

        var commit = await _runner.RunAsync(repo.Path, commitArgs, ct, options.TimeoutSeconds).ConfigureAwait(false);
        if (commit.Ok)
        {
            step.Outcome = StepOutcome.Success;
            step.Message = "커밋했습니다.";
            step.Detail = commit.StdOut.Trim();
            return;
        }

        if (commit.StdOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "커밋할 변경이 없습니다.";
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = FirstLine(commit.Text);
        step.Detail = commit.Text;
    }

    private async Task PushAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var branch = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branch) || branch == "(detached)")
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "브랜치가 분리돼 있어 올리지 않았습니다.";
            return;
        }

        var upstream = await _runner.RunAsync(repo.Path,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct, 20).ConfigureAwait(false);

        // 계약: 올리는 브랜치가 지금 브랜치와 다르면 그 원격 브랜치로 올린다 — upstream 은 바꾸지 않고, 빨리감기가 안 되면 git 이 거부한다
        var target = options.PushBranch.Trim();
        var elsewhere = target.Length > 0 && !string.Equals(target, branch, StringComparison.Ordinal);

        var args = elsewhere
            ? new List<string> { "push", options.RemoteName, $"HEAD:refs/heads/{target}" }
            : upstream.Ok
                ? ["push", options.RemoteName, $"HEAD:{branch}"]
                : options.SetUpstreamOnPush
                    ? ["push", "-u", options.RemoteName, branch]
                    : ["push", options.RemoteName, branch];

        var result = await _runner.RunAsync(repo.Path, args, ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var already = result.Text.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase);
            step.Outcome = already ? StepOutcome.Skipped : StepOutcome.Success;
            step.Message = already
                ? "올릴 것이 없습니다."
                : elsewhere ? $"{branch} 를 {options.RemoteName}/{target} 로 올렸습니다." : $"{branch} 를 올렸습니다.";
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        // 계약: 거부당하면 force 를 권하지 않는다 — 먼저 받아서 합치라고 적는다
        step.Message = result.Text.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            ? "거부됐습니다 — 원격이 앞서 있습니다. 먼저 받아서 합쳐 주십시오."
            : FirstLine(result.Text);
        step.Detail = result.Text;
    }

    private async Task AbortAsync(RepoEntry repo, BulkStepResult step, CancellationToken ct)
    {
        var gitDir = await ResolveGitDirAsync(repo, ct).ConfigureAwait(false);
        var state = DetectOperation(gitDir);

        var args = state switch
        {
            GitOperationState.Merging => new[] { "merge", "--abort" },
            GitOperationState.Rebasing => ["rebase", "--abort"],
            GitOperationState.CherryPicking => ["cherry-pick", "--abort"],
            GitOperationState.Reverting => ["revert", "--abort"],
            _ => []
        };

        if (args.Length == 0)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "진행 중인 작업이 없습니다.";
            return;
        }

        var result = await _runner.RunAsync(repo.Path, args, ct, 60).ConfigureAwait(false);
        step.Outcome = result.Ok ? StepOutcome.Success : StepOutcome.Failed;
        step.Message = result.Ok ? "되돌렸습니다." : FirstLine(result.Text);
        step.Detail = result.Text;
    }

    // ── 거들개 ────────────────────────────────────────────────

    private async Task<string> CurrentBranchAsync(RepoEntry repo, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path, ["rev-parse", "--abbrev-ref", "HEAD"], ct, 20).ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : string.Empty;
    }

    private async Task<bool> IsDirtyAsync(RepoEntry repo, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path, ["status", "--porcelain"], ct, 60).ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Length > 0;
    }

    /// 계약: 원격 추적본(origin/main)이 있으면 그것을, 없으면 로컬 브랜치를 기준으로 쓴다
    private async Task<string?> ResolveDefaultRefAsync(RepoEntry repo, string defaultBranch, CancellationToken ct)
    {
        var remote = $"origin/{defaultBranch}";
        var check = await _runner.RunAsync(repo.Path, ["rev-parse", "--verify", "--quiet", remote], ct, 20).ConfigureAwait(false);
        if (check.Ok && check.StdOut.Trim().Length > 0)
        {
            return remote;
        }

        var local = await _runner.RunAsync(repo.Path, ["rev-parse", "--verify", "--quiet", defaultBranch], ct, 20).ConfigureAwait(false);
        return local.Ok && local.StdOut.Trim().Length > 0 ? defaultBranch : null;
    }

    private async Task<string?> ResolveGitDirAsync(RepoEntry repo, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path, ["rev-parse", "--absolute-git-dir"], ct, 20).ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    private static GitOperationState DetectOperation(string? gitDir)
    {
        if (gitDir is null || !Directory.Exists(gitDir))
        {
            return GitOperationState.None;
        }

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
        {
            return GitOperationState.Merging;
        }

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
        {
            return GitOperationState.Rebasing;
        }

        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
        {
            return GitOperationState.CherryPicking;
        }

        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
        {
            return GitOperationState.Reverting;
        }

        return File.Exists(Path.Combine(gitDir, "BISECT_LOG")) ? GitOperationState.Bisecting : GitOperationState.None;
    }

    /// 왜: FETCH_HEAD 의 수정 시각이 「마지막으로 원격을 읽은 때」다 — git 이 따로 기록해 주지 않는다
    private static DateTimeOffset? ReadFetchTimeUtc(string? gitDir)
    {
        if (gitDir is null)
        {
            return null;
        }

        var path = Path.Combine(gitDir, "FETCH_HEAD");
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }

    /// 계약: upstream:track 은 "[ahead 1, behind 2]" · "[gone]" · 빈 칸 중 하나다
    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            return (0, 0);
        }

        var ahead = 0;
        var behind = 0;
        var parts = track.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var fields = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !int.TryParse(fields[1], out var value))
            {
                continue;
            }

            if (fields[0].Equals("ahead", StringComparison.OrdinalIgnoreCase))
            {
                ahead = value;
            }
            else if (fields[0].Equals("behind", StringComparison.OrdinalIgnoreCase))
            {
                behind = value;
            }
        }

        return (ahead, behind);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
