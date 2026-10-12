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

        // 왜: 원격 없는 로컬 전용 저장소를 「Push 전」으로 세지 않으려면 여기서 한 번 읽어야 한다 — RepoHealth.NoRemote 는 아무도 매기지 않는다
        var remotes = await _runner.RunAsync(repo.Path, ["remote"], cancellationToken, 20).ConfigureAwait(false);
        status.HasRemote = !remotes.Ok || remotes.StdOut.Trim().Length > 0;

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

    public async Task<IReadOnlyDictionary<string, int>> ReadFileExtensionsAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        // 계약: 추적 중인 파일만 센다 — 무시된 산출물(node_modules · bin)은 ls-files 에 안 나온다
        // 함정: quotepath 를 끄지 않으면 한글 경로가 따옴표로 묶여 끝 따옴표가 확장자에 붙는다
        var files = await _runner.RunAsync(repo.Path,
            ["-c", "core.quotepath=off", "ls-files"], cancellationToken, 60).ConfigureAwait(false);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!files.Ok)
        {
            return found;
        }

        foreach (var line in files.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = line[(line.LastIndexOf('/') + 1)..].TrimEnd('"');
            var dot = name.LastIndexOf('.');
            // 계약: 점으로 시작하는 이름(.gitignore)과 점으로 끝나는 이름은 확장자가 없는 것으로 센다
            var extension = dot > 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : string.Empty;
            found[extension] = found.GetValueOrDefault(extension) + 1;
        }

        return found;
    }

    public async Task<CommitFacts> ReadCommitFactsAsync(RepoEntry repo, int days, CancellationToken cancellationToken)
    {
        // 계약: 시(%H)를 앞에 둔다 — 작성자 이름에 탭이 있어도 앞 칸이 밀리지 않는다
        var log = await _runner.RunAsync(repo.Path,
            ["log", $"--since={days}.days.ago", "--date=format:%H", "--format=%cd%x09%an"], cancellationToken, 60).ConfigureAwait(false);
        if (!log.Ok)
        {
            return CommitFacts.Empty;
        }

        var authors = new Dictionary<string, int>(StringComparer.Ordinal);
        var hours = new int[24];
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = line.IndexOf('\t');
            if (cut <= 0 || !int.TryParse(line[..cut], out var hour) || hour is < 0 or > 23)
            {
                continue;
            }

            hours[hour]++;
            var author = line[(cut + 1)..].Trim();
            authors[author] = authors.GetValueOrDefault(author) + 1;
        }

        return new CommitFacts(authors, hours);
    }

    public async Task<IReadOnlyList<CommitInfo>> ReadBranchLogAsync(RepoEntry repo, string reference, int max, CancellationToken cancellationToken)
    {
        // 함정: 「--」 를 붙여야 같은 이름의 파일이 있어도 ref 로 읽는다
        var log = await _runner.RunAsync(repo.Path,
            ["log", reference, "-n", max.ToString(System.Globalization.CultureInfo.InvariantCulture), $"--format=%H{Unit}%cs{Unit}%an{Unit}%cI{Unit}%s", "--"],
            cancellationToken, 60).ConfigureAwait(false);
        if (!log.Ok)
        {
            return [];
        }

        var found = new List<CommitInfo>();
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(Unit, 5);
            if (fields.Length == 5 && DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", out var day))
            {
                found.Add(new CommitInfo(fields[0], day, fields[2], DateTimeOffset.TryParse(fields[3], out var when) ? when : null, fields[4]));
            }
        }

        return found;
    }

    public async Task<IReadOnlySet<string>> ReadUnpushedAsync(RepoEntry repo, string reference, int max, CancellationToken cancellationToken)
    {
        var log = await _runner.RunAsync(repo.Path,
            ["log", reference, "-n", max.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%H", "--not", "--remotes", "--"],
            cancellationToken, 60).ConfigureAwait(false);
        return log.Ok
            ? log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<(bool Ok, string Message)> SwitchBranchAsync(RepoEntry repo, BranchInfo branch, CancellationToken cancellationToken)
    {
        // 계약: 원격 브랜치 이름은 「<remote>/<브랜치>」 다 — 첫 빗금 뒤가 로컬 이름이다
        var local = branch.IsRemote ? branch.Name[(branch.Name.IndexOf('/') + 1)..] : branch.Name;
        var exists = await _runner.RunAsync(repo.Path,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{local}"], cancellationToken, 20).ConfigureAwait(false);

        var result = await _runner.RunAsync(repo.Path,
            exists.Ok ? ["switch", local] : ["switch", "--track", "-c", local, branch.Name], cancellationToken, 60).ConfigureAwait(false);
        if (result.Ok)
        {
            return (true, $"{local} 로 옮겼습니다.");
        }

        return (false, result.Text.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
            ? $"변경한 파일이 {local} 와 겹쳐 옮기지 못했습니다 — 먼저 커밋해 주십시오."
            : FirstLine(result.Text));
    }

    public async Task<long> ReadObjectSizeAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var count = await _runner.RunAsync(repo.Path, ["count-objects", "-v"], cancellationToken, 30).ConfigureAwait(false);
        if (!count.Ok)
        {
            return 0;
        }

        // 계약: size 는 풀린 객체, size-pack 은 묶인 객체다 — 둘 다 KiB 로 나온다
        long total = 0;
        foreach (var line in count.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if ((line.StartsWith("size:", StringComparison.Ordinal) || line.StartsWith("size-pack:", StringComparison.Ordinal))
                && long.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out var kib))
            {
                total += kib;
            }
        }

        return total;
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

    /// 계약: 비어 있으면 stash 가 없다 — refs/stash 가 없는 저장소는 git 이 실패로 돌려주므로 그것도 빈 목록이다
    // 함정: %gs 는 「On main: 메시지」 꼴이다 — 머리말을 떼어야 브랜치와 메시지를 가를 수 있다
    public async Task<IReadOnlyList<StashEntry>> ReadStashesAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(repo.Path,
            ["stash", "list", $"--format=%gd{Unit}%H{Unit}%cI{Unit}%gs"], cancellationToken, 30).ConfigureAwait(false);

        if (!list.Ok)
        {
            return [];
        }

        var found = new List<StashEntry>();
        foreach (var line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(Unit, 4);
            if (fields.Length < 4)
            {
                continue;
            }

            var (branch, message) = SplitStashSubject(fields[3]);
            var files = await ReadStashFilesAsync(repo, fields[0], cancellationToken).ConfigureAwait(false);
            found.Add(new StashEntry(
                fields[0], fields[1],
                DateTimeOffset.TryParse(fields[2], out var when) ? when : null,
                branch, message, files));
        }

        return found;
    }

    /// 계약: 「On <브랜치>: <글>」·「WIP on <브랜치>: <글>」 둘만 가른다 — 그 밖의 모양은 통째로 글이 된다
    internal static (string Branch, string Message) SplitStashSubject(string subject)
    {
        var text = subject.Trim();
        foreach (var head in new[] { "WIP on ", "On " })
        {
            if (!text.StartsWith(head, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = text[head.Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? (rest, string.Empty) : (rest[..colon], rest[(colon + 1)..].Trim());
        }

        return (string.Empty, text);
    }

    // 왜: 「무엇이 들었나」가 없으면 stash 숫자만 보고는 지워도 되는지 판단할 수 없다 — 파일 이름까지 읽어 준다
    private async Task<IReadOnlyList<string>> ReadStashFilesAsync(RepoEntry repo, string stashRef, CancellationToken ct)
    {
        var show = await _runner.RunAsync(repo.Path,
            ["stash", "show", "--name-only", "--include-untracked", stashRef], ct, 30).ConfigureAwait(false);

        return show.Ok
            ? show.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
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

            // 왜: 원격 없는 로컬 전용 저장소에 git 원문(fatal: 'origin' does not appear…)을 그대로 내면 사용자가 읽지 못한다 — 실패가 아니라 건너뜀으로 센다
            if (NeedsRemote(kind))
            {
                var remote = await _runner.RunAsync(repo.Path, ["remote", "get-url", options.RemoteName], cancellationToken, 20).ConfigureAwait(false);
                if (!remote.Ok)
                {
                    step.Outcome = StepOutcome.Skipped;
                    step.Message = $"원격 저장소 {options.RemoteName} 이 등록돼 있지 않아 건너뛰었습니다 — 로컬에만 있는 저장소입니다. GitHub 에 올리려면 저장소 줄의 「GitHub 에 올리기」를 눌러 주십시오.";
                    step.Detail = remote.Text;
                    step.NextAction = BulkNextAction.Publish;
                    return step;
                }
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
                        // 왜: 작업 브랜치가 아니라 둘 다 건너뛰면 같은 글이 두 번 붙는다
                        step.Message = commitMessage == step.Message ? commitMessage : $"{commitMessage} · {step.Message}";
                    }

                    break;
                case BulkOperationKind.AbortOperation:
                    await AbortAsync(repo, step, cancellationToken).ConfigureAwait(false);
                    break;
                case BulkOperationKind.SwitchToWorkBranch:
                    await SwitchToWorkBranchAsync(repo, options, step, cancellationToken).ConfigureAwait(false);
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
            step.Message = DescribeFailure(result.Text);
            step.Detail = result.Text;
            return;
        }

        var changed = result.Text.Length > 0;
        step.Outcome = changed ? StepOutcome.Success : StepOutcome.Skipped;
        // 계약: 「원격」이라고만 적지 않는다 — 무엇을 받았는지 원격 이름과 브랜치 이름으로 적는다
        var remote = options.RemoteName;
        var upload = await UploadBranchAsync(repo, options, ct).ConfigureAwait(false);
        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        var names = string.Join(" · ", new[]
        {
            upload is null ? null : $"업로드 브랜치 {remote}/{upload}",
            current.Length == 0 || current == upload ? null : $"작업 브랜치 {remote}/{current}"
        }.Where(n => n is not null));
        var scope = names.Length == 0 ? $"{remote} 의 브랜치 전체" : $"{remote} 의 브랜치 전체({names} 포함)";
        step.Message = changed ? $"{scope}를 받았습니다." : $"{scope}에 새로 받을 것이 없습니다.";
        step.Detail = result.Text;
    }

    /// 계약: 업로드 브랜치(저장소별 → 공통)를 적었으면 그것을, 비웠으면 저장소의 기본 브랜치를 쓴다
    private async Task<string?> UploadBranchAsync(RepoEntry repo, BulkOperationOptions options, CancellationToken ct)
    {
        var upload = options.UploadBranchFor(repo);
        return upload.Length == 0 ? await ReadDefaultBranchAsync(repo, ct).ConfigureAwait(false) : upload;
    }

    /// 계약: 지금 브랜치가 작업 브랜치가 아니면 건너뛴다(true) — 병합·커밋·올리기가 엉뚱한 브랜치에 들어가지 않게 한다
    // 왜: 작업 브랜치가 그 저장소에(로컬에도 원격에도) 아예 없으면 막지 않는다 — 공통 값을 안 쓰는 저장소는 지금 브랜치로 일하는 것이다
    private async Task<bool> NotOnWorkBranchAsync(RepoEntry repo, BulkOperationOptions options, string current, BulkStepResult step, CancellationToken ct)
    {
        // 계약: 사용자가 「이 브랜치 그대로」라고 확인한 저장소는 막지 않는다 — 확인한 뒤 브랜치가 바뀌었으면 다시 막는다
        if (options.ConfirmedBranches.TryGetValue(repo.Id, out var confirmed) && string.Equals(confirmed, current, StringComparison.Ordinal))
        {
            return false;
        }

        var work = options.WorkBranchFor(repo);
        if (!await IsOffWorkBranchAsync(repo, options, current, ct).ConfigureAwait(false))
        {
            return false;
        }

        step.Outcome = StepOutcome.Skipped;
        step.Message = $"지금 브랜치가 {current} 입니다 — 작업 브랜치({work})가 아니라 건너뛰었습니다.";
        step.NextAction = BulkNextAction.Switch;
        return true;
    }

    public async Task<(string Current, string Work)?> ReadOffWorkBranchAsync(RepoEntry repo, BulkOperationOptions options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repo.Path))
        {
            return null;
        }

        var current = await CurrentBranchAsync(repo, cancellationToken).ConfigureAwait(false);
        return current.Length > 0 && await IsOffWorkBranchAsync(repo, options, current, cancellationToken).ConfigureAwait(false)
            ? (current, options.WorkBranchFor(repo))
            : null;
    }

    private async Task<bool> IsOffWorkBranchAsync(RepoEntry repo, BulkOperationOptions options, string current, CancellationToken ct)
    {
        var work = options.WorkBranchFor(repo);
        if (work.Length == 0 || string.Equals(work, current, StringComparison.Ordinal))
        {
            return false;
        }

        var exists = await _runner.RunAsync(repo.Path,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{work}"], ct, 20).ConfigureAwait(false);
        if (!exists.Ok)
        {
            // 함정: 원격에만 있는 작업 브랜치도 「있는 것」이다 — 안 보면 main 에 커밋하고 그대로 올린다(「전환」이 추적 브랜치를 만든다)
            var remote = await _runner.RunAsync(repo.Path,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{options.RemoteName}/{work}"], ct, 20).ConfigureAwait(false);
            if (!remote.Ok)
            {
                return false;
            }
        }

        return true;
    }

    private async Task UpdateDefaultBranchAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var defaultBranch = await UploadBranchAsync(repo, options, ct).ConfigureAwait(false);
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
            step.Message = changed
                ? $"업로드 브랜치 {options.RemoteName}/{defaultBranch} 를 받아 로컬 {defaultBranch} 를 갱신했습니다."
                : $"업로드 브랜치 {defaultBranch} 는 이미 {options.RemoteName}/{defaultBranch} 와 같습니다.";
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = result.Text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            ? $"{defaultBranch} 가 갈라져 빨리감기로 갱신할 수 없습니다."
            : result.Text.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
                ? $"{options.RemoteName} 에 {defaultBranch} 브랜치가 없습니다 — 이 저장소의 업로드 브랜치를 따로 고르거나 비우면 기본 브랜치를 씁니다."
                : DescribeFailure(result.Text);
        step.Detail = result.Text;
    }

    private async Task MergeDefaultIntoCurrentAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        // 계약: 병합해 올 브랜치를 골랐으면 그것을, 안 골랐으면 이 저장소의 업로드 브랜치를 가져온다
        var picked = options.MergeSource.Trim();
        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        var defaultBranch = options.MergeOwnRemote ? (current.Length > 0 ? current : null)
            : picked.Length > 0 ? picked : await UploadBranchAsync(repo, options, ct).ConfigureAwait(false);
        if (defaultBranch is null)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = options.MergeOwnRemote ? "브랜치가 분리돼 있어 병합하지 않았습니다." : "기본 브랜치를 찾지 못했습니다.";
            return;
        }

        // 계약: 지금 브랜치가 가져올 브랜치와 같으면 원격의 같은 브랜치를 합친다 — 올리기가 거부된(갈라진) 저장소를 이 단추로 푼다
        var sameBranch = current == defaultBranch;

        if (await NotOnWorkBranchAsync(repo, options, current, step, ct).ConfigureAwait(false))
        {
            return;
        }

        // 왜: 단추 하나로 원격의 최신을 받아 합친다 — 받기가 실패해도(원격에 없는 로컬 브랜치) 이미 아는 ref 로 병합을 이어 간다
        await _runner.RunAsync(repo.Path,
            ["fetch", options.RemoteName, defaultBranch], ct, options.TimeoutSeconds).ConfigureAwait(false);

        string? reference;
        if (sameBranch)
        {
            // 함정: 같은 이름의 로컬 브랜치는 자기 자신이라 합칠 것이 없다 — 원격 추적 ref 만 본다
            var remoteRef = $"{options.RemoteName}/{defaultBranch}";
            var known = await _runner.RunAsync(repo.Path,
                ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remoteRef}"], ct, 20).ConfigureAwait(false);
            reference = known.Ok ? remoteRef : null;
        }
        else
        {
            reference = await ResolveDefaultRefAsync(repo, defaultBranch, ct).ConfigureAwait(false);
        }

        if (reference is null)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = sameBranch
                ? $"지금이 {defaultBranch} 이고 {options.RemoteName}/{defaultBranch} 가 없습니다."
                : $"{defaultBranch} 브랜치가 이 저장소에 없어 병합하지 않았습니다.";
            return;
        }

        // 왜: git 은 겹치면 영어 원문으로 거부한다 — 병합을 걸기 전에 겹친 파일을 찾아 무엇을 먼저 할지 우리말로 알린다
        var overlap = await OverlapWithIncomingAsync(repo, reference, ct).ConfigureAwait(false);
        if (overlap.Count > 0)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = OverlapMessage(overlap.Count, reference);
            step.Detail = string.Join(Environment.NewLine, overlap);
            return;
        }

        var result = await _runner.RunAsync(repo.Path,
            ["merge", "--no-edit", reference], ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var already = result.StdOut.Contains("Already up to date", StringComparison.OrdinalIgnoreCase);
            step.Outcome = already ? StepOutcome.Skipped : StepOutcome.Success;
            var label = picked.Length == 0 && !options.MergeOwnRemote ? "업로드 브랜치 " : string.Empty;
            step.Message = already ? $"{label}{reference} 와 이미 같습니다." : $"{label}{reference} 를 {current} 로 병합했습니다.";
            step.Detail = result.Text;
            return;
        }

        var conflicts = await ReadConflictsAsync(repo, ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            step.Outcome = StepOutcome.Conflict;
            step.Message = $"충돌 {conflicts.Count}건 — 병합이 멈춰 있습니다.";
            step.NextAction = BulkNextAction.Conflict;
            step.ConflictPaths.AddRange(conflicts.Select(c => c.Path));
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = DescribeFailure(result.Text);
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

        // 왜: 갓 만든 작업 브랜치는 upstream 이 없다 — 일괄 받기에서 그 저장소마다 「실패」로 세지 않는다
        var upstream = await _runner.RunAsync(repo.Path,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct, 20).ConfigureAwait(false);
        var current = upstream.Ok ? string.Empty : await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(current))
        {
            var known = await _runner.RunAsync(repo.Path,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{options.RemoteName}/{current}"], ct, 20).ConfigureAwait(false);
            if (!known.Ok)
            {
                step.Outcome = StepOutcome.Skipped;
                step.Message = $"{options.RemoteName} 에 {current} 브랜치가 아직 없어 받을 것이 없습니다 — 올리면 생깁니다.";
                return;
            }

            // 계약: upstream 은 없지만 원격에 같은 이름이 있으면 그 브랜치를 받는다 — upstream 설정은 바꾸지 않는다
            args.AddRange([options.RemoteName, current]);
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
            var source = upstream.Ok ? upstream.StdOut.Trim() : $"{options.RemoteName}/{current}";
            step.Message = already ? $"{source} 와 이미 같습니다." : $"{source} 를 받았습니다.";
            step.Detail = string.IsNullOrEmpty(step.Detail) ? result.Text : step.Detail;
            return;
        }

        var conflicts = await ReadConflictsAsync(repo, ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            step.Outcome = StepOutcome.Conflict;
            step.Message = $"충돌 {conflicts.Count}건";
            step.NextAction = BulkNextAction.Conflict;
            step.ConflictPaths.AddRange(conflicts.Select(c => c.Path));
        }
        else
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = result.Text.Contains("no tracking information", StringComparison.OrdinalIgnoreCase)
                ? $"{options.RemoteName} 에 이 브랜치를 올린 적이 없어 받을 곳이 없습니다."
                : DescribeFailure(result.Text);
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
                failed.Add($"{branch.Name}: {DescribeFailure(fetch.Text)}");
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

        // 함정: add -A 는 충돌 표식(<<<<<<<)이 든 파일도 「풀렸다」고 스테이지한다 — 풀지 않은 충돌이 있으면 커밋하지 않는다(2026-10-11 실측: 표식째 main 에 올라갔다)
        var unresolved = await ReadConflictsAsync(repo, ct).ConfigureAwait(false);
        if (unresolved.Count > 0)
        {
            step.Outcome = StepOutcome.Conflict;
            step.Message = $"충돌 {unresolved.Count}건이 남아 있어 커밋하지 않았습니다 — 「충돌」 탭에서 풀어 주십시오.";
            step.NextAction = BulkNextAction.Conflict;
            step.ConflictPaths.AddRange(unresolved.Select(c => c.Path));
            return;
        }

        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (await NotOnWorkBranchAsync(repo, options, current, step, ct).ConfigureAwait(false))
        {
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

    /// 계약: 올리기는 두 벌이다 — ① 지금(작업) 브랜치를 제 원격 브랜치로 ② 업로드 브랜치가 다르면 거기에도.
    /// 왜: ②만 하면 작업 브랜치가 원격에 영영 안 올라가 줄의 「올릴 것 ↑N」이 사라지지 않는다.
    /// 계약: force 는 어디에도 쓰지 않는다 — 거부되면 「먼저 받아서 합쳐 주십시오」로 끝낸다.
    private async Task PushAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var branch = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branch))
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "브랜치가 분리돼 있어 올리지 않았습니다.";
            return;
        }

        if (await NotOnWorkBranchAsync(repo, options, branch, step, ct).ConfigureAwait(false))
        {
            return;
        }

        var parts = new List<PushPart> { await PushCurrentAsync(repo, options, branch, ct).ConfigureAwait(false) };

        var upload = options.UploadBranchFor(repo);
        if (upload.Length > 0 && !string.Equals(upload, branch, StringComparison.Ordinal))
        {
            parts.Add(await PushUploadAsync(repo, options, branch, upload, ct).ConfigureAwait(false));
        }

        // 계약: 한쪽이 실패하면 전체가 실패다 — 하나라도 올라갔으면 성공, 둘 다 할 일이 없었으면 건너뜀이다
        step.Outcome = parts.Any(p => p.Outcome == StepOutcome.Failed) ? StepOutcome.Failed
            : parts.Any(p => p.Outcome == StepOutcome.Success) ? StepOutcome.Success
            : StepOutcome.Skipped;
        step.Message = string.Join(" · ", parts.Select(p => p.Message));
        step.Detail = string.Join(Environment.NewLine, parts.Select(p => p.Detail).Where(d => d.Length > 0));
        step.NextAction = parts.Select(p => p.NextAction).FirstOrDefault(a => a.Length > 0) ?? string.Empty;
    }

    /// <summary>올리기 한 벌의 결과 — 작업 브랜치와 업로드 브랜치가 각각 하나씩 낸다.</summary>
    private readonly record struct PushPart(StepOutcome Outcome, string Message, string Detail, string NextAction);

    // 계약: upstream 이 있으면 그 브랜치로, 없으면 설정에 따라 -u 로 만든다 — 업로드 브랜치와 무관하게 늘 돈다
    private async Task<PushPart> PushCurrentAsync(RepoEntry repo, BulkOperationOptions options, string branch, CancellationToken ct)
    {
        var upstream = await _runner.RunAsync(repo.Path,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct, 20).ConfigureAwait(false);

        List<string> args = upstream.Ok
            ? ["push", options.RemoteName, $"HEAD:{branch}"]
            : options.SetUpstreamOnPush
                ? ["push", "-u", options.RemoteName, branch]
                : ["push", options.RemoteName, branch];

        return await RunPushAsync(repo, options, args, branch, $"{branch} 를 올렸습니다.", ct).ConfigureAwait(false);
    }

    // 왜: 원격에 없는 이름으로 올리면 새 원격 브랜치가 조용히 생긴다 — master 저장소에 origin/main 이 만들어졌다
    // 계약: 판정은 로컬이 아는 ref 로 한다 — 원격에 있어도 Fetch 전이면 건너뛴다
    private async Task<PushPart> PushUploadAsync(RepoEntry repo, BulkOperationOptions options, string branch, string upload, CancellationToken ct)
    {
        var known = await _runner.RunAsync(repo.Path,
            ["show-ref", "--verify", "--quiet", $"refs/remotes/{options.RemoteName}/{upload}"], ct, 20).ConfigureAwait(false);
        if (!known.Ok)
        {
            return new PushPart(StepOutcome.Skipped,
                $"{options.RemoteName} 에 {upload} 브랜치가 없어 올리지 않았습니다 — 이 저장소의 업로드 브랜치를 따로 고르거나 Fetch 뒤 다시 해 주십시오.",
                string.Empty, string.Empty);
        }

        List<string> args = ["push", options.RemoteName, $"HEAD:refs/heads/{upload}"];
        var pushed = await RunPushAsync(repo, options, args, upload,
            $"{branch} 를 {options.RemoteName}/{upload} 로 올렸습니다.", ct).ConfigureAwait(false);
        return pushed.Outcome == StepOutcome.Failed
            ? pushed
            : await SyncLocalUploadAsync(repo, options, upload, pushed, ct).ConfigureAwait(false);
    }

    // 왜: 원격 업로드 브랜치만 올리면 로컬의 같은 이름 브랜치가 남아 줄에 「뒤처짐 N」으로 보인다
    // 계약: 원격에 닿지 않는다(`fetch .`) — 빨리감기만 하고, 로컬 브랜치가 없으면 만들지 않으며 갈라졌으면 git 이 거부해 그대로 남는다
    private async Task<PushPart> SyncLocalUploadAsync(
        RepoEntry repo, BulkOperationOptions options, string upload, PushPart pushed, CancellationToken ct)
    {
        var local = await _runner.RunAsync(repo.Path,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{upload}"], ct, 20).ConfigureAwait(false);
        if (!local.Ok)
        {
            return pushed;
        }

        var sync = await _runner.RunAsync(repo.Path,
            ["fetch", ".", $"refs/remotes/{options.RemoteName}/{upload}:refs/heads/{upload}"], ct, 20).ConfigureAwait(false);
        if (!sync.Ok)
        {
            return pushed with
            {
                Message = $"{pushed.Message} · 로컬 {upload} 는 빨리감기가 안 돼 그대로 두었습니다.",
                Detail = string.Join(Environment.NewLine, new[] { pushed.Detail, sync.Text }.Where(t => t.Length > 0)),
            };
        }

        if (sync.Text.Trim().Length == 0)
        {
            return pushed;
        }

        return pushed with
        {
            Outcome = StepOutcome.Success,
            Message = pushed.Outcome == StepOutcome.Success
                ? $"{pushed.Message} · 로컬 {upload} 도 맞췄습니다."
                : $"로컬 {upload} 를 {options.RemoteName}/{upload} 에 맞췄습니다.",
            Detail = string.Join(Environment.NewLine, new[] { pushed.Detail, sync.Text }.Where(t => t.Length > 0)),
        };
    }

    private async Task<PushPart> RunPushAsync(
        RepoEntry repo, BulkOperationOptions options, IReadOnlyList<string> args, string target, string doneMessage, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path, args, ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var already = result.Text.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase);
            return new PushPart(
                already ? StepOutcome.Skipped : StepOutcome.Success,
                already ? $"{target} 에 올릴 것이 없습니다." : doneMessage,
                result.Text, string.Empty);
        }

        // 왜: 원격이 없는 로컬 전용 저장소는 실패가 아니다 — 일괄 올리기의 「실패 1」로 세지 않는다
        if (result.Text.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return new PushPart(StepOutcome.Skipped, $"{options.RemoteName} 저장소가 연결돼 있지 않아 올리지 않았습니다.", result.Text, string.Empty);
        }

        // 계약: 거부당하면 force 를 권하지 않는다 — 먼저 받아서 합치라고 적는다
        var rejected = result.Text.Contains("rejected", StringComparison.OrdinalIgnoreCase);
        return new PushPart(
            StepOutcome.Failed,
            rejected ? $"{options.RemoteName}/{target} 이 앞서 있어 {target} 올리기가 거부됐습니다 — 먼저 받아서 합쳐 주십시오." : FirstLine(result.Text),
            result.Text,
            rejected ? BulkNextAction.Merge : string.Empty);
    }

    /// 계약: 있는 브랜치로만 옮긴다 — 로컬에 없으면 원격의 같은 이름을 추적하는 브랜치를 만들고, 둘 다 없으면 건너뛴다
    // 계약: 강제 옵션을 쓰지 않는다 — 덮어쓸 변경이 있거나 병합 중이면 git 이 거부하고 작업 트리는 그대로 남는다
    private async Task SwitchToWorkBranchAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var work = options.WorkBranchFor(repo);
        if (work.Length == 0)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = "작업 브랜치를 정하지 않았습니다.";
            return;
        }

        var current = await CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        if (string.Equals(current, work, StringComparison.Ordinal))
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = $"이미 {work} 입니다.";
            return;
        }

        var local = await _runner.RunAsync(repo.Path,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{work}"], ct, 20).ConfigureAwait(false);
        var remote = local.Ok
            ? local
            : await _runner.RunAsync(repo.Path,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{options.RemoteName}/{work}"], ct, 20).ConfigureAwait(false);

        if (!local.Ok && !remote.Ok)
        {
            step.Outcome = StepOutcome.Skipped;
            step.Message = $"{work} 브랜치가 이 저장소에 없어 그대로 두었습니다.";
            return;
        }

        var result = await _runner.RunAsync(repo.Path,
            local.Ok ? ["switch", work] : ["switch", "--track", "-c", work, $"{options.RemoteName}/{work}"],
            ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            step.Outcome = StepOutcome.Success;
            step.Message = $"{current} → {work} 로 옮겼습니다.";
            step.Detail = result.Text;
            return;
        }

        step.Outcome = StepOutcome.Failed;
        step.Message = result.Text.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
            ? $"변경한 파일이 {work} 와 겹쳐 옮기지 못했습니다 — 먼저 커밋해 주십시오."
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
        var name = result.Ok ? result.StdOut.Trim() : string.Empty;
        // 함정: 분리된 HEAD 는 이름이 "HEAD" 로 나온다 — 브랜치 이름으로 쓰면 push HEAD:HEAD 가 돼 거부된다
        return name == "HEAD" ? string.Empty : name;
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

    public async Task<RepoAudit> ReadAuditAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        // 계약: 한 번에 읽는다 — 줄은 「키 값」이고 같은 키가 여러 층(전역·저장소)에 있으면 뒤에 온 것이 이긴다
        var config = await _runner.RunAsync(repo.Path,
            ["config", "--get-regexp", @"^(user\.name|user\.email|core\.autocrlf|remote\.origin\.url)$"], cancellationToken, 20).ConfigureAwait(false);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in config.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = line.IndexOf(' ');
            values[cut < 0 ? line.Trim() : line[..cut]] = cut < 0 ? string.Empty : line[(cut + 1)..].Trim();
        }

        var gitDir = await ResolveGitDirAsync(repo, cancellationToken).ConfigureAwait(false);
        var exclude = gitDir is null ? null : Path.Combine(gitDir, "info", "exclude");
        var fetchHead = gitDir is null ? null : Path.Combine(gitDir, "FETCH_HEAD");

        return new RepoAudit
        {
            UserName = values.GetValueOrDefault("user.name", string.Empty),
            UserEmail = values.GetValueOrDefault("user.email", string.Empty),
            RemoteUrl = values.GetValueOrDefault("remote.origin.url"),
            AutoCrlf = values.GetValueOrDefault("core.autocrlf", string.Empty),
            HasGitignore = File.Exists(Path.Combine(repo.Path, ".gitignore")),
            ExcludeLines = exclude is not null && File.Exists(exclude)
                ? File.ReadLines(exclude).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
                : 0,
            LastFetch = fetchHead is not null && File.Exists(fetchHead) ? File.GetLastWriteTime(fetchHead) : null
        };
    }

    public async Task<string?> WriteAuthorAsync(RepoEntry repo, string name, string email, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in new[] { ("user.name", name), ("user.email", email) })
        {
            var result = await _runner.RunAsync(repo.Path, ["config", "--local", key, value], cancellationToken, 20).ConfigureAwait(false);
            if (!result.Ok)
            {
                return FirstLine(result.Text);
            }
        }

        return null;
    }

    // 왜: 작업 트리(worktree)는 info/exclude 가 공통 .git 에 있다 — 경로를 git 에 물어야 맞는 파일을 잡는다
    private async Task<string?> ExcludePathAsync(RepoEntry repo, CancellationToken ct)
    {
        var result = await _runner.RunAsync(repo.Path, ["rev-parse", "--git-path", "info/exclude"], ct, 20).ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Length > 0 ? Path.GetFullPath(Path.Combine(repo.Path, result.StdOut.Trim())) : null;
    }

    private static bool IsExcludeRule(string line) => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#');

    public async Task<IReadOnlyList<string>> ReadExcludeAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var path = await ExcludePathAsync(repo, cancellationToken).ConfigureAwait(false);
        return path is not null && File.Exists(path)
            ? [.. (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).Where(IsExcludeRule).Select(l => l.Trim())]
            : [];
    }

    public async Task<string?> AppendExcludeAsync(RepoEntry repo, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var path = await ExcludePathAsync(repo, cancellationToken).ConfigureAwait(false);
        return path is null
            ? "exclude 파일의 자리를 찾지 못했습니다."
            : await AppendRulesAsync(path, lines, cancellationToken).ConfigureAwait(false);
    }

    // 계약: 작업 트리 맨 위의 .gitignore 하나만 다룬다 — 하위 폴더의 .gitignore 는 읽지도 쓰지도 않는다
    private static string GitignorePath(RepoEntry repo) => Path.Combine(repo.Path, ".gitignore");

    public async Task<IReadOnlyList<string>> ReadGitignoreAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var path = GitignorePath(repo);
        return File.Exists(path)
            ? [.. (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).Where(IsExcludeRule).Select(l => l.Trim())]
            : [];
    }

    public Task<string?> AppendGitignoreAsync(RepoEntry repo, IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        AppendRulesAsync(GitignorePath(repo), lines, cancellationToken);

    private static async Task<string?> AppendRulesAsync(string path, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        try
        {
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
            var have = existing.Split('\n').Where(IsExcludeRule).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
            var missing = lines.Select(l => l.Trim()).Where(l => IsExcludeRule(l) && have.Add(l)).ToList();
            if (missing.Count == 0)
            {
                return null;
            }

            // 왜: CRLF 로 적힌 파일에 LF 를 섞으면 .gitignore 가 줄바꿈 혼합으로 통째 변경처럼 보인다 — 있던 줄바꿈을 따른다
            var eol = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            // 함정: 마지막 줄이 줄바꿈 없이 끝나 있으면 그대로 붙였을 때 앞 규칙과 한 줄로 합쳐진다
            var lead = existing.Length > 0 && !existing.EndsWith('\n') ? eol : string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, lead + string.Join(eol, missing) + eol, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
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

    // 계약: Fetch·Pull·Push 가 원격에 닿는 종류인지 — 원격 없는 저장소는 이 종류만 건너뛴다 (CommitAndPush 는 커밋은 해야 하므로 Push 쪽 판정에 맡긴다)
    private static bool NeedsRemote(BulkOperationKind kind) => kind is BulkOperationKind.Fetch
        or BulkOperationKind.UpdateDefaultBranch
        or BulkOperationKind.MergeDefaultIntoCurrent
        or BulkOperationKind.PullCurrent
        or BulkOperationKind.UpdateAllTrackedBranches
        or BulkOperationKind.Push;

    /// 계약: 커밋하지 않은 변경(추적 파일 + 추적 전 파일) 가운데 reference 에서 들어올 파일과 겹치는 것만 돌려준다 — 읽기만 한다
    // 함정: 이미 병합 중이면 묻지 않는다 — HEAD 와 견준 변경이 전부 병합분이라 통째로 겹친 것으로 보인다
    private async Task<IReadOnlyList<string>> OverlapWithIncomingAsync(RepoEntry repo, string reference, CancellationToken ct)
    {
        var gitDir = await ResolveGitDirAsync(repo, ct).ConfigureAwait(false);
        if (DetectOperation(gitDir) != GitOperationState.None)
        {
            return [];
        }

        // 계약: 세 점(HEAD...ref)은 갈라진 뒤 상대 쪽이 바꾼 파일이다 — 병합이 실제로 덮어쓸 파일과 같다
        var incoming = await _runner.RunAsync(repo.Path,
            ["-c", "core.quotepath=false", "diff", "--name-only", $"HEAD...{reference}"], ct, 60).ConfigureAwait(false);
        if (!incoming.Ok)
        {
            return [];
        }

        var theirs = Lines(incoming.StdOut).ToHashSet(StringComparer.Ordinal);
        if (theirs.Count == 0)
        {
            return [];
        }

        var tracked = await _runner.RunAsync(repo.Path,
            ["-c", "core.quotepath=false", "diff", "--name-only", "HEAD"], ct, 60).ConfigureAwait(false);
        var untracked = await _runner.RunAsync(repo.Path,
            ["-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard"], ct, 60).ConfigureAwait(false);

        return Lines(tracked.StdOut).Concat(Lines(untracked.StdOut))
            .Where(theirs.Contains).Distinct(StringComparer.Ordinal).ToList();

        static IEnumerable<string> Lines(string text) =>
            text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
    }

    private static string OverlapMessage(int count, string reference) =>
        $"커밋하지 않은 변경 {count}개가 {reference} 에서 들어올 파일과 겹쳐 병합하지 않았습니다 — 먼저 커밋한 뒤 다시 Merge 해 주십시오.";

    // 계약: 사용자가 자주 만나는 git 원문 넷만 우리말로 바꾼다 — 원문은 Detail 이 갖는다
    private static string DescribeFailure(string text)
    {
        // 왜: 사전 점검이 못 거른 겹침(Pull · 병합 중 판정 실패)도 같은 말로 알린다
        if (text.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
        {
            return "커밋하지 않은 변경이 들어올 파일과 겹쳐 진행하지 않았습니다 — 먼저 커밋한 뒤 다시 해 주십시오. 겹친 파일은 「자세히」에 있습니다.";
        }

        if (text.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return "원격 저장소가 등록돼 있지 않거나 주소가 틀렸습니다 — git remote -v 로 주소를 확인해 주십시오.";
        }

        if (text.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Could not read from remote repository", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return "원격 저장소에 접속하지 못했습니다 — 네트워크 연결과 GitHub 로그인(자격 증명)을 확인해 주십시오.";
        }

        return FirstLine(text);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
