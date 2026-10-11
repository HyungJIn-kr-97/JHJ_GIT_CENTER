using CommunityToolkit.Mvvm.ComponentModel;

namespace GitCenter.App.ViewModels;

/// <summary>
/// 저장소 목록의 그룹 머리 한 줄이자 대시보드의 그룹 카드 하나.
/// 계약: 같은 그룹의 RepoRow 는 같은 GroupNode 객체를 든다 — 목록이 이 객체로 묶고(PropertyGroupDescription) 머리가 이 객체를 그린다.
/// 계약: 숫자는 MainViewModel.RefreshGroups 가 채운다 — 스스로 세지 않는다.
/// </summary>
public sealed partial class GroupNode(string key) : ObservableObject
{
    private bool _syncing;
    private bool _allSelected;

    /// 계약: 빈 문자열이면 「그룹 없음」이다
    public string Key { get; } = key;

    public string Label => Key.Length == 0 ? "그룹 없음" : Key;

    /// 계약: 사용자가 만든 그룹인가 — 스캔이 매긴 폴더 이름과 가른다
    [ObservableProperty]
    private bool _isUserGroup;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(Clean))]
    private int _total;

    /// 계약: 거르개를 통과해 지금 목록에 보이는 수
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Clean))]
    private int _dirty;

    [ObservableProperty]
    private int _behind;

    [ObservableProperty]
    private int _ahead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Clean))]
    private int _problem;

    public string CountText => Shown == Total ? Total.ToString() : $"{Shown} / {Total}";

    /// 계약: 변경도 문제도 없는 저장소 수 — 그룹 카드의 막대가 Total 과 견준다
    public int Clean => Math.Max(0, Total - Dirty - Problem);

    /// <summary>사용자가 그룹 체크 상자를 눌렀다 — 그 그룹의 저장소를 모두 고르거나 푼다.</summary>
    public event Action<GroupNode, bool>? SelectionRequested;

    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (SetProperty(ref _allSelected, value) && !_syncing)
            {
                SelectionRequested?.Invoke(this, value);
            }
        }
    }

    /// 함정: 줄의 선택을 세어 되비칠 때 SelectionRequested 가 다시 돌면 한 줄만 풀어도 그룹 전체가 풀린다 — 알림 없이 넣는다
    public void SyncAllSelected(bool value)
    {
        _syncing = true;
        AllSelected = value;
        _syncing = false;
    }
}
