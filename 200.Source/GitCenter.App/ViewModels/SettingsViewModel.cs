using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Localization;
using GitCenter.App.Services;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Settings;
using GitCenter.Infrastructure.GitHubDesktop;
using Jhj.Core.App;
using Jhj.Core.Diagnostics;
using Jhj.Core.Updates;
using Jhj.Core.Wpf.Hotkeys;
using Jhj.Core.Wpf.Shell;
using Jhj.Core.Wpf.Updates;
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

    public SettingsViewModel(ISettingsStore store, AppSettings settings, MainViewModel main, UpdateOptions updateOptions)
    {
        _store = store;
        _main = main;
        Settings = settings;
        Update = new UpdatePanelViewModel(
            updateOptions, new UpdateSettings(settings), SetAutoUpdate, (title, body) => Notify?.Invoke(title, body));
        Hotkeys = new HotkeySlotEditor([settings.Hotkey, .. settings.ExtraHotkeys], SetHotkeys);

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
        LoadIconChoices();
        InitRemap();
    }

    public AppSettings Settings { get; }

    /// 계약: 「화면」 탭의 업데이트 구역 — 화면·동작은 JHJ_CS_CORE 의 UpdatePanel 이 갖는다
    public UpdatePanelViewModel Update { get; }

    /// <summary>적용 1분 전 알림 (제목, 본문) — App 이 트레이 말풍선을 붙인다.</summary>
    public Action<string, string>? Notify { get; set; }

    // 계약: 이 창의 다른 칸처럼 「적용」에서 저장한다 — 여기서는 값만 옮긴다
    private void SetAutoUpdate(bool enabled, string time, bool notify)
    {
        Settings.AutoUpdateEnabled = enabled;
        Settings.AutoUpdateTime = time;
        Settings.AutoUpdateNotify = notify;
    }

    /// 계약: 「일반」 탭의 단축키 줄 — 줄 상태·판정·등록은 JHJ_CS_CORE 의 HotkeySlotEditor 가 갖는다
    public HotkeySlotEditor Hotkeys { get; }

    /// <summary>트레이 아이콘 고정을 지금 입힌다 — App 이 TrayHost.SetPinned 를 붙인다.</summary>
    public Action<bool>? PinTray { get; set; }

    // 계약: 등록은 편집기가 「저장」에서 이미 했다 — 여기서는 값만 옮기고 파일 저장은 「적용」이다
    private void SetHotkeys(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return;
        }

        Settings.Hotkey = texts[0];
        Settings.ExtraHotkeys = [.. texts.Skip(1)];
    }

    // 계약: Git Center 구역은 Services/GitCenterDiagnostics 가 갖는다 — 상태 줄 「오류 보고」와 같은 것을 쓴다
    private string BuildDiagnostics() => new DiagnosticsReportBuilder()
        .AppSection(Settings, Hotkeys.Slots.Select(r => r.SettledText))
        .EventLog()
        .Build();

    [RelayCommand]
    private void SaveDiagnostics()
    {
        var dialog = new SaveFileDialog
        {
            FileName = DiagnosticsReportBuilder.DefaultFileName,
            Filter = "Markdown (*.md)|*.md",
        };

        if (dialog.ShowDialog() == true)
        {
            TestResult = Loc.T(DiagnosticsReportBuilder.TrySave(dialog.FileName, BuildDiagnostics())
                ? "Saved the diagnostics file."
                : "Could not save the file.");
        }
    }

    [RelayCommand]
    private void OpenIssue()
    {
        if (!ClipboardText.TryCopy(BuildDiagnostics()))
        {
            TestResult = Loc.T("Could not copy to the clipboard.");
            return;
        }

        TestResult = Loc.T(Jhj.Core.Windows.Shell.ShellOpener.OpenUrl(AppLinks.NewIssueUrl)
            ? "Copied the diagnostics. Paste them into the issue."
            : "Could not open the link.");
    }

    public ObservableCollection<ScanRootRow> Roots { get; } = [];

    public ObservableCollection<string> ManualPaths { get; } = [];

    public ObservableCollection<string> Excluded { get; } = [];

    public ObservableCollection<string> Owners { get; } = [];

    public ObservableCollection<CommandSourceDef> Commands { get; } = [];

    public IReadOnlyList<string> Palettes => ThemeManager.PaletteNames;

    public IReadOnlyList<string> Themes => ThemeManager.ThemeNames;

    // 계약: 목록은 Core 의 LocLanguages.All 에서 만든다 — 코드를 박아 두면 Core 가 언어를 더해도 빠진다
    public IReadOnlyList<string> Languages { get; } =
        Jhj.Core.Wpf.Localization.LocLanguages.All.Select(l => l.Code).ToList();

    /// 계약: 화면 배율 선택지 — AppSettings.UiScale 과 MainWindow 의 조임 범위(0.8~1.5) 안이어야 한다
    public IReadOnlyList<double> Scales { get; } = [0.9, 1.0, 1.1, 1.25];

    // ── 모양 — 고르는 즉시 화면에 비친다 ──────────────────────
    // 왜: 색을 고르고 「적용」을 눌러야 보이면 다섯 가지를 비교하려고 다섯 번 저장하게 된다.
    //     Settings 는 알림이 없는 보통 객체라 이렇게 감싸야 바뀐 것을 가로챌 수 있다.

    public string Theme
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value)
            {
                return;
            }

            Settings.Theme = value;
            OnPropertyChanged();
            PreviewTheme();
        }
    }

    public string Palette
    {
        get => Settings.Palette;
        set
        {
            if (Settings.Palette == value)
            {
                return;
            }

            Settings.Palette = value;
            OnPropertyChanged();
            PreviewTheme();
            // 왜: 견본 색은 지금 명암에서 뽑는다 — 팔레트가 바뀌면 목록의 동그라미도 다시 그려야 한다
            OnPropertyChanged(nameof(Palettes));
        }
    }

    public string Language
    {
        get => Settings.Language;
        set
        {
            if (Settings.Language == value)
            {
                return;
            }

            Settings.Language = value;
            OnPropertyChanged();
            Loc.SetLanguage(value);
        }
    }

    public double UiScale
    {
        get => Settings.UiScale;
        set
        {
            if (Math.Abs(Settings.UiScale - value) < 0.001)
            {
                return;
            }

            Settings.UiScale = value;
            OnPropertyChanged();
            _main.NotifyDisplaySettingsChanged();
        }
    }

    public bool CompactRows
    {
        get => Settings.CompactRows;
        set
        {
            if (Settings.CompactRows == value)
            {
                return;
            }

            Settings.CompactRows = value;
            OnPropertyChanged();
            _main.NotifyDisplaySettingsChanged();
        }
    }

    public bool ShowActivityPanel
    {
        get => Settings.ShowActivityPanel;
        set
        {
            if (Settings.ShowActivityPanel == value)
            {
                return;
            }

            Settings.ShowActivityPanel = value;
            OnPropertyChanged();
            _main.NotifyDisplaySettingsChanged();
        }
    }

    private void PreviewTheme() => ThemeManager.ApplyPreference(Settings.Palette, Settings.Theme);

    // ── 아이콘 — 내장 · 앱이 그리는 모양 · 불러온 그림 ─────────
    // 규격 — JHJ_DEV/000.AGENTS_MD/080.CS앱-화면/080.CS앱-화면-공통.md §2

    /// 계약: 고르는 상자의 줄 — 내장("") · 모양("preset:키") · 불러온 그림이 있으면 그 경로 한 줄
    public ObservableCollection<string> IconChoices { get; } = [];

    public string IconChoice
    {
        get => Settings.AppIcon;
        set
        {
            // 함정: 목록을 다시 채우는 동안 ComboBox 가 null 을 되돌려 보낸다 — 고른 것이 풀리지 않게 흘려보낸다
            if (value is null || Settings.AppIcon == value)
            {
                return;
            }

            Settings.AppIcon = value;
            OnPropertyChanged();
            AppIcon.Apply(value);
        }
    }

    private void LoadIconChoices()
    {
        IconChoices.Clear();
        IconChoices.Add(AppIcon.BuiltIn);
        foreach (var preset in AppIcon.Presets)
        {
            IconChoices.Add(AppIcon.PresetPrefix + preset.Key);
        }

        var current = AppIcon.Normalize(Settings.AppIcon);
        if (!IconChoices.Contains(current))
        {
            IconChoices.Add(current);
        }

        Settings.AppIcon = current;
        OnPropertyChanged(nameof(IconChoice));
    }

    [RelayCommand]
    private void ImportIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "아이콘으로 쓸 그림을 고르십시오",
            Filter = "그림 (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 왜: 원본을 옮기거나 지워도 아이콘이 남도록 데이터 폴더에 사본을 둔다
            // 함정: 같은 이름으로 덮으면 이미 읽어 둔 그림이 안 바뀐 것처럼 보인다 — 이름에 시각을 붙인다
            var folder = AppPaths.Ensure(System.IO.Path.Combine(AppPaths.Data, "icons"));
            var name = $"custom-{DateTime.Now:yyyyMMdd-HHmmss}{System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant()}";
            var dest = System.IO.Path.Combine(folder, name);
            System.IO.File.Copy(dialog.FileName, dest, overwrite: true);

            Settings.AppIcon = dest;
            LoadIconChoices();
            AppIcon.Apply(dest);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "아이콘 그림을 가져오지 못했습니다");
            TestResult = "그림을 가져오지 못했습니다: " + ex.Message;
        }
    }

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
        PinTray?.Invoke(Settings.PinTrayIcon);
        DiagnosticsLog.Enabled = Settings.DiagnosticsEnabled;

        ThemeManager.ApplyPreference(Settings.Palette, Settings.Theme);
        Loc.SetLanguage(Settings.Language);
        _main.NotifyDisplaySettingsChanged();

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
            ReapplyDisplay();
        }
    }

    private void ReapplyDisplay()
    {
        ThemeManager.ApplyPreference(Settings.Palette, Settings.Theme);
        Loc.SetLanguage(Settings.Language);
        LoadIconChoices();
        AppIcon.Apply(Settings.AppIcon);
        _main.NotifyDisplaySettingsChanged();

        // 왜: 설정값만 되돌리면 등록된 키는 옛 값 그대로다 — 등록과 줄 목록을 함께 맞춘다
        Jhj.Core.Wpf.Hotkeys.HotkeyService.Current?.Reload([Settings.Hotkey, .. Settings.ExtraHotkeys]);
        Hotkeys.Rebuild();
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

    /// 계약: 0 은 바로 위 폴더 이름, 1·2 는 루트에서 그 단계까지의 폴더 이름이다(RepoKey.GroupUnder)
    public int GroupLevel
    {
        get => Model.GroupLevel;
        set
        {
            Model.GroupLevel = Math.Clamp(value, 0, 2);
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
