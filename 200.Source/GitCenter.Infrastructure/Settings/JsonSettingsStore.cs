using GitCenter.Application.Abstractions;
using GitCenter.Application.Settings;
using CoreStore = Jhj.Core.Settings.JsonSettingsStore<GitCenter.Application.Settings.AppSettings>;

namespace GitCenter.Infrastructure.Settings;

/// <summary>
/// 설정 한 벌을 %LOCALAPPDATA%\JHJ_Git-Center\settings.json 에 둔다.
/// 계약: 형식·원자적 쓰기·깨짐 복구는 JHJ_CS_CORE 가 한다 — 여기는 **이 앱의 기본값**만 더한다.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly CoreStore _store = new(WithDefaults);

    public string SettingsPath => _store.Path;

    /// <summary>로그 등 다른 파일이 함께 쓰는 자료 폴더.</summary>
    public static string DataRoot => Jhj.Core.App.AppPaths.Root;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => _store.LoadAsync(cancellationToken);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => _store.SaveAsync(settings, cancellationToken);

    /// 계약: 첫 실행이면 이 PC 에서 가장 그럴듯한 루트를 하나 넣어 둔다 — 빈 목록으로 열면 쓸 수가 없다
    private static AppSettings WithDefaults(AppSettings settings)
    {
        // 왜: 자리표가 Ctrl+Alt+G 에서 3번으로 옮겨 갔다(060.단축키-대장, 2026-10-10) — 그 값을 쥔 PC 만 새 기본값으로 옮긴다
        if (settings.Hotkey is "Ctrl+Alt+G")
        {
            settings.Hotkey = AppSettings.DefaultHotkey;
        }

        // 왜: 「chore: 일괄 반영」 한 줄로는 무엇을 왜 올렸는지 안 남는다 — 옛 기본값을 쥔 PC 만 새 기본값으로 옮긴다
        if (settings.CommitMessageTemplate is AppSettings.LegacyCommitSubject)
        {
            settings.CommitMessageTemplate = AppSettings.DefaultCommitSubject;
        }

        // 왜: 「받는·올리는 브랜치」가 「작업·업로드 브랜치」로 바뀌었다 — 받는 칸에 일하는 브랜치를 적어 쓰던 PC 의 값을 한 번 옮긴다
        if (settings.ReceiveBranch is not null || settings.PushBranch is not null)
        {
            var receive = settings.ReceiveBranch?.Trim() ?? string.Empty;
            var push = settings.PushBranch?.Trim() ?? string.Empty;
            if (receive.Length > 0 && !string.Equals(receive, push, StringComparison.Ordinal))
            {
                settings.WorkBranch = receive;
            }

            if (settings.PushBranch is not null)
            {
                settings.UploadBranch = push;
            }

            settings.ReceiveBranch = null;
            settings.PushBranch = null;
        }

        // 함정: 역직렬화가 사전을 새로 만들어 대소문자 무시 비교가 사라진다 — 저장소 Id 는 경로라 다시 씌운다
        settings.RepoBranches = new Dictionary<string, RepoBranchRule>(settings.RepoBranches, StringComparer.OrdinalIgnoreCase);

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
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
