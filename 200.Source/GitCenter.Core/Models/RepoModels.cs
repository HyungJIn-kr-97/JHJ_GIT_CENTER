namespace GitCenter.Core.Models;

/// <summary>저장소 목록을 채운 출처. 한 저장소가 여러 출처에서 동시에 잡힐 수 있다.</summary>
public enum RepoSourceKind
{
    RootScan,
    Manual,
    GitHubDesktop,
    GitHubAccount,
    Command
}

/// <summary>목록에 있는 저장소가 지금 쓸 수 있는 상태인지.</summary>
public enum RepoHealth
{
    Ok,
    /// 계약: 경로가 사라졌다 — 「다시 연동」이 이름으로 루트를 다시 뒤진다
    PathMissing,
    /// 계약: 폴더는 있는데 .git 이 없다 — 옮겨졌거나 clone 이 덜 끝났다
    NotGitRepo,
    /// 계약: 원격이 없다 — fetch·push 대상에서 자동으로 빠진다
    NoRemote,
    /// 계약: 아직 clone 하지 않았다(GitHub 계정 출처에서만 생긴다)
    NotCloned,
    Unreadable
}

/// <summary>목록의 한 줄. 경로가 신원이고, 상태(RepoStatus)는 따로 읽어 붙인다.</summary>
public sealed class RepoEntry
{
    /// 계약: 경로를 소문자·구분자 통일로 정규화한 값이 키다 — 같은 저장소를 여러 출처가 잡아도 한 줄로 합친다
    public required string Id { get; init; }

    public required string Name { get; set; }

    /// 계약: NotCloned 이면 비어 있을 수 있다
    public string Path { get; set; } = string.Empty;

    public string? RemoteUrl { get; set; }

    public string? Owner { get; set; }

    public List<RepoSourceKind> Sources { get; set; } = [];

    public RepoHealth Health { get; set; } = RepoHealth.Ok;

    /// 계약: 사용자가 목록에서 숨긴 저장소 — 스캔이 다시 찾아도 되살리지 않는다
    public bool Excluded { get; set; }

    public bool Selected { get; set; } = true;

    public DateTimeOffset? LastSeenUtc { get; set; }

    public string DisplayGroup { get; set; } = string.Empty;
}

/// <summary>한 저장소의 지금 상태 — git status·rev-list 한 번으로 모은다.</summary>
public sealed class RepoStatus
{
    public string Branch { get; set; } = string.Empty;

    public bool IsDetached { get; set; }

    public string? Upstream { get; set; }

    /// 계약: 원격이 하나라도 등록돼 있나 — false 면 올릴 곳이 없어 「Push 전」으로 세지 않는다
    public bool HasRemote { get; set; } = true;

    /// 계약: upstream 기준 — upstream 이 없으면 둘 다 0 이다
    public int Ahead { get; set; }

    public int Behind { get; set; }

    public int Staged { get; set; }

    public int Unstaged { get; set; }

    public int Untracked { get; set; }

    public int Conflicted { get; set; }

    public int StashCount { get; set; }

    /// 계약: 마지막 커밋과 견준 추가·삭제 줄 수(스테이지 + 수정) — 추적 전 새 파일의 줄은 세지 않는다
    public int Insertions { get; set; }

    public int Deletions { get; set; }

    /// 계약: 기본 브랜치(origin/HEAD 또는 main·master) 이름 — 「main 받기」가 쓴다
    public string? DefaultBranch { get; set; }

    /// 계약: 현재 브랜치가 기본 브랜치보다 뒤처진 커밋 수 — 「현재로 병합」이 필요한지 판단
    public int BehindDefault { get; set; }

    public int AheadDefault { get; set; }

    public GitOperationState Operation { get; set; } = GitOperationState.None;

    public string? LastCommitSha { get; set; }

    public string? LastCommitSubject { get; set; }

    public string? LastCommitAuthor { get; set; }

    public DateTimeOffset? LastCommitWhen { get; set; }

    public DateTimeOffset? FetchedAtUtc { get; set; }

