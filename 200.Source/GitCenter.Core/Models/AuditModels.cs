namespace GitCenter.Core.Models;

/// <summary>저장소 하나의 설정 점검 값 — 읽기만 한 것이고 판정(무엇이 문제인가)은 화면 쪽이 한다.</summary>
public sealed class RepoAudit
{
    /// 계약: 저장소·전역을 합쳐 git 이 실제로 쓰는 값이다 — 비어 있으면 커밋이 거부된다
    public string UserName { get; init; } = string.Empty;

    public string UserEmail { get; init; } = string.Empty;

    public string? RemoteUrl { get; init; }

    public string AutoCrlf { get; init; } = string.Empty;

    public bool HasGitignore { get; init; }

    /// 계약: .git/info/exclude 에서 빈 줄·주석을 뺀 줄 수다
    public int ExcludeLines { get; init; }

    /// 계약: FETCH_HEAD 를 마지막으로 쓴 때다 — 한 번도 Fetch 하지 않았으면 null 이다
    public DateTimeOffset? LastFetch { get; init; }
}
