using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Localization;
using GitCenter.App.Services;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Settings;
using GitCenter.Infrastructure.GitHubDesktop;
using Microsoft.Win32;
using Serilog;

namespace GitCenter.App.ViewModels;

/// <summary>
/// 설정 창. 목록 출처 · 동기화 · git 동작 · 화면 네 묶음이다.
/// 계약: 고친 값은 「적용」에서 한 번에 저장한다 — 창을 닫아도 저장되지 않으면 되돌아간다.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly MainViewModel _main;

    public SettingsViewModel(ISettingsStore store, AppSettings settings, MainViewModel main)
    {
        _store = store;
        _main = main;
        Settings = settings;

        foreach (var root in settings.ScanRoots)
        {
            Roots.Add(new ScanRootRow(root));
        }

        foreach (var path in settings.ManualPaths)
        {
            ManualPaths.Add(path);
        }

        foreach (var id in settings.ExcludedIds)
        {
            Excluded.Add(id);
        }

        foreach (var owner in settings.GitHubOwners)
        {
            Owners.Add(owner);
        }

        foreach (var command in settings.CommandSources)
        {
            Commands.Add(command);
        }

        StartAtLogin = StartupRegistry.IsRegistered() && !StartupRegistry.IsDisabledByTaskManager();
    }

    public AppSettings Settings { get; }

    public ObservableCollection<ScanRootRow> Roots { get; } = [];

    public ObservableCollection<string> ManualPaths { get; } = [];

    public ObservableCollection<string> Excluded { get; } = [];

    public ObservableCollection<string> Owners { get; } = [];

    public ObservableCollection<CommandSourceDef> Commands { get; } = [];

    public IReadOnlyList<string> Palettes => ThemeManager.PaletteNames;

    public IReadOnlyList<string> Themes { get; } = [ThemeManager.SystemMode, ThemeManager.Light, ThemeManager.Dark];

    public IReadOnlyList<string> Languages { get; } = [Loc.Korean, Loc.English];

    [ObservableProperty]
    private bool _startAtLogin;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private string _newOwner = string.Empty;

    public string GitHubDesktopState => GitHubDesktopReader.IsInstalled
        ? $"설치됨 — {GitHubDesktopReader.InstallPath}"
        : "설치돼 있지 않습니다.";

    // ── 루트 ─────────────────────────────────────────────────

    [RelayCommand]
    private void AddRoot()
    {
        var dialog = new OpenFolderDialog { Title = "스캔할 루트 폴더를 고르십시오", Multiselect = false };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var root = new ScanRoot { Path = dialog.FolderName, Depth = 3 };
        Settings.ScanRoots.Add(root);
        Roots.Add(new ScanRootRow(root));
    }

    [RelayCommand]
    private void RemoveRoot(ScanRootRow? row)
    {
        if (row is null)
        {
            return;
        }

        Settings.ScanRoots.Remove(row.Model);
        Roots.Remove(row);
    }

    // ── 수동 추가 ────────────────────────────────────────────

    [RelayCommand]
    private void AddManual()
    {
        var dialog = new OpenFolderDialog { Title = "저장소 폴더를 고르십시오", Multiselect = true };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var path in dialog.FolderNames)
        {
            if (Settings.ManualPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Settings.ManualPaths.Add(path);
            ManualPaths.Add(path);
        }
    }

    [RelayCommand]
    private void RemoveManual(string? path)
    {
        if (path is null)
        {
            return;
        }

        Settings.ManualPaths.Remove(path);
        ManualPaths.Remove(path);
    }

    [RelayCommand]
    private void RestoreHidden(string? id)
    {
        if (id is null)
        {
            return;
        }

        Settings.ExcludedIds.Remove(id);
        Excluded.Remove(id);
    }

    // ── GitHub 계정 ──────────────────────────────────────────

    [RelayCommand]
    private void AddOwner()
    {
        var owner = NewOwner.Trim();
        if (owner.Length == 0 || Settings.GitHubOwners.Contains(owner, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Settings.GitHubOwners.Add(owner);
        Owners.Add(owner);
        NewOwner = string.Empty;
    }

    [RelayCommand]
    private void RemoveOwner(string? owner)
    {
        if (owner is null)
        {
            return;
        }

        Settings.GitHubOwners.Remove(owner);
        Owners.Remove(owner);
    }

    // ── 명령 출처 ────────────────────────────────────────────

    [RelayCommand]
    private void AddCommand()
    {
        // 계약: 처음 한 줄은 본보기다 — 사용자가 바로 고쳐 쓴다
        var definition = new CommandSourceDef
        {
            Name = "새 명령",
            FileName = "cmd.exe",
            Arguments = "/c dir /b /s /ad .git"
        };

        Settings.CommandSources.Add(definition);
        Commands.Add(definition);
    }

    [RelayCommand]
    private void RemoveCommand(CommandSourceDef? definition)
    {
        if (definition is null)
        {
            return;
        }

        Settings.CommandSources.Remove(definition);
        Commands.Remove(definition);
    }

    // ── 시험·저장 ────────────────────────────────────────────

    /// <summary>지금 설정대로 목록을 한 번 훑어 몇 개가 잡히는지만 본다.</summary>
    [RelayCommand]
    private async Task TestSourcesAsync()
    {
        TestResult = "확인 중…";
        try
        {
            await _main.RescanAsync().ConfigureAwait(true);
            TestResult = _main.StatusLine + (_main.SourceNotes is null ? string.Empty : "  ·  " + _main.SourceNotes);
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        Settings.ScanRoots.Clear();
        foreach (var row in Roots)
        {
            Settings.ScanRoots.Add(row.Model);
        }

        StartupRegistry.Set(StartAtLogin);
        Settings.StartAtLogin = StartAtLogin;

        ThemeManager.Apply(Settings.Palette, ThemeManager.ResolveIsDark(Settings.Theme));
        Loc.SetLanguage(Settings.Language);

        try
        {
            await _store.SaveAsync(Settings, CancellationToken.None).ConfigureAwait(true);
            TestResult = "저장했습니다.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "설정 저장 실패");
            TestResult = "저장하지 못했습니다: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ResetSources() => Reset(SettingsGroup.Sources);

    [RelayCommand]
    private void ResetGit() => Reset(SettingsGroup.Git);

    [RelayCommand]
    private void ResetDisplay() => Reset(SettingsGroup.Display);

    private void Reset(SettingsGroup group)
    {
        Settings.ResetToDefaults(group);
        OnPropertyChanged(string.Empty);

        if (group is SettingsGroup.Display)
        {
            ThemeManager.Apply(Settings.Palette, ThemeManager.ResolveIsDark(Settings.Theme));
            Loc.SetLanguage(Settings.Language);
        }
    }
}

/// <summary>설정 화면의 루트 한 줄 — 모델을 그대로 들고 바인딩만 중계한다.</summary>
public sealed partial class ScanRootRow(ScanRoot model) : ObservableObject
{
    public ScanRoot Model { get; } = model;

    public string Path
    {
        get => Model.Path;
        set
        {
            Model.Path = value;
            OnPropertyChanged();
        }
    }

    public int Depth
    {
        get => Model.Depth;
        set
        {
            Model.Depth = Math.Clamp(value, 0, 8);
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            Model.Enabled = value;
            OnPropertyChanged();
        }
    }
}
