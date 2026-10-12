namespace GitCenter.Core.Models;

/// <summary>일괄 작업 단추 하나에 대응한다.</summary>
public enum BulkOperationKind
{
    /// 원격만 읽는다 — 작업 트리를 건드리지 않아 언제나 안전하다
    Fetch,

    /// 업로드 브랜치(비면 기본 브랜치)를 원격에서 받아 로컬의 같은 브랜치까지 갱신한다. 체크아웃은 바꾸지 않는다
    UpdateDefaultBranch,

    /// 고른 브랜치(비면 업로드 브랜치)를 원격에서 받아 지금 체크아웃된 브랜치로 병합한다
    MergeDefaultIntoCurrent,

    /// 현재 브랜치를 upstream 에서 당긴다(--ff-only)
    PullCurrent,

    /// 추적 중인 모든 로컬 브랜치를 upstream 으로 빨리감기한다
    UpdateAllTrackedBranches,

    /// 변경을 모두 스테이지하고 한 메시지로 커밋한다
    CommitAll,

    /// 현재 브랜치를 origin 으로 올린다(upstream 이 없으면 -u 로 만든다)
    Push,

    /// 커밋 + push 를 한 번에
    CommitAndPush,

    /// 진행 중인 병합·리베이스를 되돌린다
    AbortOperation,

    /// 체크아웃을 작업 브랜치로 바꾼다 — 덮어쓸 변경이 있으면 git 이 거부하고 그대로 남는다
    SwitchToWorkBranch
}

public enum StepOutcome
{
    Success,
    /// 할 일이 없었다(이미 최신 · 변경 없음) — 실패가 아니다
    Skipped,
    /// 충돌로 멈췄다 — 사람이 봐야 한다
    Conflict,
    Failed,
    /// 사용자가 중간에 멈췄다
    Canceled
}

/// <summary>일괄 작업의 저장소 한 건 결과.</summary>
public sealed class BulkStepResult
{
    public required string RepoId { get; init; }

    public required string RepoName { get; init; }

    public required BulkOperationKind Kind { get; init; }

    public StepOutcome Outcome { get; set; } = StepOutcome.Success;

    public string Message { get; set; } = string.Empty;

    /// 계약: 실패했을 때 git 이 낸 말 그대로 — 화면 우측 로그가 접어서 보여준다
    public string Detail { get; set; } = string.Empty;

    public TimeSpan Elapsed { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public List<string> ConflictPaths { get; init; } = [];

    /// 계약: 실패를 푸는 다음 단추의 이름(BulkNextAction) — 화면이 그 단추를 깜빡여 가리킨다. 비면 가리킬 것이 없다
    public string NextAction { get; set; } = string.Empty;
}

/// <summary>실패한 일괄 작업 뒤에 누를 단추의 이름 — 화면의 Nudge.Key 와 같은 글자다.</summary>
public static class BulkNextAction
{
    /// 올리기가 거부됐다(원격이 앞섬) — 「지금 브랜치로 병합」으로 받아서 합친다
    public const string Merge = "merge";

    /// 병합·받기가 충돌로 멈췄다 — 상세의 「충돌」 탭에서 구간을 골라 푼다
    public const string Conflict = "conflict";

    /// 지금 브랜치가 작업 브랜치가 아니라 건너뛰었다 — 「전환」으로 작업 브랜치에 옮긴다
    public const string Switch = "switch";

    /// 원격이 등록돼 있지 않아 건너뛰었다 — 저장소 줄의 「GitHub 에 올리기」로 만들어 연결한다
    public const string Publish = "publish";
}

/// <summary>git 한 번 실행의 결과.</summary>
public sealed class GitResult
{
    public required string CommandLine { get; init; }

    public required string WorkingDirectory { get; init; }

    public int ExitCode { get; init; }

    public string StdOut { get; init; } = string.Empty;

    public string StdErr { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }

    public bool Ok => ExitCode == 0;

    /// 계약: 실패 메시지는 stderr 우선, 비면 stdout — git 은 둘 다 쓴다
    public string Text => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}
