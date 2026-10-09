using GitCenter.Application.Abstractions;
using GitCenter.Core.Models;

namespace GitCenter.Application.Services;

/// <summary>
/// 고른 저장소에 같은 작업을 돌린다. 저장소 사이는 동시에, 한 저장소 안은 차례대로다.
/// 계약: 한 저장소가 실패해도 나머지는 계속 돈다 — 결과는 저장소마다 한 줄로 남는다.
/// 함정: 동시 실행 수를 올리면 원격이 같은 호스트일 때 인증·대역이 몰린다 — 기본 4 다.
/// </summary>
public sealed class BulkOperationRunner(IGitService git)
{
    private readonly IGitService _git = git;

    public async Task<IReadOnlyList<BulkStepResult>> RunAsync(
        IReadOnlyList<RepoEntry> repos,
        BulkOperationKind kind,
        BulkOperationOptions options,
        int concurrency,
        IProgress<BulkStepResult>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<BulkStepResult>();
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 16));
        var sync = new object();

        var tasks = repos.Select(async repo =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var step = Guard(repo, kind) ?? await _git
                    .ExecuteAsync(repo, kind, options, cancellationToken)
                    .ConfigureAwait(false);

                lock (sync)
                {
                    results.Add(step);
                }

                progress?.Report(step);
            }
            catch (OperationCanceledException)
            {
                var step = new BulkStepResult
                {
                    RepoId = repo.Id,
                    RepoName = repo.Name,
                    Kind = kind,
                    Outcome = StepOutcome.Canceled,
                    Message = "멈췄습니다."
                };

                lock (sync)
                {
                    results.Add(step);
                }

                progress?.Report(step);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// 계약: 돌릴 수 없는 저장소는 git 을 부르지 않고 바로 건너뛴다
    private static BulkStepResult? Guard(RepoEntry repo, BulkOperationKind kind)
    {
        if (repo.Health == RepoHealth.Ok)
        {
            return null;
        }

        var reason = repo.Health switch
        {
            RepoHealth.NotCloned => "아직 clone 하지 않았습니다.",
            RepoHealth.PathMissing => "경로가 없습니다.",
            RepoHealth.NotGitRepo => "git 저장소가 아닙니다.",
            RepoHealth.NoRemote => "원격이 없습니다.",
            _ => "읽을 수 없습니다."
        };

        return new BulkStepResult
        {
            RepoId = repo.Id,
            RepoName = repo.Name,
            Kind = kind,
            Outcome = StepOutcome.Skipped,
            Message = reason
        };
    }
}
