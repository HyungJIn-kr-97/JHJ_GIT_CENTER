using System.Collections.ObjectModel;
using GitCenter.Application.Settings;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 상세의 「상태」 탭 — 줄에 보이는 한 단어(「올릴 것」·「변경」·「stash 2」)가 왜 그런지를 한 줄씩 풀어 적고,
// stash 는 목록으로 펼친다. 숫자만 보고는 지워도 되는지 판단할 수 없기 때문이다(2026-10-11 사용자 지시).
// 계약: 읽기뿐이다 — 이 탭은 git 에 쓰지 않는다. stash 는 `stash list`·`stash show --name-only` 두 번만 부른다.

/// <summary>「상태」 탭의 사유 한 줄.</summary>
/// 계약: Tone 은 StateBrushConverter 가 아는 한 단어다 — 「충돌」·「받을 것」·「올릴 것」·「변경」·「깨끗함」
public sealed record StatusReason(string Tone, string Title, string Detail);

public sealed partial class MainViewModel
{
    /// 계약: 고른 저장소의 지금 상태를 풀어 적은 줄들 — LoadDetailAsync 가 채운다
    public ObservableCollection<StatusReason> StatusReasons { get; } = [];

    public ObservableCollection<StashEntry> Stashes { get; } = [];

    private async Task LoadStatusTabAsync(RepoRow row, int generation)
    {
        StatusReasons.Clear();
        Stashes.Clear();

        var stashes = row.Status is { StashCount: > 0 }
            ? await _git.ReadStashesAsync(row.Entry, CancellationToken.None).ConfigureAwait(true)
            : [];

        if (generation != _detailGeneration || !ReferenceEquals(SelectedRow, row))
        {
            return;
        }

        foreach (var stash in stashes)
        {
            Stashes.Add(stash);
        }

        foreach (var reason in BuildReasons(row, stashes.Count))
        {
            StatusReasons.Add(reason);
        }
    }

    /// 계약: 줄에 보이는 상태 글자와 같은 근거로 만든다 — 화면 두 곳이 다른 말을 하지 않게
    private IEnumerable<StatusReason> BuildReasons(RepoRow row, int stashCount)
    {
        if (row.Entry.Health != RepoHealth.Ok)
        {
            yield return new StatusReason("충돌", row.HealthText, "이 저장소는 일괄 작업에서 건너뜁니다.");
            yield break;
        }

        if (row.Status is not { } status)
        {
            yield break;
        }

        if (status.Error is not null)
        {
            yield return new StatusReason("충돌", "오류", status.Error);
        }

        if (status.Operation is not GitOperationState.None)
        {
            yield return new StatusReason("충돌", Describe(status.Operation),
                "끝내거나 되돌리기 전에는 커밋·올리기가 막힙니다.");
        }

        if (status.Conflicted > 0)
        {
            yield return new StatusReason("충돌", $"충돌 {status.Conflicted}개",
                "「충돌」 탭에서 구간을 골라 푼 뒤 「병합 마무리」를 누릅니다.");
        }

        var work = BranchRuleFor(row).Work;
        var upload = BranchRuleFor(row).Upload;

        if (status.Upstream is null)
        {
            yield return new StatusReason("변경", "추적 없음",
                $"{status.Branch} 에 upstream 이 없습니다 — 「올리기」가 {Settings.RemoteName} 에 같은 이름으로 새로 만듭니다.");
        }
        else
        {
            if (status.Behind > 0)
            {
                yield return new StatusReason("받을 것", $"받을 것 {status.Behind}개",
                    $"{status.Upstream} 이(가) {status.Behind} 커밋 앞섭니다 — 「현재 브랜치 Pull」이나 「지금 브랜치로 병합」으로 받습니다.");
            }

            if (status.Ahead > 0)
            {
                yield return new StatusReason("올릴 것", $"올릴 것 {status.Ahead}개",
                    $"{status.Branch} 이(가) {status.Upstream} 보다 {status.Ahead} 커밋 앞섭니다 — 「올리기」가 {status.Upstream} 에 올립니다.");
            }

            if (status is { Ahead: 0, Behind: 0 })
            {
                yield return new StatusReason("깨끗함", "최신", $"{status.Upstream} 과(와) 같습니다.");
            }
        }

        // 왜: 업로드 브랜치가 지금 브랜치와 다르면 줄의 ↑↓ 는 그쪽 상태를 말해 주지 않는다 — 여기서 따로 적는다
        if (upload.Length > 0 && !string.Equals(upload, status.Branch, StringComparison.Ordinal))
        {
            var note = status.DefaultBranch is { } fallback && string.Equals(upload, fallback, StringComparison.Ordinal)
                ? $"{fallback} 과(와) 견주어 {status.AheadDefault} 앞 · {status.BehindDefault} 뒤입니다."
                : "Fetch 뒤에 견줄 수 있습니다.";
            yield return new StatusReason("올릴 것", $"업로드 브랜치 {upload}",
                $"「올리기」는 {status.Branch} 를 {Settings.RemoteName}/{status.Branch} 와 {Settings.RemoteName}/{upload} 둘 다에 올립니다. {note}");
        }

        if (work.Length > 0 && !string.Equals(work, status.Branch, StringComparison.Ordinal))
        {
            yield return new StatusReason("변경", $"작업 브랜치가 아님({work})",
                $"지금 {status.Branch} 에 있어 병합·커밋·올리기를 건너뜁니다 — 「전환」을 누르면 {work} 로 옮깁니다.");
        }

        if (status.IsDirty)
        {
            var parts = new List<string>();
            if (status.Staged > 0)
            {
                parts.Add($"스테이지 {status.Staged}");
            }

            if (status.Unstaged > 0)
            {
                parts.Add($"수정 {status.Unstaged}");
            }

            if (status.Untracked > 0)
            {
                parts.Add($"새 파일 {status.Untracked}");
            }

            yield return new StatusReason("변경", $"변경 {status.Staged + status.Unstaged + status.Untracked}개",
                $"{string.Join(" · ", parts)} — 「커밋」이 git add -A 로 모두 담습니다. 「변경 파일」 탭에서 하나씩 봅니다.");
        }

        if (stashCount > 0)
        {
            yield return new StatusReason("변경", $"stash {stashCount}개",
                "아래 목록이 git stash list 를 그대로 읽은 것입니다 — 비어 있으면 줄의 숫자도 0 이어야 합니다.");
        }

        if (!status.IsDirty && status.Conflicted == 0 && stashCount == 0 && status.Operation is GitOperationState.None)
        {
            yield return new StatusReason("깨끗함", "작업 트리 깨끗함", "커밋할 변경이 없습니다.");
        }
    }

    private RepoBranchRule BranchRuleFor(RepoRow row)
    {
        var rule = Settings.RepoBranches.TryGetValue(row.Id, out var found) ? found : new RepoBranchRule();
        return new RepoBranchRule
        {
            Work = rule.Work.Length > 0 ? rule.Work : Settings.WorkBranch.Trim(),
            Upload = rule.Upload.Length > 0 ? rule.Upload : Settings.UploadBranch.Trim()
        };
    }

    private static string Describe(GitOperationState state) => state switch
    {
        GitOperationState.Merging => "병합 중",
        GitOperationState.Rebasing => "리베이스 중",
        GitOperationState.CherryPicking => "체리픽 중",
        GitOperationState.Reverting => "되돌리는 중",
        _ => "bisect 중"
    };
}
