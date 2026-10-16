using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Application.Services;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 충돌 탭의 「받기 미리 보기」 — 받을 것이 있는 저장소에서 받으면 무엇이 충돌하는지 미리 합쳐 보고 처리 순서를 안내한다.
// 계산은 IGitService.PreviewIncomingAsync(merge-tree)이고 작업 트리·브랜치를 바꾸지 않는다.
// 계약: 실제 충돌(병합 중)이 있으면 미리 보기는 보이지 않는다 — 그때는 구간 고르기가 먼저다.
// 계약: 「커밋하고 받기」는 띠의 커밋 제목으로 커밋한 뒤 원격의 같은 브랜치를 Merge 한다 — 충돌하면 이 탭이 실제 충돌로 바뀐다.

/// <summary>받기 미리 보기의 파일 한 줄 — 충돌이면 미리 합친 구간까지.</summary>
public sealed class IncomingFileVm(IncomingPreviewFile file)
{
    /// 계약: 충돌 파일만 갖는다 — 왼쪽 항목 목록에 올라 구간을 미리 고른다
    public ConflictFileVm? Resolver { get; } = file is { Kind: IncomingFileKind.Conflict, Content: { } content }
        ? new ConflictFileVm(content, isPreview: true)
        : null;

    public string Path => file.Path;

    public IncomingFileKind Kind => file.Kind;

    public bool IsConflict => file.Kind == IncomingFileKind.Conflict;

    public string KindText => file.Kind switch
    {
        IncomingFileKind.Conflict => Loc.T("충돌"),
        IncomingFileKind.AutoMerge => Loc.T("자동 병합"),
        _ => Loc.T("추적 전")
    };

    /// 계약: 무엇과 부딪혔는지 한 줄 — 커밋 안 한 변경인지 · 이미 커밋한 변경인지 · git 이 적은 충돌 종류
    public string Note => file.Kind switch
    {
        IncomingFileKind.Conflict => (file.IsUncommitted ? Loc.T("커밋하지 않은 변경과 같은 곳을 고쳤습니다") : Loc.T("올리지 않은 커밋과 같은 곳을 고쳤습니다"))
            + (file.Reason.Length > 0 ? $" · {file.Reason}" : string.Empty),
        IncomingFileKind.AutoMerge => Loc.T("서로 다른 곳을 고쳐 저절로 합쳐집니다"),
        _ => Loc.T("같은 이름의 파일이 들어옵니다 — 커밋에 넣거나 다른 이름으로 옮겨야 받을 수 있습니다")
    };

    public IReadOnlyList<ConflictHunk> Hunks => file.Content?.Hunks ?? [];

    public string? ContentNote => file.Content?.Note;

    public int CertainHunks => Hunks.Count(h => ConflictResolver.Suggest(h, file.Content?.HasBase ?? false).IsCertain);

    public string HunkSummary => Hunks.Count == 0
        ? string.Empty
        : Loc.T("구간 {0} · 자동으로 풀 구간 {1} · 직접 고를 구간 {2}", Hunks.Count, CertainHunks, Hunks.Count - CertainHunks);
}

public sealed partial class MainViewModel
{
    public ObservableCollection<IncomingFileVm> IncomingFiles { get; } = [];

    public ObservableCollection<string> IncomingGuide { get; } = [];

