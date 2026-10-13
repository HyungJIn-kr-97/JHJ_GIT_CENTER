using CommunityToolkit.Mvvm.ComponentModel;

namespace GitCenter.App.ViewModels;

// 대시보드 통계 카드의 자료 모양 — 한 벌의 「이름 칸 + 수치 칸들」을 여러 표시 방식이 함께 읽는다.
// 계약: 표시 방식 낱말(donut · hbar · column · line · strip · treemap · heat · gauge · table)은 Controls/ChartView 가 그린다.
// 계약: 「cards」 는 ChartView 가 아니라 DashboardView 의 XAML 이 그린다(그룹 카드).

/// <summary>카드 머리의 표시 방식 단추 하나.</summary>
public sealed record ChartKind(string Key, string Label)
{
    // 함정: 이 앱의 ComboBox 틀은 고른 줄을 ToString 으로 그린다 — 안 덮으면 「ChartKind { Key = … }」 가 찍힌다
    public override string ToString() => Label;
}

/// 계약: Tone 은 StateBrushConverter 가 아는 상태 낱말이다 — 비면 범주 색을 자리 순서대로 받는다
public sealed record ChartSeries(string Name, string Tone = "");

/// 계약: Note 는 말풍선의 둘째 줄이다 — 그 줄의 속사정(어느 저장소가 몇 건)을 적는다
/// 계약: Tag 는 그 줄을 눌렀을 때 건네는 열쇠다 — 커밋 캘린더는 「yyyy-MM-dd..yyyy-MM-dd」(그 줄이 덮는 날)를 적는다
public sealed record ChartRow(string Label, IReadOnlyList<double> Values, string Tone = "", string Note = "", string Tag = "")
{
    public ChartRow(string label, double value, string tone = "") : this(label, [value], tone)
    {
    }

    public double Total => Values.Sum();
}

/// 계약: Mono 가 true 면 줄마다 다른 색을 주지 않는다 — 크기를 견주는 자료(순위·시점)는 한 색이 맞다
public sealed record ChartData(IReadOnlyList<ChartSeries> Series, IReadOnlyList<ChartRow> Rows, bool Mono = false)
{
    public static ChartData Empty { get; } = new([], []);

    public double Total => Rows.Sum(r => r.Total);
}

/// <summary>통계 카드 하나 — 제목 · 고를 수 있는 표시 방식 · 지금 자료.</summary>
public sealed partial class ChartCard(string key, string title, IReadOnlyList<ChartKind> kinds,
    IReadOnlyList<ChartKind>? ranges = null, string? defaultRange = null,
    IReadOnlyList<ChartKind>? metrics = null, IReadOnlySet<string>? rangedMetrics = null) : ObservableObject
{
    // 계약: 기간이 뜻을 갖는 지표의 열쇠 — 비면 모든 지표에 기간 고르개가 보인다
    private readonly IReadOnlySet<string> _rangedMetrics = rangedMetrics ?? new HashSet<string>(StringComparer.Ordinal);

    public string Key { get; } = key;

    public string Title { get; } = title;

    public IReadOnlyList<ChartKind> Kinds { get; } = kinds;

    /// 계약: 기간 고르개의 조각들 — 비면 고르개가 보이지 않는다(기간이 없는 카드)
    public IReadOnlyList<ChartKind> Ranges { get; } = ranges ?? [];

    /// 계약: 「무엇을 셀까」 고르개의 조각들 — 비면 고르개가 보이지 않는다(값이 하나뿐인 카드)
    public IReadOnlyList<ChartKind> Metrics { get; } = metrics ?? [];

    /// 계약: 지표와 표시 방식·기간은 서로 묶이지 않는다 — 기간이 뜻 없는 지표에서는 UsesRange 가 고르개만 숨긴다
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesRange))]
    private string _metric = metrics is { Count: > 0 } ? metrics[0].Key : string.Empty;

    /// 계약: 기간 고르개를 보일지 — 기간이 없는 카드와 기간이 뜻 없는 지표(파일 수 · 용량)에서는 숨긴다
    public bool UsesRange => Ranges.Count > 0 && (_rangedMetrics.Count == 0 || _rangedMetrics.Contains(Metric));

    [ObservableProperty]
    private string _kind = kinds[0].Key;

    /// 계약: 기간과 표시 방식은 서로 묶이지 않는다 — 어느 조합이든 고를 수 있다
    [ObservableProperty]
    private string _range = defaultRange ?? string.Empty;

    [ObservableProperty]
    private ChartData _data = ChartData.Empty;

    /// 계약: 제목 옆의 흐린 한 줄 요약 — 비면 자리를 차지하지 않는다
    [ObservableProperty]
    private string _caption = string.Empty;

    /// 계약: 그림의 줄(막대·칸)을 눌렀을 때 그 ChartRow 를 받아 도는 명령 — null 이면 눌러도 아무 일이 없다
    public System.Windows.Input.ICommand? RowCommand { get; set; }

    // 함정: 이 앱의 ComboBox 틀은 고른 줄을 ToString 으로 그린다 — 안 덮으면 고르개에 형식 이름이 찍힌다
    public override string ToString() => Title;

    /// 계약: 칸에 올릴 수 있는 통계 전부다 — 「UI 편집」 중 제목 자리의 고르개가 읽는다
    public IReadOnlyList<ChartCard> Choices { get; set; } = [];

    /// 계약: 고르개가 다른 통계를 고르면 (지금 카드, 고른 카드)로 불린다 — 칸을 바꾸는 일은 MainViewModel 이 한다
    public Action<ChartCard, ChartCard>? PickRequested { get; set; }

    /// 계약: 읽으면 늘 자기 자신이다 — 고르개가 이 칸의 통계를 고른 줄로 보여 준다
    public ChartCard Pick
    {
        get => this;
        set
        {
            if (value is not null && !ReferenceEquals(value, this))
            {
                PickRequested?.Invoke(this, value);
            }
        }
    }

    /// 계약: 사용자가 표시 방식이나 기간을 바꿨을 때만 불린다 — MainViewModel 이 받아 설정에 적는다
    public Action<ChartCard>? KindChanged { get; set; }

    partial void OnKindChanged(string value) => KindChanged?.Invoke(this);

    partial void OnRangeChanged(string value) => KindChanged?.Invoke(this);

    partial void OnMetricChanged(string value) => KindChanged?.Invoke(this);

    /// <summary>저장된 값을 되살린다 — 이 카드가 모르는 낱말이면 표시 방식은 첫 조각, 기간·지표는 기본값으로 둔다.</summary>
    public void Restore(string? saved, string? savedRange = null, string? savedMetric = null)
    {
        var hook = KindChanged;
        KindChanged = null;
        Kind = Kinds.Any(k => k.Key == saved) ? saved! : Kinds[0].Key;
        if (Ranges.Any(r => r.Key == savedRange))
        {
            Range = savedRange!;
        }

        if (Metrics.Any(m => m.Key == savedMetric))
        {
            Metric = savedMetric!;
        }

        KindChanged = hook;
    }
}
