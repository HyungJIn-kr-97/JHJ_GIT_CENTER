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

    Task<BulkStepResult> ExecuteAsync(RepoEntry repo, BulkOperationKind kind, BulkOperationOptions options, CancellationToken cancellationToken);

    Task<string?> ReadDefaultBranchAsync(RepoEntry repo, CancellationToken cancellationToken);
}

/// <summary>일괄 작업에 넘기는 값 — 커밋 메시지처럼 작업마다 다른 것만 담는다.</summary>
public sealed class BulkOperationOptions
{
    public string CommitMessage { get; set; } = string.Empty;

    /// 계약: Push 는 언제나 upstream 없는 브랜치에 -u 를 붙인다. force 는 어디에서도 쓰지 않는다
    public bool SetUpstreamOnPush { get; set; } = true;

    public bool FastForwardOnly { get; set; } = true;

    public bool AutoStash { get; set; }

    public string RemoteName { get; set; } = "origin";

    public int TimeoutSeconds { get; set; } = 120;
}
