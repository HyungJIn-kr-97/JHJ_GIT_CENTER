using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 거르개 단 「그룹」 머리의 ✎ 가 여는 그룹 관리 쪽지 — 만들기 · 이름 바꾸기 · 지우기.
// 계약: 저장소를 그룹에 넣고 빼는 길은 그대로 끌어 놓기·줄 쪽지다 — 여기는 그룹 자체만 다룬다.
// 함정: 스캔이 매긴 폴더 이름 그룹은 Settings.Groups 에 없다 — 이름을 바꾸면 그 그룹의 저장소를 새 이름으로 찍어 사용자 그룹으로 바꾼다.

/// <summary>그룹 관리 쪽지의 한 줄.</summary>
public sealed partial class GroupEditRow(string key, string name, int count, bool isUserGroup) : ObservableObject
{
    /// 계약: 지금 저장돼 있는 이름 — 고치는 글(Name)과 견줘 바뀌었는지 가린다
    public string Key { get; } = key;

    [ObservableProperty]
    private string _name = name;

    public int Count { get; } = count;

    /// 계약: 사용자가 만든 그룹만 지울 수 있다 — 스캔이 매긴 폴더 이름은 지워도 다음 훑기에 돌아온다
    public bool IsUserGroup { get; } = isUserGroup;
}

public sealed partial class MainViewModel
{
    /// 계약: 그룹 관리 쪽지가 열려 있나 — 거르개 단 「그룹」 머리의 단추가 켠다
    [ObservableProperty]
    private bool _isGroupManagerOpen;

    public ObservableCollection<GroupEditRow> ManagedGroups { get; } = [];

    [RelayCommand]
    private void ToggleGroupManager()
    {
        IsGroupManagerOpen = !IsGroupManagerOpen;
        if (IsGroupManagerOpen)
        {
            RefreshManagedGroups();
        }
    }

    /// 계약: 저장소가 든 그룹 + 설정에 적힌 그룹을 합쳐 세운다 — 「그룹 없음」은 고칠 것이 없으므로 뺀다
    private void RefreshManagedGroups()
    {
        var counts = _all.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        ManagedGroups.Clear();
        foreach (var chip in GroupChips.Where(c => c.Key is { Length: > 0 }))
        {
            ManagedGroups.Add(new GroupEditRow(
                chip.Key!, chip.Key!, counts.TryGetValue(chip.Key!, out var n) ? n : 0, chip.IsUserGroup));
        }
    }

    /// <summary>적어 넣은 이름으로 빈 그룹을 만든다 — 저장소는 끌어 놓아 넣는다.</summary>
    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (Settings.Groups.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            AddActivity("그룹", StepOutcome.Skipped, $"그룹 「{name}」은(는) 이미 있습니다.");
            return;
        }

        Settings.Groups.Add(name);
        NewGroupName = string.Empty;
        AddActivity("그룹", StepOutcome.Success, $"그룹 「{name}」을(를) 만들었습니다 — 저장소를 끌어다 놓으십시오.");
        RefreshGroupChips();
        RefreshManagedGroups();
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>그룹 이름을 바꾼다 — 그 그룹에 든 저장소가 함께 따라간다.</summary>
    [RelayCommand]
    private async Task RenameGroupAsync(GroupEditRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = row.Name.Trim();
        if (name.Length == 0 || string.Equals(name, row.Key, StringComparison.Ordinal))
        {
            row.Name = row.Key;
            return;
        }

        if (row.IsUserGroup)
        {
            Settings.RenameGroup(row.Key, name);
        }
        else
        {
            // 왜: 스캔이 매긴 이름은 설정에 없다 — 그 그룹의 저장소를 새 이름으로 찍어야 바뀐다
            foreach (var repo in _all.Where(r => r.Group.Equals(row.Key, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.AssignGroup(repo.Id, name);
            }
        }

        foreach (var repo in _all.Where(r => r.Group.Equals(row.Key, StringComparison.OrdinalIgnoreCase)))
        {
            repo.UserGroup = name;
        }

        if (string.Equals(GroupFilter, row.Key, StringComparison.OrdinalIgnoreCase))
        {
            GroupFilter = name;
        }

        AddActivity("그룹", StepOutcome.Success, $"그룹 「{row.Key}」을(를) 「{name}」(으)로 바꿨습니다.");
        RefreshGroupChips();
        RefreshManagedGroups();
        ApplyFilter();
        await SaveSettingsAsync().ConfigureAwait(true);
    }
}