    public string? Error { get; set; }

    public bool IsDirty => Staged + Unstaged + Untracked + Conflicted > 0;

    public bool HasConflict => Conflicted > 0 || Operation is GitOperationState.Merging or GitOperationState.Rebasing or GitOperationState.CherryPicking;
}

/// 계약: .git 의 표식 파일로 가린다 — MERGE_HEAD · REBASE_HEAD · CHERRY_PICK_HEAD
public enum GitOperationState
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
    Bisecting
}

/// <summary>커밋 하나 — 「커밋 내역」 화면의 한 줄.</summary>
/// 계약: Day 는 커밋한 날(%cs)이다 — 커밋 캘린더가 세는 날과 같은 값이다
public sealed record CommitInfo(string Sha, DateOnly Day, string Author, DateTimeOffset? When, string Subject);

/// <summary>커밋 하나가 바꾼 파일 한 줄 — 「브랜치」 탭의 이력 아래 목록이 쓴다.</summary>
/// 계약: Added·Removed 가 null 이면 바이너리라 줄을 세지 못한 것이다
public sealed record CommitFile(string Sha, string Path, int? Added, int? Removed)
{
    public string AddedText => Added is { } added ? $"+{added}" : string.Empty;

    public string RemovedText => Removed is { } removed ? $"-{removed}" : string.Empty;
}

/// <summary>stash 하나 — 「상태」 탭이 목록으로 보여준다.</summary>
/// 계약: Ref 는 stash@{0} 꼴이고 Branch 는 stash 를 만든 때의 브랜치다(%gs 머리말에서 뗀다)
public sealed record StashEntry(string Ref, string Sha, DateTimeOffset? When, string Branch, string Message, IReadOnlyList<string> Files);

/// 계약: Authors 는 작성자 이름 → 커밋 수, Hours 는 커밋한 시(0~23 · 그 커밋의 시간대 기준)별 커밋 수 스물넷이다
public sealed record CommitFacts(IReadOnlyDictionary<string, int> Authors, IReadOnlyList<int> Hours)
{
    public static CommitFacts Empty { get; } = new(new Dictionary<string, int>(), new int[24]);
}

/// 계약: 최근 커밋이 건드린 파일 하나 — Last 는 마지막으로 바뀐 날(커밋한 날), Touches 는 그 기간에 바뀐 횟수다
public sealed record FileTouch(string Path, DateOnly Last, int Touches);

/// 계약: reflog 한 줄 — When 은 기록이 생긴 때(커밋한 때가 아니다), Ref 는 「HEAD」·「refs/heads/…」·「refs/remotes/…」·「refs/stash」, Subject 는 git 의 원문이다
public sealed record ReflogEntry(DateTimeOffset When, string Ref, string Subject, string Sha);

/// <summary>git status --porcelain=v2 한 줄.</summary>
public sealed class FileChange
{
    public required string Path { get; init; }

    public string? OriginalPath { get; init; }

    /// 계약: XY 두 글자 중 X(인덱스) — 'M' · 'A' · 'D' · 'R' · '?' · 'U'
    public char Index { get; init; }

    public char WorkTree { get; init; }

    public bool IsConflict => Index == 'U' || WorkTree == 'U';

    public bool IsUntracked => Index == '?';

    // 함정: porcelain=v2 는 「변화 없음」을 공백이 아니라 '.' 으로 적는다 — v1 기준으로 세면 모든 줄이 스테이지된 것으로 잡힌다
    public bool IsStaged => Index is not ('?' or '.' or ' ' or 'U');

    public bool HasWorkTreeChange => WorkTree is not ('?' or '.' or ' ' or 'U');

    /// 계약: HEAD 와 견준 추가·삭제 줄 수 — 못 셌으면(폴더 · 큰 새 파일 · 커밋 없는 저장소) null 이다
    public int? Insertions { get; set; }

    public int? Deletions { get; set; }

    public bool IsBinary { get; set; }

    public string AddedText => Insertions is { } n ? $"+{n:N0}" : string.Empty;

