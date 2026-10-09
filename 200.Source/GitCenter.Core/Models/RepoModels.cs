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

    /// 계약: upstream 기준 — upstream 이 없으면 둘 다 0 이다
    public int Ahead { get; set; }

    public int Behind { get; set; }

    public int Staged { get; set; }

    public int Unstaged { get; set; }

    public int Untracked { get; set; }

    public int Conflicted { get; set; }

    public int StashCount { get; set; }

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
}

/// <summary>충돌 파일 하나와 그 안의 충돌 덩어리들.</summary>
public sealed class ConflictFile
{
    public required string Path { get; init; }

    /// 계약: 양쪽 다 지웠거나 바이너리면 덩어리가 비어 있다 — 그래도 목록에는 남는다
    public List<ConflictHunk> Hunks { get; init; } = [];

    public string? Note { get; set; }

    public bool IsBinary { get; set; }
}

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
