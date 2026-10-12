using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Localization;
using GitCenter.Application.Services;
using GitCenter.Infrastructure.Sources;
using Microsoft.Win32;
using Serilog;

namespace GitCenter.App.ViewModels;

// 설정 「목록 출처」 탭의 「폴더 기준으로 다시 잡기」와 초기화 둘(목록·그룹 / 전체).
// 계약: 이 셋은 「적용」을 기다리지 않는다 — 확인을 받은 뒤 바로 저장하고 목록을 다시 훑는다.
// 계약: 설정 파일만 고친다 — 저장소 폴더·git 에는 손대지 않는다.

/// <summary>그룹 기준 고르개의 한 줄.</summary>
public sealed record RemapLevelChoice(int Level, string Label)
{
    // 함정: 이 앱의 ComboBox 틀은 고른 줄을 ToString 으로 그린다
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel
{
    // 왜: 그룹 폴더 아래로 한 단계 더(묶음\하위\저장소) 닿아야 그 단계의 그룹이 빠짐없이 잡힌다 — 1단계 3 · 2단계 4
    private int RemapDepth => RemapLevel + 2;

    public IReadOnlyList<RemapLevelChoice> RemapLevels { get; } =
    [
        new(1, Loc.T("1단계 — 바로 아래 폴더 이름")),
        new(2, Loc.T("2단계 — 그 아래 폴더까지")),
    ];

    [ObservableProperty]
    private string _remapFolder = string.Empty;

    [ObservableProperty]
    private RemapLevelChoice? _remapChoice;

    [ObservableProperty]
    private string? _remapPreview;

    private int RemapLevel => RemapChoice?.Level ?? 1;

    partial void OnRemapFolderChanged(string value) => RemapPreview = null;

    partial void OnRemapChoiceChanged(RemapLevelChoice? value) => RemapPreview = null;

    private void InitRemap()
    {
        var root = Settings.ScanRoots.FirstOrDefault();
        RemapFolder = root?.Path ?? string.Empty;
        RemapChoice = RemapLevels.FirstOrDefault(c => c.Level == root?.GroupLevel) ?? RemapLevels[0];
    }

    [RelayCommand]
    private void PickRemapFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("기준 폴더를 고르십시오"), Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            RemapFolder = dialog.FolderName;
        }
    }

    /// <summary>설정을 바꾸지 않고 그 폴더에서 저장소와 그룹이 어떻게 잡히는지만 본다.</summary>
    [RelayCommand]
    private async Task PreviewRemapAsync() => await ScanRemapAsync().ConfigureAwait(true);

    private async Task<int> ScanRemapAsync()
    {
        var folder = RemapFolder.Trim().Trim('"');
        if (!System.IO.Directory.Exists(folder))
        {
            RemapPreview = Loc.T("폴더를 찾지 못했습니다.");
            return -1;
        }

        var level = RemapLevel;
        RemapPreview = Loc.T("확인 중…");
        var paths = await Task.Run(() => RootScanSource.Find(folder, RemapDepth, CancellationToken.None)).ConfigureAwait(true);
        var groups = paths
            .GroupBy(p => RepoKey.GroupUnder(folder, p, level), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();

        RemapPreview = paths.Count == 0
            ? Loc.T("이 폴더 아래에서 저장소를 찾지 못했습니다.")
            : Loc.T("저장소 {0}개 · 그룹 {1}개 — {2}", paths.Count, groups.Count, string.Join("  ·  ", groups));
        return paths.Count;
    }

    [RelayCommand]
    private async Task ApplyRemapAsync()
    {
        if (IsMainBusy() || await ScanRemapAsync().ConfigureAwait(true) <= 0)
        {
            return;
        }

        var folder = RemapFolder.Trim().Trim('"');
        if (!Confirm(Loc.T("「{0}」 기준으로 그룹과 목록을 다시 잡을까요?", folder) + Environment.NewLine
                     + Loc.T("이 폴더 아래 저장소에 손으로 매긴 그룹이 풀립니다.") + Environment.NewLine
                     + Loc.T("저장소 폴더와 git 에는 손대지 않습니다.")))
        {
            return;
        }

        Settings.RemapToFolder(folder, RemapLevel, RemapDepth);
        await CommitMappingAsync(Loc.T("다시 잡았습니다 — {0}", RemapPreview ?? string.Empty)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearMappingAsync()
    {
        if (IsMainBusy() || !Confirm(
                Loc.T("목록과 그룹을 초기화할까요?") + Environment.NewLine
                + Loc.T("그룹 · 수동 추가 · 숨긴 저장소 · 저장소별 브랜치 · 「작업 중」 표시가 지워집니다.") + Environment.NewLine
                + Loc.T("루트 폴더와 나머지 설정은 그대로입니다.")))
        {
            return;
        }

        Settings.ClearMapping();
        await CommitMappingAsync(Loc.T("목록과 그룹을 초기화했습니다.")).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetEverythingAsync()
    {
        if (IsMainBusy() || !Confirm(
                Loc.T("모든 설정을 처음 상태로 되돌릴까요?") + Environment.NewLine
                + Loc.T("루트 폴더 · 그룹 · 목록 출처 · git 동작 · 화면 설정이 전부 지워집니다.") + Environment.NewLine
                + Loc.T("되돌릴 수 없습니다. 저장소 폴더와 git 에는 손대지 않습니다.")))
        {
            return;
        }

        // 왜: 언어까지 되돌리면 초기화 직후 화면이 다른 말로 바뀌어 길을 잃는다 — ResetEverything 이 언어만 남긴다
        Settings.ResetEverything();
        StartAtLogin = false;
        ReloadLists();
        InitRemap();
        OnPropertyChanged(string.Empty);
        ReapplyDisplay();

        await ApplyAsync().ConfigureAwait(true);
        await _main.ReloadMappingAsync().ConfigureAwait(true);
        TestResult = Loc.T("모든 설정을 초기화했습니다 — 위에서 기준 폴더를 골라 다시 잡아 주십시오.");
    }

    private async Task CommitMappingAsync(string done)
    {
        ReloadLists();
        try
        {
            await _store.SaveAsync(Settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "설정 저장 실패");
            TestResult = Loc.T("저장하지 못했습니다: ") + ex.Message;
            return;
        }

        await _main.ReloadMappingAsync().ConfigureAwait(true);
        TestResult = done;
    }

    // 함정: 일괄 작업이 도는 중에는 RescanAsync 가 말없이 돌아온다 — 설정만 바뀌고 목록이 옛 그대로 남는다
    private bool IsMainBusy()
    {
        if (_main.IsBusy)
        {
            TestResult = Loc.T("작업이 도는 중입니다 — 끝난 뒤에 다시 눌러 주십시오.");
        }

        return _main.IsBusy;
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "JHJ Git Center", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    /// 계약: 설정 객체를 통째로 갈아엎은 뒤 화면의 줄 목록을 그 값으로 다시 채운다
    private void ReloadLists()
    {
        Roots.Clear();
        foreach (var root in Settings.ScanRoots)
        {
            Roots.Add(new ScanRootRow(root));
        }

        Refill(ManualPaths, Settings.ManualPaths);
        Refill(Excluded, Settings.ExcludedIds);
        Refill(Owners, Settings.GitHubOwners);
        Refill(Commands, Settings.CommandSources);
    }

    private static void Refill<T>(System.Collections.ObjectModel.ObservableCollection<T> rows, IEnumerable<T> values)
    {
        rows.Clear();
        foreach (var value in values)
        {
            rows.Add(value);
        }
    }
}