    /// 계약: 충돌이 아닌 겹침(자동 병합 · 추적 전) — 고를 것이 없어 왼쪽 목록 아래에 이름만 보인다
    public IEnumerable<IncomingFileVm> IncomingOthers => IncomingFiles.Where(f => !f.IsConflict);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming), nameof(CanApplyIncoming), nameof(IncomingSummary), nameof(IncomingConflictCount), nameof(IncomingActionText))]
    private IncomingPreview? _incoming;

    /// 계약: 미리 보기 단은 겹치거나 충돌하는 파일이 있을 때 · 미리 보기가 실패했을 때만 보인다
    public bool HasIncoming => Incoming is { } p && (p.Files.Count > 0 || p.Error is not null);

    public bool CanApplyIncoming => Incoming is { Error: null } p && p.Files.Count > 0;

    public int IncomingConflictCount => Incoming?.ConflictCount ?? 0;

    public string IncomingSummary => Incoming switch
    {
        null => string.Empty,
        { Error: { } error } => Loc.T("받기 미리 보기를 못 했습니다 — {0}", error),
        var p => Loc.T("{0} 에서 받을 커밋 {1}개 · 충돌 {2} · 자동 병합 {3} · 추적 전 {4} (마지막 Fetch 기준)",
            p.Reference, p.Behind, p.ConflictCount, p.AutoMergeCount, p.UntrackedCount)
    };

    public string IncomingActionText => Incoming?.BlocksPull ?? false ? Loc.T("커밋하고 받기") : Loc.T("지금 브랜치로 받기");

    private async Task LoadIncomingAsync(RepoRow row, int generation)
    {
        if (row.Status is not { Behind: > 0 } || row.Status.HasConflict)
        {
            return;
        }

        var preview = await _git.PreviewIncomingAsync(row.Entry, null, Settings.RemoteName, CancellationToken.None).ConfigureAwait(true);
        if (generation != _detailGeneration || !ReferenceEquals(SelectedRow, row))
        {
            return;
        }

        SetIncoming(preview);
    }

    private void SetIncoming(IncomingPreview? preview)
    {
        IncomingFiles.Clear();
        IncomingGuide.Clear();
        Incoming = preview;
        try
        {
            FillIncoming(preview);
        }
        finally
        {
            OnPropertyChanged(nameof(IncomingOthers));
            RebuildConflictItems();
        }
    }

    private void FillIncoming(IncomingPreview? preview)
    {
        if (preview is null || preview.Error is not null)
        {
            return;
        }

        // 왜: 충돌을 먼저 보여야 무엇을 고를지 바로 보인다 — 충돌 → 추적 전 → 자동 병합 차례다
        foreach (var file in preview.Files.OrderBy(f => f.Kind switch { IncomingFileKind.Conflict => 0, IncomingFileKind.Untracked => 1, _ => 2 })
                     .ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            IncomingFiles.Add(new IncomingFileVm(file));
        }

        foreach (var step in BuildIncomingGuide(preview))
        {
            IncomingGuide.Add(step);
        }
    }

    /// 계약: 처리 순서는 상황마다 다르다 — 겹침(커밋 먼저) · 충돌(구간 고르기 · 병합 마무리) · 추적 전 파일 유무로 단계를 고른다
    private IEnumerable<string> BuildIncomingGuide(IncomingPreview p)
    {
        var n = 0;
        string Step(string text) => $"{++n}. {text}";

        if (p.UntrackedCount > 0)
        {
            yield return Step(Loc.T("추적 전 파일 {0}개와 같은 이름의 파일이 들어옵니다 — 함께 올릴 파일이면 다음 커밋에 넣고, 아니면 다른 이름으로 옮겨 둡니다.", p.UntrackedCount));
        }

        if (p.BlocksPull)
        {
            yield return Step(Loc.T("지금 변경을 커밋해 저장합니다 — 커밋 제목은 아래 띠의 「커밋 제목」을 씁니다({0}).", CommitMessage));
        }

        yield return Step(p.AutoMergeCount > 0
            ? Loc.T("{0} 를 지금 브랜치로 Merge 합니다 — 겹친 파일 중 {1}개는 저절로 합쳐집니다.", p.Reference, p.AutoMergeCount)
            : Loc.T("{0} 를 지금 브랜치로 Merge 합니다.", p.Reference));

        if (p.ConflictCount > 0)
        {
            var hunks = IncomingFiles.Sum(f => f.Hunks.Count);
            var certain = IncomingFiles.Sum(f => f.CertainHunks);
            yield return Step(Loc.T("충돌 {0}개 파일 · {1}개 구간을 왼쪽 목록에서 하나씩 열어 「내 것」·「상대 것」·「둘 다」 중 고릅니다 — {2}개 구간은 자동으로 골라 두었고, 지금 고른 것은 받은 뒤에도 그대로 이어집니다. 받은 뒤 「고른 것 모두 적용」을 누릅니다.",
                p.ConflictCount, hunks, certain));
            yield return Step(Loc.T("「병합 마무리」로 병합 커밋을 만듭니다."));
        }

        yield return Step(Loc.T("Push 로 올립니다."));

        if (p.BlocksPull)
        {
            yield return Loc.T("※ 커밋하기 이른 변경이면 「작업」 탭의 stash 넣기 → 받기 → stash 꺼내기로도 됩니다 — 꺼낼 때 같은 곳에서 충돌합니다.");
        }
    }

    [RelayCommand]
    private async Task RefreshIncomingAsync()
    {
        if (SelectedRow is { } row)
        {
            SetIncoming(null);
            await LoadIncomingAsync(row, _detailGeneration).ConfigureAwait(true);
        }
    }

    /// <summary>미리 보기의 안내대로 커밋(겹칠 때만)하고 원격의 같은 브랜치를 Merge 한다.</summary>
    /// 계약: 커밋이 실패·건너뜀이면 Merge 하지 않는다 — 겹친 채로 Merge 하면 git 이 또 거부한다
    [RelayCommand]
    private async Task ApplyIncomingAsync()
    {
        if (SelectedRow is not { } row || Incoming is not { Error: null } preview)
        {
            return;
        }

        try
        {
            if (preview.BlocksPull)
            {
                var committed = await RunBulkAsync(BulkOperationKind.CommitAll, [row], showGap: false).ConfigureAwait(true);
                if (committed is null || committed.Any(r => r.Outcome != StepOutcome.Success))
                {
                    return;
                }
            }

            await RunBulkAsync(BulkOperationKind.MergeDefaultIntoCurrent, [row], ownRemote: true, showGap: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "받기 미리 보기 적용 실패 {Repo}", row.Name);
            AddActivity(row.Name, StepOutcome.Failed, ex.Message);
        }
    }
}
