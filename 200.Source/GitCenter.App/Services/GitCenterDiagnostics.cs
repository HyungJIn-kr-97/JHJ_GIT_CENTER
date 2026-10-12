using System.Text.RegularExpressions;
using GitCenter.Application.Settings;
using Jhj.Core.Diagnostics;

namespace GitCenter.App.Services;

/// <summary>진단 보고서의 Git Center 구역 — 설정 창 「진단 보고서」와 상태 줄 「오류 보고」가 같은 것을 쓴다.</summary>
/// 계약: 저장소 경로·원격 주소·작성자는 싣지 않는다 — 사용자가 이슈에 그대로 붙이므로 개수와 설정값만 낸다
public static partial class GitCenterDiagnostics
{
    public static DiagnosticsReportBuilder AppSection(this DiagnosticsReportBuilder builder, AppSettings settings, IEnumerable<string> hotkeys) =>
        builder.Section("Git Center", sb =>
        {
            sb.AppendLine($"- 목록 출처: 루트 {settings.ScanRoots.Count} · 수동 {settings.ManualPaths.Count} · 숨김 {settings.ExcludedIds.Count} · gh 계정 {settings.GitHubOwners.Count} · 명령 {settings.CommandSources.Count}");
            sb.AppendLine($"- 연동: GitHub Desktop {settings.GitHubDesktopEnabled} · gh 계정 {settings.GitHubAccountEnabled}");
            sb.AppendLine($"- 주기(분): 다시 훑기 {settings.RescanMinutes} · 상태 {settings.StatusRefreshMinutes} · 자동 Fetch {settings.AutoFetchMinutes}");
            sb.AppendLine($"- git: 동시 실행 {settings.Concurrency} · 제한 시간 {settings.GitTimeoutSeconds}초 · ff-only {settings.FastForwardOnly} · stash {settings.AutoStashBeforePull}");
            sb.AppendLine($"- 화면: 명암 {settings.Theme} · 색 {settings.Palette} · 언어 {settings.Language} · 배율 {settings.UiScale}");
            sb.AppendLine($"- 단축키: {string.Join(" · ", hotkeys.Where(h => !string.IsNullOrWhiteSpace(h)))}");
        });

    /// 계약: git 이 낸 말에서 저장소 경로와 원격 주소를 가린다 — 저장소 이름은 남긴다
    public static string MaskRepo(string text, IEnumerable<string> repoPaths)
    {
        var masked = UrlRx().Replace(text, "<url>");
        foreach (var path in repoPaths.Where(p => p.Length > 0).OrderByDescending(p => p.Length))
        {
            masked = masked.Replace(path, "<repo>", StringComparison.OrdinalIgnoreCase)
                .Replace(path.Replace('\\', '/'), "<repo>", StringComparison.OrdinalIgnoreCase);
        }

        // 함정: git 은 경로를 / 로 적는다 — Core 의 Mask 는 \ 꼴의 사용자 폴더만 가린다
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        if (profile.Length > 0)
        {
            masked = masked.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return DiagnosticsReportBuilder.Mask(masked);
    }

    [GeneratedRegex(@"(https?|ssh|git)://\S+|git@[\w.-]+:\S+")]
    private static partial Regex UrlRx();
}
