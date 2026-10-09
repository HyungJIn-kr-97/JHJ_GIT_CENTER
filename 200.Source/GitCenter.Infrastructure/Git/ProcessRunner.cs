using System.Diagnostics;
using System.Text;

namespace GitCenter.Infrastructure.Git;

/// <summary>외부 명령 한 번 — 셸을 거치지 않고 인자를 배열로 넘긴다.</summary>
/// 함정: stdout·stderr 를 동기로 읽으면 버퍼가 차서 서로 막힌다 — 비동기 이벤트로 받는다.
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed, bool TimedOut);

    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outDone.TrySetResult();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errDone.TrySetResult();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new Result(-1, string.Empty, ex.Message, stopwatch.Elapsed, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // 함정: 자격 증명을 물으면 stdin 에서 영원히 기다린다 — 입력을 바로 닫아 질문 대신 실패로 끝내게 한다
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // 이미 닫혔으면 그만이다
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 이미 끝났으면 그만이다
            }
        }

        stopwatch.Stop();
        var exitCode = -1;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            // 접근할 수 없으면 -1 로 둔다
        }

        return new Result(exitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, timedOut);
    }
}
