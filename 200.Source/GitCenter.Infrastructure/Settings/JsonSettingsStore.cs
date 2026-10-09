using System.Text.Json;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Settings;

namespace GitCenter.Infrastructure.Settings;

/// <summary>
/// 설정 한 벌을 %LOCALAPPDATA%\JHJ_Git-Center\settings.json 에 둔다.
/// 함정: 깨진 json 을 만나면 settings.bad.json 으로 옮기고 기본값으로 연다 — 앱이 안 뜨는 것보다 낫다.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string AppFolderName = "JHJ_Git-Center";

    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string SettingsPath => Path.Combine(DataRoot, "settings.json");

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = SettingsPath;
        if (!File.Exists(path))
        {
            return WithDefaults(new AppSettings());
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                .ConfigureAwait(false);
            return WithDefaults(settings ?? new AppSettings());
        }
        catch (JsonException)
        {
            BackupCorrupt(path);
            return WithDefaults(new AppSettings());
        }
        catch (IOException)
        {
            return WithDefaults(new AppSettings());
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataRoot);
            var temporary = SettingsPath + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken).ConfigureAwait(false);
            }

            // 왜: 쓰다가 끊겨도 옛 설정이 남게 — 임시 파일에 다 쓴 뒤 바꿔 끼운다
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// 계약: 첫 실행이면 이 PC 에서 가장 그럴듯한 루트를 하나 넣어 둔다 — 빈 목록으로 열면 쓸 수가 없다
    private static AppSettings WithDefaults(AppSettings settings)
    {
        if (settings.ScanRoots.Count > 0)
        {
            return settings;
        }

        foreach (var candidate in DefaultRootCandidates())
        {
            if (Directory.Exists(candidate))
            {
                settings.ScanRoots.Add(new ScanRoot { Path = candidate, Depth = 3 });
                break;
            }
        }

        return settings;
    }

    private static IEnumerable<string> DefaultRootCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine("C:", "DEV", "Github");
        yield return Path.Combine(home, "Documents", "GitHub");
        yield return Path.Combine(home, "source", "repos");
        yield return Path.Combine(home, "git");
    }

    private static void BackupCorrupt(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                File.Copy(path, Path.Combine(directory, "settings.bad.json"), true);
            }
        }
        catch
        {
            // 계약: 백업 실패가 앱 시작을 막지 않는다
        }
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
