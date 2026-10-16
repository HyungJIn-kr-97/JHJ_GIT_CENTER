using System.Collections.ObjectModel;
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
        HasBase = hasBase && hunk.Base.Count > 0;

        // 계약: 같은 줄은 자리와 무관하게 내용으로만 센다 — 훑어보는 요약이지 병합 판정이 아니다
        var theirs = hunk.Theirs.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var same = 0;
        foreach (var line in hunk.Ours)
        {
            if (theirs.TryGetValue(line, out var left) && left > 0)
            {
                theirs[line] = left - 1;
                same++;
            }
        }

        DiffSummary = Loc.T("내 것 {0}줄 · 상대 것 {1}줄 · 같은 줄 {2} · 내 것에만 {3} · 상대 것에만 {4}",
            hunk.Ours.Count, hunk.Theirs.Count, same, hunk.Ours.Count - same, hunk.Theirs.Count - same);
    }

    public ConflictHunk Hunk { get; }

    /// 계약: 양쪽이 갈라지기 전의 내용(공통 조상) — 조상이 있을 때만 보인다
    public IReadOnlyList<string> Base => Hunk.Base;

    public bool HasBase { get; }

    /// 계약: 이 구간에서 양쪽이 얼마나 다른지 한 줄로 적은 것
    public string DiffSummary { get; }

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
    public ConflictFileVm(ConflictFile file, bool isPreview = false)
    {
        File = file;
        IsPreview = isPreview;
        Hunks = [.. file.Hunks.Select(h => new ConflictHunkVm(h, file.HasBase))];
        foreach (var hunk in Hunks)
        {
            hunk.Changed = () =>
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(Summary));
                ChoiceChanged?.Invoke(this);
            };
        }
    }

    public ConflictFile File { get; }

    /// 계약: 받기 미리 보기의 항목이다 — 고른 것은 기억만 하고, 실제로 받아 충돌이 나면 같은 구간에 이어 붙인다(파일은 안 바뀐다)
    public bool IsPreview { get; }

    public bool IsReal => !IsPreview;

    public Action<ConflictFileVm>? ChoiceChanged { get; set; }

    public string Path => File.Path;

    public string FileName => System.IO.Path.GetFileName(File.Path);

    public string Folder => System.IO.Path.GetDirectoryName(File.Path)?.Replace('\\', '/') ?? string.Empty;

    public string? Note => File.Note;

    public bool CanResolve => File.CanResolve;

    public IReadOnlyList<ConflictHunkVm> Hunks { get; }

    public bool IsReady => CanResolve && Hunks.All(h => h.Choice != ConflictChoice.None);

    /// 계약: 이 파일의 차이를 한 줄로 적은 것 — 구간 수와 양쪽 줄 수, 사람이 골라야 할 구간 수
    public string DiffSummary => Hunks.Count == 0
        ? string.Empty
        : Loc.T("구간 {0} · 내 것 {1}줄 · 상대 것 {2}줄 · 직접 고를 구간 {3}",
            Hunks.Count, Hunks.Sum(h => h.Ours.Count), Hunks.Sum(h => h.Theirs.Count), Hunks.Count(h => !h.IsCertain));

    public string Summary => !CanResolve
        ? Loc.T("앱에서 풀 수 없음")
        : Loc.T("{0} / {1} 구간 선택 · 자동 {2}", Hunks.Count(h => h.Choice != ConflictChoice.None), Hunks.Count, Hunks.Count(h => h.IsCertain));
}

public sealed partial class MainViewModel
{
    // 계약: 충돌 탭은 왼쪽 항목 목록 + 오른쪽 고른 항목 하나다 — 실제 충돌이 있으면 그것을, 없으면 받기 미리 보기의 충돌을 싣는다
    public ObservableCollection<ConflictFileVm> ConflictItems { get; } = [];

    [ObservableProperty]
    private ConflictFileVm? _selectedConflict;

    public bool HasConflictItems => ConflictItems.Count > 0;

    /// 계약: 두 단은 고를 항목이 있거나 받기 미리 보기가 있을 때 보인다 — 둘 다 없으면 「충돌이 없습니다」 한 줄이다
    public bool ShowConflictPane => HasConflictItems || HasIncoming;

    public string ConflictItemsTitle => Conflicts.Count > 0
        ? Loc.T("충돌 파일 {0} · 남은 것 {1}", ConflictItems.Count, ConflictItems.Count(f => !f.IsReady))
        : Loc.T("받으면 충돌할 파일 {0} · 미리 고른 것 {1}", ConflictItems.Count, ConflictItems.Count(f => f.IsReady));

