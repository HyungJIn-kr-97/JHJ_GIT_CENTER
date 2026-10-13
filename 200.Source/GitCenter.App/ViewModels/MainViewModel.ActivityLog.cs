using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 작업 기록 두 갈래 — ① 앱이 한 일(Activity)은 data\activity.jsonl 에 남겨 다음 실행에 되살린다.
// ② 대시보드 「최근 작업」(RecentWork)은 저장소마다 git reflog 에서 읽는다 — 앱 밖(CLI · 다른 도구)에서 한 일도 보인다.
// 계약: ①은 PushActivity 가 넣는 것만 적는다 — 되살린 줄은 다시 적지 않는다(파일이 두 배로 늘지 않게).
// 계약: ②는 HEAD · refs/remotes 의 push · refs/stash · 브랜치 생성만 고른다 — refs/heads 의 커밋은 HEAD 와 같은 일이라 뺀다.
public sealed partial class MainViewModel
{
    private static readonly string ActivityPath = Path.Combine(Jhj.Core.App.AppPaths.Data, "activity.jsonl");
    private static readonly JsonSerializerOptions ActivityJson = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _activityGate = new(1, 1);

    // 계약: 되살리는 상한 — 화면 상한(TrimActivity 500)과 같다. 파일이 그 두 배를 넘으면 최근 500줄만 남기고 다시 쓴다
    private const int ActivityKeep = 500;

    private sealed record ActivityRecord(DateTimeOffset When, string RepoName, string? RepoId, StepOutcome Outcome, string Message, string Detail);

    /// 계약: 시작 때 한 번 — 파일은 오래된 줄부터라 거꾸로 넣어 Activity 를 새것부터로 맞춘다
    private void LoadActivityLog()
    {
        try
        {
            if (!File.Exists(ActivityPath))
            {
                return;
            }

            var lines = File.ReadAllLines(ActivityPath);
            var tail = lines.Length > ActivityKeep ? lines[^ActivityKeep..] : lines;
            var restored = new List<ActivityEntry>();
            foreach (var line in tail)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<ActivityRecord>(line, ActivityJson) is { } r)
                    {
                        restored.Add(new ActivityEntry
                        {
                            When = r.When, RepoName = r.RepoName, RepoId = r.RepoId, Outcome = r.Outcome, Message = r.Message, Detail = r.Detail
                        });
                    }
                }
                catch (JsonException)
                {
                    // 함정: 깨진 줄 하나로 전부를 버리지 않는다
                }
            }

            for (var i = restored.Count - 1; i >= 0; i--)
            {
                Activity.Add(restored[i]);
            }

            if (restored.Count > 0)
            {
                LastActivity = restored[^1];
            }

            if (lines.Length > ActivityKeep * 2)
            {
                File.WriteAllLines(ActivityPath, tail);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "작업 기록 되읽기 실패");
        }
    }

    private async Task AppendActivityLogAsync(ActivityEntry entry)
    {
        var line = JsonSerializer.Serialize(
            new ActivityRecord(entry.When, entry.RepoName, entry.RepoId, entry.Outcome, entry.Message, entry.Detail), ActivityJson);
        await _activityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Jhj.Core.App.AppPaths.Ensure(Jhj.Core.App.AppPaths.Data);
            await File.AppendAllTextAsync(ActivityPath, line + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "작업 기록 쓰기 실패");
        }
        finally
        {
            _activityGate.Release();
        }
    }

    /// 계약: 대시보드 「최근 작업」 — 모든 저장소의 git 기록(RepoRow.WorkLog)을 새것부터 RecentWorkLimit 건
    public System.Collections.ObjectModel.ObservableCollection<ActivityEntry> RecentWork { get; } = [];

    [ObservableProperty]
    private string _recentWorkCaption = string.Empty;

    // 계약: 저장소마다 reflog 를 읽어 두는 날 수(MainViewModel.RefreshStatusAsync) — 화면은 그 안에서 새것부터 자른다
    private const int WorkDays = 30;

    private const int RecentWorkLimit = 80;

    private void RefreshRecentWork()
    {
        var picked = _all.SelectMany(r => r.WorkLog.Select(e => (Row: r, Entry: e, Text: DescribeWork(e))))
            .Where(x => x.Text is not null)
            .OrderByDescending(x => x.Entry.When).Take(RecentWorkLimit).ToList();

        RecentWork.Clear();
        foreach (var (row, entry, text) in picked)
        {
            RecentWork.Add(new ActivityEntry
            {
                When = entry.When, RepoName = row.Name, RepoId = row.Id, Outcome = StepOutcome.Success, Message = text!, Detail = entry.Sha
            });
        }

        var repos = picked.Select(x => x.Row.Id).Distinct().Count();
        RecentWorkCaption = picked.Count == 0 ? string.Empty : $"최근 {WorkDays}일 · git 기록 · 저장소 {repos}";
    }

    /// 계약: reflog 한 줄을 우리말 한 줄로 — null 이면 보이지 않는 줄이다(브랜치 ref 의 커밋 · fetch · rebase 중간 단계)
    private static string? DescribeWork(ReflogEntry e)
    {
        var subject = e.Subject;
        if (e.Ref.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            return subject.StartsWith("update by push", StringComparison.Ordinal) ? $"Push → {e.Ref["refs/remotes/".Length..]}" : null;
        }

        if (e.Ref.StartsWith("refs/stash", StringComparison.Ordinal))
        {
            return $"Stash — {subject}";
        }

        if (e.Ref.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return subject.StartsWith("branch: Created", StringComparison.Ordinal)
                ? $"브랜치 만듦 {e.Ref["refs/heads/".Length..]} — {subject["branch: ".Length..]}"
                : null;
        }

        if (e.Ref != "HEAD")
        {
            return null;
        }

        var cut = subject.IndexOf(": ", StringComparison.Ordinal);
        var verb = cut < 0 ? subject : subject[..cut];
        var rest = cut < 0 ? string.Empty : subject[(cut + 2)..];
        return verb switch
        {
            "commit" => $"커밋 — {rest}",
            "commit (amend)" => $"커밋 수정 — {rest}",
            "commit (initial)" => $"첫 커밋 — {rest}",
            "commit (merge)" => $"병합 커밋 — {rest}",
            "checkout" when rest.StartsWith("moving from ", StringComparison.Ordinal)
                => $"전환 {rest["moving from ".Length..].Replace(" to ", " → ", StringComparison.Ordinal)}",
            "reset" when rest.StartsWith("moving to ", StringComparison.Ordinal) => $"Reset → {rest["moving to ".Length..]}",
            "clone" => $"클론 — {rest}",
            "cherry-pick" => $"Cherry-pick — {rest}",
            "revert" => $"Revert — {rest}",
            _ when verb.StartsWith("merge ", StringComparison.Ordinal) => $"Merge {verb[6..]} — {rest}",
            _ when verb.StartsWith("pull", StringComparison.Ordinal) => $"Pull{(verb.Length > 5 ? " " + verb[5..] : string.Empty)} — {rest}",
            _ when verb.StartsWith("rebase", StringComparison.Ordinal) => verb.Contains("finish", StringComparison.Ordinal) ? $"Rebase — {rest}" : null,
            _ => subject
        };
    }
}
