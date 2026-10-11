using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 변경 파일 목록의 확장자 거르개 — 고른 저장소의 변경 파일을 확장자 하나로 좁힌다(저장소 목록은 건드리지 않는다).
// 계약: Changes 는 걸러진 목록이고 전체는 _allChanges 가 갖는다 — 채우는 길은 SetChanges 하나다.

public sealed partial class MainViewModel
{
    private const string NoExtension = "(없음)";
    private const string FolderLabel = "(폴더)";

    private List<FileChange> _allChanges = [];

    /// 계약: 확장자가 둘 이상일 때만 채운다 — 하나뿐이면 고를 것이 없어 줄을 숨긴다
    public ObservableCollection<FacetItem> ChangeExtensions { get; } = [];

    /// 계약: null 이면 전체 — 소문자 확장자(".cs")거나 NoExtension 이다
    [ObservableProperty]
    private string? _changeExtFilter;

    partial void OnChangeExtFilterChanged(string? value) => ApplyChangeFilter();

    [RelayCommand]
    private void PickChangeExt(FacetItem? item)
    {
        if (item is not null)
        {
            ChangeExtFilter = item.IsActive ? null : item.Value;
        }
    }

    private static string ExtensionOf(FileChange change)
    {
        // 왜: 추적 전 폴더는 경로가 '/' 로 끝난다 — 이름에 점이 있으면(v0.9/) 그 뒤가 확장자로 둔갑한다
        if (change.Path.EndsWith('/'))
        {
            return FolderLabel;
        }

        var extension = Path.GetExtension(change.Path);
        return extension.Length == 0 ? NoExtension : extension.ToLowerInvariant();
    }

    private void SetChanges(IEnumerable<FileChange> changes)
    {
        _allChanges = changes.ToList();

        // 왜: 저장소를 바꿨는데 그 확장자가 없으면 빈 목록으로 보인다 — 없을 때만 거르개를 푼다
        if (ChangeExtFilter is not null && !_allChanges.Any(c => ExtensionOf(c) == ChangeExtFilter))
        {
            ChangeExtFilter = null;
            return;
        }

        ApplyChangeFilter();
    }

    private void ApplyChangeFilter()
    {
        // 함정: 목록을 비우면 ListBox 가 SelectedItem 을 null 로 되돌려 보낸다 — 남아 있으면 다시 고른다
        var keep = SelectedChange;

        Changes.Clear();
        foreach (var change in _allChanges.Where(c => ChangeExtFilter is null || ExtensionOf(c) == ChangeExtFilter))
        {
            Changes.Add(change);
        }

        ChangeExtensions.Clear();
        var groups = _allChanges.GroupBy(ExtensionOf, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (groups.Count > 1)
        {
            ChangeExtensions.Add(new FacetItem("ext", null, "전체", _allChanges.Count, ChangeExtFilter is null));
            foreach (var group in groups)
            {
                ChangeExtensions.Add(new FacetItem("ext", group.Key, group.Key, group.Count(), group.Key == ChangeExtFilter));
            }
        }

        if (keep is not null && Changes.Contains(keep))
        {
            SelectedChange = keep;
        }
    }
}
