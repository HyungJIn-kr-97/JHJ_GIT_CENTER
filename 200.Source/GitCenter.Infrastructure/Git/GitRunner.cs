using GitCenter.Application.Abstractions;
using GitCenter.Core.Models;

namespace GitCenter.Infrastructure.Git;

/// <summary>git 실행기. 환경을 고정해 자격 증명 창이 뜨지 않게 한다.</summary>
/// 함정: GIT_TERMINAL_PROMPT 를 켜 두면 인증이 필요한 원격에서 창이 떠 앱이 통째로 멈춘다.
public sealed class GitRunner : IGitRunner
{
    private static readonly Dictionary<string, string> FixedEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_PAGER"] = "cat",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["LC_ALL"] = "C"
    };

    private readonly Lazy<string?> _version = new(ProbeVersion);

    public bool IsGitAvailable => _version.Value is not null;

    public string? GitVersion => _version.Value;

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        // 계약: 한글 경로가 따옴표로 감싸져 나오지 않게 — porcelain 파싱이 그 따옴표에 걸린다
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);

        var result = await ProcessRunner.RunAsync(
            "git",
            full,
            workingDirectory,
            timeoutSeconds ?? 120,
            FixedEnvironment,
            cancellationToken).ConfigureAwait(false);

        return new GitResult
        {
            CommandLine = "git " + string.Join(' ', args),
            WorkingDirectory = workingDirectory,
            ExitCode = result.TimedOut ? -2 : result.ExitCode,
            StdOut = result.StdOut,
            StdErr = result.TimedOut ? $"제한 시간({timeoutSeconds ?? 120}초)을 넘겨 중단했습니다.\n{result.StdErr}" : result.StdErr,
            Elapsed = result.Elapsed
        };
    }

    private static string? ProbeVersion()
    {
        try
        {
            var result = ProcessRunner
                .RunAsync("git", ["--version"], null, 15, FixedEnvironment, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return result.ExitCode == 0 ? result.StdOut.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
