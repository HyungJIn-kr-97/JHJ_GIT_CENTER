using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// 오른쪽 단의 로그 탭 하나(작업 로그 · 시스템 작업 로그 · 시스템 오류)가 쓰는 거르개 — 결과 분류 + 검색.
// 계약: 원본 목록은 건드리지 않는다 — 탭마다 따로 만든 보기(ListCollectionView)만 거른다. 같은 원본을 두 탭이 함께 쓴다
// 계약: errorsOnly 면 실패·충돌만 남긴다 — 「시스템 오류」 탭이 전체 기록(Activity)에 이것을 건다
public sealed partial class ActivityLogView : ObservableObject
{
    public const string All = "all";
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Conflict = "conflict";
    public const string Skipped = "skipped";

    private readonly ObservableCollection<ActivityEntry> _source;

    public ActivityLogView(ObservableCollection<ActivityEntry> source, bool errorsOnly = false)
    {
        _source = source;
        ErrorsOnly = errorsOnly;
        Items = new ListCollectionView(source) { Filter = o => o is ActivityEntry entry && Shows(entry) };
        source.CollectionChanged += (_, _) => Recount();
        Recount();
    }

    public ICollectionView Items { get; }

    public bool ErrorsOnly { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsOk), nameof(IsFailed), nameof(IsConflict), nameof(IsSkipped))]
    private string _kind = All;

    // 계약: 알약은 이 값에 양방향으로 묶인다 — 켜면 그 분류, 켜진 것을 끄면 「전체」다
    public bool IsAll { get => Kind == All; set => Choose(All, true); }

    public bool IsOk { get => Kind == Ok; set => Choose(Ok, value); }

    public bool IsFailed { get => Kind == Failed; set => Choose(Failed, value); }

    public bool IsConflict { get => Kind == Conflict; set => Choose(Conflict, value); }

    public bool IsSkipped { get => Kind == Skipped; set => Choose(Skipped, value); }

    private void Choose(string kind, bool on)
    {
        Kind = on ? kind : All;

        // 함정: 알약은 누르는 순간 제 IsChecked 를 뒤집는다 — 값이 안 바뀐 때(「전체」를 또 누름)도 다시 알려야 되돌아온다
        foreach (var name in new[] { nameof(IsAll), nameof(IsOk), nameof(IsFailed), nameof(IsConflict), nameof(IsSkipped) })
        {
            OnPropertyChanged(name);
        }
    }

    /// 계약: 이 탭이 다루는 기록 수다 — 거르개(분류·검색)를 걸기 전의 수라 탭 머리의 칩이 이것을 쓴다
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _okCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private int _skippedCount;

    /// 계약: 기록은 있는데 거르개에 다 걸러졌을 때만 참이다 — 「기록 없음」 문구와 겹치지 않는다
    [ObservableProperty]
    private bool _isFilteredEmpty;

    partial void OnSearchTextChanged(string value) => Apply();

    partial void OnKindChanged(string value) => Apply();

    private void Apply()
    {
        Items.Refresh();
        IsFilteredEmpty = TotalCount > 0 && Items.IsEmpty;
    }

    private void Recount()
    {
        var scope = _source.Where(InScope).ToList();
        TotalCount = scope.Count;
        OkCount = scope.Count(e => KindOf(e) == Ok);
        FailedCount = scope.Count(e => KindOf(e) == Failed);
        ConflictCount = scope.Count(e => KindOf(e) == Conflict);
        SkippedCount = scope.Count(e => KindOf(e) == Skipped);
        IsFilteredEmpty = TotalCount > 0 && Items.IsEmpty;
    }

    private bool InScope(ActivityEntry entry) =>
        !ErrorsOnly || entry.Outcome is StepOutcome.Failed or StepOutcome.Conflict;

    private bool Shows(ActivityEntry entry)
    {
        if (!InScope(entry) || (Kind != All && KindOf(entry) != Kind))
        {
            return false;
        }

        var text = SearchText.Trim();
        return text.Length == 0
            || entry.RepoName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.TimeText.Contains(text, StringComparison.Ordinal);
    }

    // 계약: 「취소」는 「건너뜀」과 한 분류로 센다 — 둘 다 일이 일어나지 않은 기록이다
    private static string KindOf(ActivityEntry entry) => entry.Outcome switch
    {
        StepOutcome.Success => Ok,
        StepOutcome.Failed => Failed,
        StepOutcome.Conflict => Conflict,
        _ => Skipped
    };
}
