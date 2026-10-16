using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Application.Services;
using GitCenter.Core.Models;
using GitCenter.Infrastructure.GitHubDesktop;
using GitCenter.Infrastructure.Sources;
using Microsoft.Win32;

namespace GitCenter.App.ViewModels;

// 「저장소 추가」 층(Views/AddRepoLayer) — 폴더를 훑어 아직 목록에 없는 git 저장소를 골라 더한다. 저장소 목록 위 단추가 연다.
// 계약: 보기는 둘이다 — 「이 폴더」(바로 아래 폴더 + 그 안의 저장소 수)와 「아래 전부」(AddRepoDeepDepth 단계까지의 저장소).
// 계약: 더하기는 설정만 고친다 — 숨긴 저장소는 숨김을 풀고, 나머지는 수동 추가(ManualPaths)에 적는다. git 은 부르지 않는다.
// 함정: 폴더 읽기는 Task.Run 이라 빨리 옮겨 다니면 겹친다 — 세대 번호로 늦게 온 결과를 버린다.
public sealed partial class MainViewModel
{
    // 왜: 루트 스캔 기본 깊이(3)보다 한 단계 깊게 본다 — 루트에서 못 찾은 저장소를 찾으러 오는 화면이다
    private const int AddRepoDeepDepth = 4;
    private const int AddRepoPeekDepth = 2;

    [ObservableProperty]
    private bool _isAddRepoOpen;

    [ObservableProperty]
    private string _addRepoFolder = string.Empty;

    [ObservableProperty]
    private bool _addRepoDeep;

    [ObservableProperty]
    private bool _addRepoLoading;

    [ObservableProperty]
    private string _addRepoNote = string.Empty;

    private int _addRepoGeneration;

    public ObservableCollection<AddRepoCandidate> AddRepoItems { get; } = [];

    /// 계약: 켜 둔 루트 폴더 — 층 위쪽의 바로 가기 단추가 된다
    public ObservableCollection<string> AddRepoRoots { get; } = [];

    public int AddRepoCheckedCount => AddRepoItems.Count(i => i.IsChecked && i.CanCheck);