    // 계약: 고른 구간은 저장소별로 기억한다 — 미리 보기에서 고른 것이 실제 충돌로, 다시 읽은 충돌 목록으로 이어진다
    // 함정: 같은 구간인지는 위치가 아니라 양쪽 내용으로 가린다 — 커밋 뒤 줄 번호가 달라질 수 있다
    private readonly Dictionary<string, List<(IReadOnlyList<string> Ours, IReadOnlyList<string> Theirs, ConflictChoice Choice)>> _pickedHunks =
        new(StringComparer.Ordinal);

    private string _pickedRepoId = string.Empty;

    private void RebuildConflictItems()
    {
        var keep = SelectedConflict?.Path;
        ConflictItems.Clear();
        var source = Conflicts.Count > 0
            ? Conflicts.AsEnumerable()
            : IncomingFiles.Select(f => f.Resolver).OfType<ConflictFileVm>();

        foreach (var item in source)
        {
            if (item.ChoiceChanged is null)
            {
                RestorePicks(item);
                item.ChoiceChanged = RememberPicks;
            }

            ConflictItems.Add(item);
        }

        SelectedConflict = ConflictItems.FirstOrDefault(f => f.Path == keep)
            ?? ConflictItems.FirstOrDefault(f => !f.IsReady)
            ?? ConflictItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasConflictItems));
        OnPropertyChanged(nameof(ShowConflictPane));
        OnPropertyChanged(nameof(ConflictItemsTitle));
    }

    private void ForgetPicksUnless(string repoId)
    {
        if (_pickedRepoId != repoId)
        {
            _pickedHunks.Clear();
            _pickedRepoId = repoId;
        }
    }

    private void RememberPicks(ConflictFileVm file)
    {
        _pickedHunks[file.Path] = [.. file.Hunks.Where(h => h.Choice != ConflictChoice.None).Select(h => (h.Ours, h.Theirs, h.Choice))];
        OnPropertyChanged(nameof(ConflictItemsTitle));
    }

    private void RestorePicks(ConflictFileVm file)
    {
        if (!_pickedHunks.TryGetValue(file.Path, out var picks))
        {
            return;
        }

        foreach (var hunk in file.Hunks)
        {
            var hit = picks.FirstOrDefault(p => p.Ours.SequenceEqual(hunk.Ours) && p.Theirs.SequenceEqual(hunk.Theirs));
            if (hit.Choice != ConflictChoice.None && (hit.Choice != ConflictChoice.Merged || hunk.CanMerge))
            {
                hunk.Choice = hit.Choice;
            }
        }
    }

    /// <summary>다음으로 고를 항목 — 다 고르지 않은 것 중 지금 것 다음, 없으면 처음부터.</summary>
    [RelayCommand]
    private void NextConflict()
    {
        if (ConflictItems.Count == 0)
        {
            return;
        }

        var at = SelectedConflict is null ? -1 : ConflictItems.IndexOf(SelectedConflict);
        var order = ConflictItems.Skip(at + 1).Concat(ConflictItems.Take(at + 1));
        SelectedConflict = order.FirstOrDefault(f => !f.IsReady && !ReferenceEquals(f, SelectedConflict))
            ?? ConflictItems[(at + 1) % ConflictItems.Count];
    }

    /// 계약: 병합 중인데 풀 충돌이 더 없다 — 「병합 마무리」 단추가 이때만 보인다
    public bool CanConcludeMerge => SelectedRow?.Status?.Operation == GitOperationState.Merging && Conflicts.Count == 0;

    /// 계약: 충돌 탭 맨 위의 차이 요약 — 저장소 전체에서 무엇이 얼마나 갈렸는지 한 줄이다
    public string ConflictOverview => Conflicts.Count == 0
        ? string.Empty
        : Loc.T("충돌 파일 {0}개 · 구간 {1}개 · 자동 판정 {2}개 · 직접 고를 구간 {3}개 · 앱에서 못 푸는 파일 {4}개",
            Conflicts.Count, Conflicts.Sum(f => f.Hunks.Count), Conflicts.Sum(f => f.Hunks.Count(h => h.IsCertain)),
            Conflicts.Sum(f => f.Hunks.Count(h => !h.IsCertain)), Conflicts.Count(f => !f.CanResolve));

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

        OnPropertyChanged(nameof(ConflictOverview));
        OnPropertyChanged(nameof(CanConcludeMerge));
    }
}
