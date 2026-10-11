using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Application.Services;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 충돌 탭의 「구간마다 고르고 적용」 — 판정은 ConflictResolver, 쓰기·스테이지는 IGitService.ResolveConflictAsync 가 한다.
// 계약: 자동 판정(IsCertain)은 미리 골라 두기만 한다 — 파일을 바꾸는 것은 사용자가 「적용」을 눌렀을 때뿐이다.
// 계약: 한 파일은 구간을 전부 골라야 적용된다 — 반쯤 푼 파일을 만들지 않는다.

/// <summary>충돌 구간 하나 — 양쪽 내용과 지금 고른 방식.</summary>
public sealed partial class ConflictHunkVm : ObservableObject
{
    public ConflictHunkVm(ConflictHunk hunk, bool hasBase)
    {
        Hunk = hunk;
        var suggestion = ConflictResolver.Suggest(hunk, hasBase);
        Reason = suggestion.Reason;
        IsCertain = suggestion.IsCertain;
        CanMerge = suggestion.Choice == ConflictChoice.Merged;
        MergedLines = CanMerge ? ConflictResolver.TryMergeLines(hunk.Base, hunk.Ours, hunk.Theirs) ?? [] : [];
        _choice = suggestion.Choice;
    }

    public ConflictHunk Hunk { get; }

    public int StartLine => Hunk.StartLine;

    public IReadOnlyList<string> Ours => Hunk.Ours;

    public IReadOnlyList<string> Theirs => Hunk.Theirs;

    public IReadOnlyList<string> MergedLines { get; }

    public string Reason { get; }

    /// 계약: 사람 확인 없이 믿어도 되는 판정인가 — 화면이 「자동」 표시로 가른다
    public bool IsCertain { get; }

    public bool CanMerge { get; }

    public Action? Changed { get; set; }

    [ObservableProperty]
    private ConflictChoice _choice;

    // 계약: 단추 넷은 하나만 켜진다 — 켜진 것을 다시 누르면 「안 고름」으로 돌아간다
    public bool IsOurs { get => Choice == ConflictChoice.Ours; set => Pick(ConflictChoice.Ours, value); }

    public bool IsTheirs { get => Choice == ConflictChoice.Theirs; set => Pick(ConflictChoice.Theirs, value); }

    public bool IsBoth { get => Choice == ConflictChoice.Both; set => Pick(ConflictChoice.Both, value); }

    public bool IsMerged { get => Choice == ConflictChoice.Merged; set => Pick(ConflictChoice.Merged, value); }

    private void Pick(ConflictChoice choice, bool on) => Choice = on ? choice : Choice == choice ? ConflictChoice.None : Choice;

    partial void OnChoiceChanged(ConflictChoice value)
    {
        OnPropertyChanged(nameof(IsOurs));
        OnPropertyChanged(nameof(IsTheirs));
        OnPropertyChanged(nameof(IsBoth));
        OnPropertyChanged(nameof(IsMerged));
        Changed?.Invoke();
    }
}

/// <summary>충돌 파일 하나 — 구간들과 「다 골랐나」.</summary>
public sealed class ConflictFileVm : ObservableObject
{
    public ConflictFileVm(ConflictFile file)
    {
        File = file;
        Hunks = [.. file.Hunks.Select(h => new ConflictHunkVm(h, file.HasBase))];
        foreach (var hunk in Hunks)
        {
            hunk.Changed = () =>
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(Summary));
            };
        }
    }

    public ConflictFile File { get; }

    public string Path => File.Path;

    public string? Note => File.Note;

    public bool CanResolve => File.CanResolve;

    public IReadOnlyList<ConflictHunkVm> Hunks { get; }

    public bool IsReady => CanResolve && Hunks.All(h => h.Choice != ConflictChoice.None);

    public string Summary => !CanResolve
        ? "앱에서 풀 수 없음"
        : $"{Hunks.Count(h => h.Choice != ConflictChoice.None)} / {Hunks.Count} 구간 선택 · 자동 {Hunks.Count(h => h.IsCertain)}";
}

public sealed partial class MainViewModel
{
    /// 계약: 병합 중인데 풀 충돌이 더 없다 — 「병합 마무리」 단추가 이때만 보인다
    public bool CanConcludeMerge => SelectedRow?.Status?.Operation == GitOperationState.Merging && Conflicts.Count == 0;

    [RelayCommand]
    private async Task ApplyConflictFileAsync(ConflictFileVm? file)
    {
        if (file is not null && SelectedRow is { } row)
        {
            await ApplyConflictsAsync(row, [file]).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ApplyReadyConflictsAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var ready = Conflicts.Where(f => f.IsReady).ToList();
        if (ready.Count == 0)
        {
            AddActivity(row.Name, StepOutcome.Skipped, "구간을 전부 고른 충돌 파일이 없습니다.");
            return;
        }

        await ApplyConflictsAsync(row, ready).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ConcludeMergeAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var result = await _git.ConcludeMergeAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
        AddActivity(row.Name, result.Ok ? StepOutcome.Success : StepOutcome.Failed, "병합 마무리 — " + result.Message);
        await ReloadAfterConflictAsync(row).ConfigureAwait(true);
    }

    private async Task ApplyConflictsAsync(RepoRow row, IReadOnlyList<ConflictFileVm> files)
    {
        foreach (var file in files)
        {
            if (!file.IsReady)
            {
                AddActivity(row.Name, StepOutcome.Skipped, $"{file.Path} — 고르지 않은 구간이 있어 건너뜁니다.");
                continue;
            }

            try
            {
                var result = await _git.ResolveConflictAsync(row.Entry, file.Path, [.. file.Hunks.Select(h => h.Choice)], CancellationToken.None)
                    .ConfigureAwait(true);
                AddActivity(row.Name, result.Ok ? StepOutcome.Success : StepOutcome.Failed, $"충돌 해결 {file.Path} — {result.Message}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "충돌 해결 실패 {Repo} {Path}", row.Name, file.Path);
                AddActivity(row.Name, StepOutcome.Failed, $"충돌 해결 {file.Path} — {ex.Message}");
            }
        }

        await ReloadAfterConflictAsync(row).ConfigureAwait(true);
    }

    // 왜: 풀고 나면 줄의 상태·남은 충돌·「병합 마무리」 단추가 한꺼번에 달라진다 — 그 저장소만 다시 읽는다
    private async Task ReloadAfterConflictAsync(RepoRow row)
    {
        row.Status = await _git.ReadStatusAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
        row.RefreshedAt = DateTimeOffset.Now;
        ApplyFilter();
        UpdateStatusLine();
        if (ReferenceEquals(SelectedRow, row))
        {
            await LoadDetailAsync(row).ConfigureAwait(true);
        }

        OnPropertyChanged(nameof(CanConcludeMerge));
    }
}
