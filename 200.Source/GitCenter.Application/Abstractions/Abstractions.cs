using GitCenter.Application.Settings;
using GitCenter.Core.Models;

namespace GitCenter.Application.Abstractions;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    string SettingsPath { get; }
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <summary>git 실행기 — 인자를 배열로 받아 셸을 거치지 않는다.</summary>
/// 함정: 인자를 한 문자열로 이어 붙이면 공백 있는 경로·커밋 메시지가 조용히 잘린다.
public interface IGitRunner
{
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken, int? timeoutSeconds = null);

    /// 계약: git 이 PATH 에 있나 — 없으면 앱이 첫 화면에서 알린다
    bool IsGitAvailable { get; }

    string? GitVersion { get; }
}

/// <summary>저장소 목록을 채우는 한 출처.</summary>
public interface IRepoSource
{
    RepoSourceKind Kind { get; }

    string DisplayName { get; }

    bool IsEnabled(AppSettings settings);

    /// 계약: 못 찾아도 예외를 내지 않는다 — 빈 목록과 Diagnostic 한 줄로 알린다
    Task<RepoSourceResult> DiscoverAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed class RepoSourceResult
{
    public required RepoSourceKind Kind { get; init; }

    public List<RepoEntry> Entries { get; init; } = [];

    /// 계약: 출처가 왜 비었는지 한 줄 — 설정 화면이 그대로 보여준다
    public string? Note { get; set; }

    public bool Failed { get; set; }
}

/// <summary>한 저장소를 읽고 조작하는 일 — 모두 git 명령 한두 번이다.</summary>
public interface IGitService
{
    Task<RepoStatus> ReadStatusAsync(RepoEntry repo, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileChange>> ReadChangesAsync(RepoEntry repo, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchInfo>> ReadBranchesAsync(RepoEntry repo, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConflictFile>> ReadConflictsAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 한 파일의 변경 내용 — 읽기뿐이라 작업 트리를 건드리지 않는다
    Task<FileDiff> ReadDiffAsync(RepoEntry repo, FileChange change, CancellationToken cancellationToken);

    Task<BulkStepResult> ExecuteAsync(RepoEntry repo, BulkOperationKind kind, BulkOperationOptions options, CancellationToken cancellationToken);

    Task<string?> ReadDefaultBranchAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 지금 브랜치에서 최근 days 일 동안의 커밋 날짜 — 커밋 하나에 날짜 하나다(로컬만 읽는다)
    Task<IReadOnlyList<DateOnly>> ReadCommitDaysAsync(RepoEntry repo, int days, CancellationToken cancellationToken);

    /// 계약: 추적 중인 파일을 확장자(소문자 · 점 포함)별로 센다 — 확장자가 없으면 빈 열쇠다(로컬만 읽는다)
    Task<IReadOnlyDictionary<string, int>> ReadFileExtensionsAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 지금 브랜치의 최근 days 일 커밋을 작성자별 · 시간대별로 센다 — 로컬만 읽는다
    Task<CommitFacts> ReadCommitFactsAsync(RepoEntry repo, int days, CancellationToken cancellationToken);

    /// 계약: 지금 브랜치의 최근 days 일 커밋이 건드린 파일을 경로마다 하나(마지막 날 · 횟수)로 준다 — 로컬만 읽는다
    Task<IReadOnlyList<FileTouch>> ReadFileTouchesAsync(RepoEntry repo, int days, CancellationToken cancellationToken);

    /// 계약: 모든 ref 의 reflog 에서 최근 days 일 안에 생긴 줄을 새것부터 준다(최대 300) — 로컬만 읽는다
    Task<IReadOnlyList<ReflogEntry>> ReadReflogAsync(RepoEntry repo, int days, CancellationToken cancellationToken);

    /// 계약: reference(브랜치 · 원격 추적 ref)의 커밋을 새것부터 max 건 준다 — 읽기뿐이다
    Task<IReadOnlyList<CommitInfo>> ReadBranchLogAsync(RepoEntry repo, string reference, int max, CancellationToken cancellationToken);

    /// 계약: reference 의 커밋 중 어느 원격 추적 ref 에도 없는 것(아직 Push 안 한 것)의 sha 다 — 로컬이 아는 ref 로만 가른다
    Task<IReadOnlySet<string>> ReadUnpushedAsync(RepoEntry repo, string reference, int max, CancellationToken cancellationToken);

    /// 계약: 커밋 하나가 바꾼 파일과 줄 수다 — 병합 커밋은 첫 부모와 견준다. 읽기뿐이다
    Task<IReadOnlyList<CommitFile>> ReadCommitFilesAsync(RepoEntry repo, string sha, CancellationToken cancellationToken);

    /// 계약: 그 커밋이 한 파일에 낸 변경 내용이다 — 읽기뿐이라 작업 트리를 건드리지 않는다
    Task<FileDiff> ReadCommitDiffAsync(RepoEntry repo, CommitFile file, CancellationToken cancellationToken);

    /// 계약: 그 브랜치로 체크아웃을 바꾼다 — 원격 브랜치면 같은 이름의 로컬로(없으면 추적 브랜치를 만들어) 옮긴다. 강제 옵션은 없다
    Task<(bool Ok, string Message)> SwitchBranchAsync(RepoEntry repo, BranchInfo branch, CancellationToken cancellationToken);

    /// 계약: 일괄 커밋·올리기가 「작업 브랜치 아님」으로 건너뛸 저장소면 (지금 브랜치, 작업 브랜치)를, 아니면 null 을 준다 — 읽기뿐이다
    Task<(string Current, string Work)?> ReadOffWorkBranchAsync(RepoEntry repo, BulkOperationOptions options, CancellationToken cancellationToken);

    /// 계약: .git 객체가 디스크에서 차지하는 크기(KiB · 풀린 것 + 묶인 것)다 — 작업 트리는 세지 않는다
    Task<long> ReadObjectSizeAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 커밋한 날이 from~to(양끝 포함)인 지금 브랜치의 커밋을 새것부터 준다 — 읽기뿐이다
    Task<IReadOnlyList<CommitInfo>> ReadCommitsAsync(RepoEntry repo, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// 계약: stash 목록을 새것부터 준다 — 읽기뿐이고 작업 트리·인덱스를 건드리지 않는다
    Task<IReadOnlyList<StashEntry>> ReadStashesAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 구간마다 고른 대로 파일을 다시 써서 스테이지한다 — 쓰기 전의 작업 파일은 .git 안에 백업한다
    Task<ConflictActionResult> ResolveConflictAsync(RepoEntry repo, string path, IReadOnlyList<ConflictChoice> choices, CancellationToken cancellationToken);

    /// 계약: 충돌이 다 풀린 병합을 커밋으로 맺는다 — 병합 중일 때만 하고 리베이스·체리픽은 건드리지 않는다
    Task<ConflictActionResult> ConcludeMergeAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 읽기뿐이다 — git config 한 번과 파일 존재 확인만 하고 원격에는 닿지 않는다
    Task<RepoAudit> ReadAuditAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 저장소 로컬 설정(--local)에만 쓴다 — 전역은 건드리지 않는다. 실패하면 그 까닭을, 되면 null 을 돌려준다
    Task<string?> WriteAuthorAsync(RepoEntry repo, string name, string email, CancellationToken cancellationToken);

    /// 계약: .git/info/exclude 의 줄(빈 줄·주석 제외)을 읽는다 — 파일이 없으면 빈 목록이다
    Task<IReadOnlyList<string>> ReadExcludeAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 없는 줄만 끝에 덧붙인다 — 있는 줄은 지우지도 고치지도 않는다. 실패하면 그 까닭을, 되면 null 을 돌려준다
    Task<string?> AppendExcludeAsync(RepoEntry repo, IReadOnlyList<string> lines, CancellationToken cancellationToken);

    /// 계약: 작업 트리 맨 위 .gitignore 의 줄(빈 줄·주석 제외)을 읽는다 — 파일이 없으면 빈 목록이다
    Task<IReadOnlyList<string>> ReadGitignoreAsync(RepoEntry repo, CancellationToken cancellationToken);

    /// 계약: 없는 줄만 끝에 덧붙이고 파일이 없으면 만든다 — 스테이지·커밋은 하지 않는다. 실패하면 그 까닭을, 되면 null 을 돌려준다
    Task<string?> AppendGitignoreAsync(RepoEntry repo, IReadOnlyList<string> lines, CancellationToken cancellationToken);
}

/// <summary>일괄 작업에 넘기는 값 — 커밋 메시지처럼 작업마다 다른 것만 담는다.</summary>
public sealed class BulkOperationOptions
{
    public string CommitMessage { get; set; } = string.Empty;

    /// 계약: 커밋의 본문이다 — 비면 제목(CommitMessage)만 들어간다
    public string CommitBody { get; set; } = string.Empty;

    /// 계약: Push 는 언제나 upstream 없는 브랜치에 -u 를 붙인다. force 는 어디에서도 쓰지 않는다
    public bool SetUpstreamOnPush { get; set; } = true;

    public bool FastForwardOnly { get; set; } = true;

    public bool AutoStash { get; set; }

    public string RemoteName { get; set; } = "origin";

    /// 계약: 내가 일하는 브랜치(공통) — 비면 지금 브랜치를 그대로 쓴다. 지금 브랜치가 이것과 다르면 병합·커밋·올리기를 건너뛴다
    public string WorkBranch { get; set; } = string.Empty;

    /// 계약: 코드가 모이는 브랜치(공통) — 받기·병합이 여기서 가져오고 올리기가 여기로 올린다. 비면 받기는 기본 브랜치, 올리기는 지금 브랜치다
    public string UploadBranch { get; set; } = string.Empty;

    /// 계약: 저장소 Id → 그 저장소만의 규칙. 빈 칸은 공통 값을 따른다
    public IReadOnlyDictionary<string, RepoBranchRule> RepoBranches { get; set; } = new Dictionary<string, RepoBranchRule>();

    /// 계약: 「병합」이 이번 한 번 가져올 브랜치 — 비면 저장소마다 업로드 브랜치를 가져온다
    public string MergeSource { get; set; } = string.Empty;

    /// 계약: 저장소 Id → 사용자가 「작업 브랜치가 아니어도 이 브랜치 그대로」라고 확인한 브랜치 — 지금 브랜치가 이 값일 때만 건너뛰지 않는다
    public IReadOnlyDictionary<string, string> ConfirmedBranches { get; set; } = new Dictionary<string, string>();

    /// 계약: 켜면 「병합」이 MergeSource·업로드 브랜치 대신 원격의 같은 브랜치(<remote>/<지금 브랜치>)를 합친다 — 올리기가 거부된 저장소를 푸는 길이다
    public bool MergeOwnRemote { get; set; }

    public string WorkBranchFor(RepoEntry repo) =>
        RepoBranches.TryGetValue(repo.Id, out var rule) && rule.Work.Length > 0 ? rule.Work : WorkBranch.Trim();

    public string UploadBranchFor(RepoEntry repo) =>
        RepoBranches.TryGetValue(repo.Id, out var rule) && rule.Upload.Length > 0 ? rule.Upload : UploadBranch.Trim();

    public int TimeoutSeconds { get; set; } = 120;
}
