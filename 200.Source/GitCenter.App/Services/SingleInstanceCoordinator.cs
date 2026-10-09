using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GitCenter.App.Services;

/// <summary>
/// 앱을 한 번만 띄운다. 두 번째 실행은 첫 번째에게 「창을 보여라」만 알리고 끝난다.
/// 함정: 뮤텍스만 두면 두 번째 실행이 아무 반응 없이 사라져 사용자가 고장으로 본다 — 파이프로 첫 인스턴스를 깨운다.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;

    public SingleInstanceCoordinator(string name)
    {
        PipeName = name + "-activate";
        _mutex = new Mutex(true, "Local" + Path.DirectorySeparatorChar + name, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public string PipeName { get; }

    public async Task StartListenerAsync(Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(message))
                {
                    await onMessage(message).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 계약: 파이프 한 번 실패가 앱을 세우지 않는다 — 다음 연결을 기다린다
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public static async Task SignalPrimaryAsync(string pipeName, string message, TimeSpan timeout)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeout);
        await client.ConnectAsync(cts.Token).ConfigureAwait(false);

        await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync(message).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            if (IsPrimary)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // 이미 풀렸으면 그만이다
        }

        _mutex.Dispose();
    }
}

public static class ActivationMessage
{
    public const string ShowWindow = "show-window";
}
