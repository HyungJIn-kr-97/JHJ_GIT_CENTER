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

    public async Task<IReadOnlyList<FileChange>> ReadChangesAsync(RepoEntry repo, CancellationToken cancellationToken)
    {
        var porcelain = await _runner.RunAsync(repo.Path,
            ["status", "--porcelain=v2"], cancellationToken, 60).ConfigureAwait(false);

        return porcelain.Ok ? PorcelainV2.Parse(porcelain.StdOut).Changes : [];
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
                files.Add(ConflictParser.Parse(relative, content));
            }
            catch (Exception ex)
            {
                files.Add(new ConflictFile { Path = relative, Note = $"읽지 못했습니다: {ex.Message}" });
            }
        }

        return files;
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

    private async Task UpdateDefaultBranchAsync(RepoEntry repo, BulkOperationOptions options, BulkStepResult step, CancellationToken ct)
    {
        var defaultBranch = await ReadDefaultBranchAsync(repo, ct).ConfigureAwait(false);
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
        var defaultBranch = await ReadDefaultBranchAsync(repo, ct).ConfigureAwait(false);
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

        var message = string.IsNullOrWhiteSpace(options.CommitMessage) ? "chore: 일괄 반영" : options.CommitMessage;

        var add = await _runner.RunAsync(repo.Path, ["add", "-A"], ct, options.TimeoutSeconds).ConfigureAwait(false);
        if (!add.Ok)
        {
            step.Outcome = StepOutcome.Failed;
            step.Message = FirstLine(add.Text);
            step.Detail = add.Text;
            return;
        }

        var commit = await _runner.RunAsync(repo.Path, ["commit", "-m", message], ct, options.TimeoutSeconds).ConfigureAwait(false);
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

        var args = upstream.Ok
            ? new List<string> { "push", options.RemoteName, $"HEAD:{branch}" }
            : options.SetUpstreamOnPush
                ? ["push", "-u", options.RemoteName, branch]
                : ["push", options.RemoteName, branch];

        var result = await _runner.RunAsync(repo.Path, args, ct, options.TimeoutSeconds).ConfigureAwait(false);

        if (result.Ok)
        {
            var already = result.Text.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase);
            step.Outcome = already ? StepOutcome.Skipped : StepOutcome.Success;
            step.Message = already ? "올릴 것이 없습니다." : $"{branch} 를 올렸습니다.";
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