    [RelayCommand]
    private async Task OpenAddRepoAsync()
    {
        AddRepoRoots.Clear();
        foreach (var root in Settings.ScanRoots.Where(r => r.Enabled && Directory.Exists(r.Path)))
        {
            AddRepoRoots.Add(root.Path);
        }

        var start = Directory.Exists(AddRepoFolder)
            ? AddRepoFolder
            : AddRepoRoots.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IsAddRepoOpen = true;
        await LoadAddRepoAsync(start).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseAddRepo() => IsAddRepoOpen = false;

    [RelayCommand]
    private Task GoAddRepoFolder(string? folder) => LoadAddRepoAsync(folder ?? AddRepoFolder);

    [RelayCommand]
    private Task ReloadAddRepo() => LoadAddRepoAsync(AddRepoFolder);

    [RelayCommand]
    private Task AddRepoUp() =>
        Directory.GetParent(AddRepoFolder.TrimEnd('\\', '/')) is { } parent ? LoadAddRepoAsync(parent.FullName) : Task.CompletedTask;

    [RelayCommand]
    private Task EnterAddRepoItem(AddRepoCandidate? item) =>
        item is { IsRepo: false } ? LoadAddRepoAsync(item.Path) : Task.CompletedTask;

    [RelayCommand]
    private Task BrowseAddRepo()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("저장소를 찾을 폴더를 고르십시오"),
            Multiselect = false,
            InitialDirectory = Directory.Exists(AddRepoFolder) ? AddRepoFolder : string.Empty
        };
        return dialog.ShowDialog() == true ? LoadAddRepoAsync(dialog.FolderName) : Task.CompletedTask;
    }

    /// 계약: 고를 수 있는 줄이 모두 체크돼 있으면 풀고, 아니면 모두 체크한다
    [RelayCommand]
    private void ToggleAddRepoAll()
    {
        var targets = AddRepoItems.Where(i => i.CanCheck).ToList();
        var check = targets.Any(i => !i.IsChecked);
        foreach (var item in targets)
        {
            item.IsChecked = check;
        }
    }

    partial void OnAddRepoDeepChanged(bool value)
    {
        if (IsAddRepoOpen)
        {
            _ = LoadAddRepoAsync(AddRepoFolder);
        }
    }

    private async Task LoadAddRepoAsync(string folder)
    {
        folder = folder.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            AddRepoNote = Loc.T("폴더를 찾지 못했습니다: {0}", folder);
            return;
        }

        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch
        {
            // 계약: 정규화에 실패해도 적힌 그대로 읽는다
        }

        var generation = ++_addRepoGeneration;
        AddRepoFolder = folder;
        AddRepoNote = string.Empty;
        AddRepoLoading = true;

        var deep = AddRepoDeep;
        var listed = _all.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = Settings.ExcludedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<AddRepoCandidate> found;
        var note = string.Empty;
        try
        {
            found = await Task.Run(() => ReadAddRepoCandidates(folder, deep, listed, hidden)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            found = [];
            note = ex.Message;
        }

        if (generation != _addRepoGeneration)
        {
            return;
        }

        foreach (var old in AddRepoItems)
        {
            old.PropertyChanged -= OnAddRepoItemChanged;
        }

        AddRepoItems.Clear();
        foreach (var item in found)
        {
            item.PropertyChanged += OnAddRepoItemChanged;
            AddRepoItems.Add(item);
        }

        AddRepoLoading = false;
        AddRepoNote = note.Length > 0 || found.Count > 0
            ? note
            : deep ? Loc.T("이 폴더 아래에서 git 저장소를 찾지 못했습니다.") : Loc.T("하위 폴더가 없습니다.");
        OnPropertyChanged(nameof(AddRepoCheckedCount));
    }

    private void OnAddRepoItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddRepoCandidate.IsChecked))
        {
            OnPropertyChanged(nameof(AddRepoCheckedCount));
        }
    }

    private static List<AddRepoCandidate> ReadAddRepoCandidates(
        string folder, bool deep, HashSet<string> listed, HashSet<string> hidden)
    {
        var list = new List<AddRepoCandidate>();
        if (deep)
        {
            foreach (var path in RootScanSource.Find(folder, AddRepoDeepDepth, CancellationToken.None)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(folder, path);
                list.Add(AddRepoCandidate.ForRepo(path, relative == "." ? RepoKey.NameOf(path) : relative, listed, hidden));
            }

            return list;
        }

        if (GitHubDesktopReader.IsGitRepository(folder))
        {
            list.Add(AddRepoCandidate.ForRepo(folder, Loc.T("(이 폴더)") + " " + RepoKey.NameOf(folder), listed, hidden));
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(folder);
        }
        catch
        {
            return list;
        }

        foreach (var child in children.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsBrowsable(child))
            {
                continue;
            }

            var name = Path.GetFileName(child);
            if (GitHubDesktopReader.IsGitRepository(child))
            {
                list.Add(AddRepoCandidate.ForRepo(child, name, listed, hidden));
                continue;
            }

            var inside = RootScanSource.Find(child, AddRepoPeekDepth, CancellationToken.None);
            list.Add(AddRepoCandidate.ForFolder(child, name, inside.Count,
                inside.Count(p => !listed.Contains(RepoKey.ForPath(p)))));
        }

        return list;
    }

    // 왜: 「.」으로 시작하는 폴더와 숨김·시스템 폴더($Recycle.Bin 등)는 저장소를 두는 자리가 아니다
    private static bool IsBrowsable(string path)
    {
        if (Path.GetFileName(path).StartsWith('.'))
        {
            return false;
        }

        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task ApplyAddRepoAsync()
    {
        var picked = AddRepoItems.Where(i => i.IsChecked && i.CanCheck).ToList();
        if (picked.Count == 0)
        {
            AddRepoNote = Loc.T("더할 저장소에 체크해 주십시오.");
            return;
        }

        if (IsBusy)
        {
            AddRepoNote = Loc.T("다른 작업이 도는 중입니다 — 끝난 뒤 다시 눌러 주십시오.");
            return;
        }

        var unhidden = 0;
        var manual = 0;
        foreach (var item in picked)
        {
            if (Settings.ExcludedIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                unhidden++;
                continue;
            }

            // 왜: 목록에 없다는 것은 어느 출처도 못 찾았다는 뜻이다 — 수동 추가에 적어야 다음 훑기에도 남는다
            if (!Settings.ManualPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                Settings.ManualPaths.Add(item.Path);
                manual++;
            }
        }

        await SaveSettingsAsync().ConfigureAwait(true);
        AddActivity(Loc.T("저장소 추가"), StepOutcome.Success,
            Loc.T("저장소 {0}개를 목록에 더했습니다 — 수동 추가 {1} · 숨김 해제 {2}.", picked.Count, manual, unhidden));
        Settings.LastSelectedRepoId = picked[0].Id;
        IsAddRepoOpen = false;
        await RescanAsync().ConfigureAwait(true);
    }
}

/// <summary>「저장소 추가」 층의 한 줄 — git 저장소이거나, 안으로 들어갈 수 있는 보통 폴더다.</summary>
public sealed partial class AddRepoCandidate : ObservableObject
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public string Id => RepoKey.ForPath(Path);

    public bool IsRepo { get; init; }

    public bool IsListed { get; init; }

    public bool IsHidden { get; init; }

    public int InsideCount { get; init; }

    public int InsideAddable { get; init; }

    public bool CanCheck => IsRepo && !IsListed;

    /// 계약: 화면의 색·아이콘을 가르는 값 — repo · listed · hidden · folder · empty
    public string Kind => !IsRepo ? (InsideCount > 0 ? "folder" : "empty") : IsListed ? "listed" : IsHidden ? "hidden" : "repo";

    public string StateText => Kind switch
    {
        "listed" => Loc.T("목록에 있음"),
        "hidden" => Loc.T("숨긴 저장소 — 체크하면 다시 보입니다"),
        "repo" => Loc.T("추가할 수 있음"),
        "folder" => Loc.T("저장소 {0}개 · 추가할 수 있는 것 {1}개", InsideCount, InsideAddable),
        _ => Loc.T("저장소 없음")
    };

    [ObservableProperty]
    private bool _isChecked;

    public static AddRepoCandidate ForRepo(string path, string name, HashSet<string> listed, HashSet<string> hidden)
    {
        var id = RepoKey.ForPath(path);
        var isListed = listed.Contains(id);
        var isHidden = hidden.Contains(id);
        // 계약: 새 저장소는 미리 체크해 두고, 숨긴 저장소는 일부러 숨긴 것이라 체크하지 않는다
        return new AddRepoCandidate
        {
            Path = path, Name = name, IsRepo = true, IsListed = isListed, IsHidden = isHidden,
            IsChecked = !isListed && !isHidden
        };
    }

    public static AddRepoCandidate ForFolder(string path, string name, int inside, int addable) =>
        new() { Path = path, Name = name, InsideCount = inside, InsideAddable = addable };
}