    public string RemovedText => Deletions is { } n ? $"−{n:N0}" : string.Empty;

    public string LineNote => IsBinary ? "바이너리" : string.Empty;
}

/// <summary>충돌 파일 하나와 그 안의 충돌 덩어리들.</summary>
public sealed class ConflictFile
{
    public required string Path { get; init; }

    /// 계약: 양쪽 다 지웠거나 바이너리면 덩어리가 비어 있다 — 그래도 목록에는 남는다
    public List<ConflictHunk> Hunks { get; init; } = [];

    public string? Note { get; set; }

    public bool IsBinary { get; set; }

    /// 계약: 구간을 골라 앱에서 풀 수 있는 파일인가 — 한쪽이 지웠거나 바이너리면 false 다
    public bool CanResolve { get; set; }

    /// 계약: 공통 조상이 있는가 — 양쪽이 새로 만든 파일(add/add)이면 false 라 자동 판정을 거의 못 한다
    public bool HasBase { get; set; }
}

/// <summary>충돌 구간 하나를 어떻게 풀지.</summary>
/// 계약: Merged 는 공통 조상에서 양쪽이 고친 줄을 합친 것이다 — ConflictResolver.TryMergeLines 가 될 때만 고를 수 있다
public enum ConflictChoice
{
    None,
    Ours,
    Theirs,
    Both,
    Merged
}

/// <summary>충돌 해결·병합 마무리 한 번의 결과.</summary>
public sealed record ConflictActionResult(bool Ok, string Message);

/// <summary>&lt;&lt;&lt;&lt;&lt;&lt;&lt; ~ &gt;&gt;&gt;&gt;&gt;&gt;&gt; 한 덩어리.</summary>
public sealed class ConflictHunk
{
    /// 계약: 파일에서 &lt;&lt;&lt;&lt;&lt;&lt;&lt; 가 있던 줄 번호(1부터)
    public int StartLine { get; init; }

    public string OursLabel { get; init; } = "HEAD";

    public string TheirsLabel { get; init; } = string.Empty;

    public List<string> Ours { get; init; } = [];

    /// 계약: diff3 형식일 때만 채워진다(||||||| 구역)
    public List<string> Base { get; init; } = [];

    public List<string> Theirs { get; init; } = [];
}

/// <summary>브랜치 한 줄 — 로컬·원격 공용.</summary>
public sealed class BranchInfo
{
    public required string Name { get; init; }

    public bool IsRemote { get; init; }

    public bool IsCurrent { get; init; }

    public string? Upstream { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    public DateTimeOffset? LastCommitWhen { get; init; }

    public string? LastCommitSubject { get; init; }
}

/// <summary>파일 하나의 변경 내용 — git diff 를 줄 단위로 푼 것.</summary>
public sealed class FileDiff
{
    public required string Path { get; init; }

    public List<DiffLine> Lines { get; init; } = [];

    public int Added { get; set; }

    public int Removed { get; set; }

    public bool IsBinary { get; set; }

    /// 계약: 보여줄 줄이 없을 때 그 까닭 한 줄 — 바이너리 · 너무 큼 · 스테이지 전후가 같음
    public string? Note { get; set; }
}

public enum DiffLineKind
{
    /// 계약: @@ 로 시작하는 구역 머리 — 줄 번호가 어디로 건너뛰는지 알린다
    Hunk,
    Context,
    Added,
    Removed,
    /// 계약: diff 머리말(diff --git · index · --- · +++) — 접어 두고 보통은 안 보인다
    Meta
}

/// <summary>diff 한 줄.</summary>
public sealed class DiffLine
{
    public required DiffLineKind Kind { get; init; }

    public required string Text { get; init; }

    /// 계약: 그 줄이 없는 쪽은 null 이다 — 더한 줄에 OldNo 가 없고, 지운 줄에 NewNo 가 없다
    public int? OldNo { get; init; }

    public int? NewNo { get; init; }

    public string OldNoText => OldNo?.ToString() ?? string.Empty;

    public string NewNoText => NewNo?.ToString() ?? string.Empty;
}
