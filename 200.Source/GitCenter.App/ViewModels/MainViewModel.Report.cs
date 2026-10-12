using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Core.Models;
using Jhj.Core.App;
using Jhj.Core.Diagnostics;
using Jhj.Core.Wpf.Shell;

namespace GitCenter.App.ViewModels;

// 「오류 보고」 — 실패한 작업 기록 + 환경·설정 + 이벤트 로그를 한 벌로 복사하고 이슈 양식을 연다(상태 줄 · 시스템 작업 로그 탭).
// 계약: 실패(Failed) 기록이 하나라도 있을 때만 단추가 보인다 — 충돌은 사용자가 푸는 일이라 오류로 치지 않지만 보고서에는 같이 싣는다
public sealed partial class MainViewModel
{
    [ObservableProperty]
    private bool _hasReportableError;

    private void RefreshReportable() => HasReportableError = Activity.Any(a => a.Outcome is StepOutcome.Failed);

    /// <summary>처리되지 않은 예외를 실패 기록으로 남긴다 — Detail 에 스택을 통째로 넣어 「오류 보고」가 그대로 싣는다(App.OfferErrorReport).</summary>
    public void RecordException(string scope, Exception ex) =>
        PushActivity(new ActivityEntry
        {
            When = DateTimeOffset.Now,
            RepoName = scope,
            Outcome = StepOutcome.Failed,
            Message = $"{ex.GetType().Name}: {ex.Message}",
            Detail = ex.ToString()
        });

    [RelayCommand]
    private void ReportError()
    {
        var paths = _all.Select(r => r.Entry.Path).ToList();
        var entries = Activity.Where(a => a.Outcome is StepOutcome.Failed or StepOutcome.Conflict).Take(20).ToList();

        var report = new DiagnosticsReportBuilder()
            .Section($"오류 기록 (최근 {entries.Count}건)", sb =>
            {
                if (entries.Count == 0)
                {
                    sb.AppendLine("- 없음");
                    return;
                }

                foreach (var entry in entries)
                {
                    sb.AppendLine($"- {entry.When:yyyy-MM-dd HH:mm:ss} [{entry.Outcome}] {entry.RepoName} — {GitCenterDiagnostics.MaskRepo(entry.Message, paths)}");
                    if (entry.HasDetail)
                    {
                        sb.AppendLine("  ```text");
                        sb.AppendLine("  " + GitCenterDiagnostics.MaskRepo(entry.Detail.Trim(), paths).Replace("\n", "\n  "));
                        sb.AppendLine("  ```");
                    }
                }
            })
            .AppSection(Settings, new[] { Settings.Hotkey }.Concat(Settings.ExtraHotkeys))
            .EventLog()
            .Build();

        if (!ClipboardText.TryCopy(report))
        {
            AddActivity("오류 보고", StepOutcome.Failed, "클립보드에 복사하지 못했습니다 — 다른 프로그램이 클립보드를 쥐고 있는지 확인해 주십시오.");
            return;
        }

        var opened = AppLinks.NewIssueUrl.Length > 0 && Jhj.Core.Windows.Shell.ShellOpener.OpenUrl(AppLinks.NewIssueUrl);
        AddActivity("오류 보고", StepOutcome.Success, opened
            ? "오류 보고를 복사하고 이슈 양식을 열었습니다 — 본문에 붙여 넣어 주십시오."
            : "오류 보고를 복사했습니다 — 이슈나 메시지에 붙여 넣어 주십시오.");
    }
}
