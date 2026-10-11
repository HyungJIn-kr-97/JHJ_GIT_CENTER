using CommunityToolkit.Mvvm.ComponentModel;

namespace GitCenter.App.ViewModels;

// 대시보드 통계 카드 여덟 — 자료는 이미 읽어 둔 상태(_all)에서만 센다. git 을 더 부르지 않는다.
// 계약: 카드 이름(state · group · change · top · age · branch · lines · calendar)은 Settings.DashboardChartKinds 의 열쇠다 — 바꾸면 저장값을 잃는다.
// 계약: 칸 크기 저장은 화면(DashboardView)이 한다 — 여기는 「편집 중인가」만 든다.
public sealed partial class MainViewModel
{
    private static readonly ChartKind Donut = new("donut", "도넛");
    private static readonly ChartKind Bars = new("hbar", "막대");
    private static readonly ChartKind Columns = new("column", "세로");
    private static readonly ChartKind Line = new("line", "선");
    private static readonly ChartKind Strip = new("strip", "띠");
    private static readonly ChartKind Treemap = new("treemap", "트리맵");
    private static readonly ChartKind Heat = new("heat", "히트맵");
    private static readonly ChartKind Gauge = new("gauge", "게이지");
    private static readonly ChartKind Table = new("table", "표");

    // 계약: 대표 상태 다섯은 서로 겹치지 않는다(줄의 상태 점과 같은 한 단어) — 합이 저장소 수다
    private static readonly (string Label, string Tone, int Severity)[] States =
    [
        ("깨끗함", "깨끗함", 4), ("변경", "변경", 3), ("올릴 것", "올릴 것", 2), ("받을 것", "받을 것", 1), ("문제", "충돌", 0)
    ];

    public ChartCard StateChart { get; } = new("state", "대표 상태 분포", [Donut, Bars, Strip, Gauge, Table]);

    public ChartCard GroupChart { get; } = new("group", "그룹별 현황", [new("cards", "카드"), Bars, Heat, Treemap, Table]);

    public ChartCard ChangeChart { get; } = new("change", "변경 파일 구성", [Donut, Bars, Columns, Strip, Table]);

    public ChartCard TopChart { get; } = new("top", "변경 많은 저장소", [Bars, Treemap, Columns, Table]);

    public ChartCard AgeChart { get; } = new("age", "마지막 커밋 시점", [Columns, Line, Bars, Table]);

    public ChartCard LinesChart { get; } = new("lines", "변경 줄 수", [Bars, Columns, Table]);

    // 계약: 기간 열쇠는 날 수다 — 「1년」·「2년」만 주 단위로 꽉 차는 날 수(371 · CalendarDays)로 읽는다
    public ChartCard CalendarChart { get; } = new("calendar", "커밋 캘린더",
        [new("calendar", "잔디"), new("month", "달력"), Columns, Line, Table],
        [new("30", "30일"), new("90", "90일"), new("180", "180일"), new("365", "1년"), new("730", "2년")], "365");

    public ChartCard BranchChart { get; } = new("branch", "브랜치 분포", [Donut, Bars, Treemap, Table]);

    /// 계약: 켜진 동안만 카드 사이 나누개가 보이고 끌린다 — 끄는 것은 화면의 「저장」·「취소」다
    [ObservableProperty]
    private bool _isDashboardEditing;

    private void InitDashboardCharts()
    {
        // 계약: 저장소·시점이 줄인 카드는 줄의 Tag(repo: · age:)로 「커밋 내역」을 연다 — 캘린더와 같은 명령이 열쇠 머리로 가른다
        foreach (var card in new[] { CalendarChart, TopChart, LinesChart, AgeChart })
        {
            card.RowCommand = OpenCommitsCommand;
        }

        foreach (var card in new[] { StateChart, GroupChart, ChangeChart, TopChart, AgeChart, BranchChart, LinesChart, CalendarChart })
        {
            card.Restore(Settings.DashboardChartKinds.GetValueOrDefault(card.Key),
                Settings.DashboardChartKinds.GetValueOrDefault(card.Key + RangeSuffix));
            card.KindChanged = changed =>
            {
                Settings.DashboardChartKinds[changed.Key] = changed.Kind;
                if (changed.Ranges.Count > 0)
                {
                    Settings.DashboardChartKinds[changed.Key + RangeSuffix] = changed.Range;
                }

                _ = SaveSettingsAsync();

                // 왜: 캘린더는 기간·표시 방식마다 줄의 수와 묶음(날·주·달)이 다르다 — 고를 때 줄을 다시 짠다
                if (ReferenceEquals(changed, CalendarChart))
                {
                    RefreshCalendar();
                }
            };
        }
    }

    private void RefreshCharts()
    {
        StateChart.Data = new ChartData([],
            [.. States.Select(s => new ChartRow(s.Label, _all.Count(r => r.Severity == s.Severity), s.Tone))]);

        GroupChart.Data = new ChartData(
            [.. States.Select(s => new ChartSeries(s.Label, s.Tone))],
            [.. Sorted(_all).Where(r => r.GroupNode is not null).GroupBy(r => r.GroupNode!)
                .Select(g => new ChartRow(g.Key.Label, [.. States.Select(s => (double)g.Count(r => r.Severity == s.Severity))]))]);

        var read = _all.Where(r => r.Status is not null).ToList();
        ChangeChart.Data = new ChartData([],
        [
            new ChartRow("스테이지", read.Sum(r => r.Status!.Staged)),
            new ChartRow("수정", read.Sum(r => r.Status!.Unstaged)),
            new ChartRow("새 파일", read.Sum(r => r.Status!.Untracked)),
            new ChartRow("충돌", read.Sum(r => r.Status!.Conflicted)),
            new ChartRow("stash", read.Sum(r => r.Status!.StashCount))
        ]);

        TopChart.Data = new ChartData(
            [new("스테이지"), new("수정"), new("새 파일"), new("충돌")],
            [.. read.Select(r => new ChartRow(r.Name, [r.Status!.Staged, r.Status.Unstaged, r.Status.Untracked, r.Status.Conflicted], Tag: RepoTag + r.Id))
                .Where(row => row.Total > 0).OrderByDescending(row => row.Total).Take(8)],
            Mono: true);

        // 왜: 저장소마다 읽어 둔 것은 마지막 커밋 하나뿐이다 — 「얼마나 묵었나」의 분포만 말할 수 있다
        var now = DateTimeOffset.Now;
        var ages = _all.Where(r => r.LastCommitWhen is not null).Select(r => (now - r.LastCommitWhen!.Value).TotalDays).ToList();
        AgeChart.Data = new ChartData([],
        [
            new ChartRow("하루 안", [ages.Count(d => d < 1)], Tag: AgeTag + "0..1"),
            new ChartRow("7일 안", [ages.Count(d => d is >= 1 and < 7)], Tag: AgeTag + "1..7"),
            new ChartRow("30일 안", [ages.Count(d => d is >= 7 and < 30)], Tag: AgeTag + "7..30"),
            new ChartRow("90일 안", [ages.Count(d => d is >= 30 and < 90)], Tag: AgeTag + "30..90"),
            new ChartRow("그 이전", [ages.Count(d => d >= 90)], Tag: AgeTag + "90..100000")
        ], Mono: true);

        // 계약: 범주 색은 여덟뿐이다 — 여덟째 자리부터는 「기타」 한 줄로 접는다
        var branches = read.GroupBy(r => r.BranchText).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ChartRow(g.Key, g.Count())).ToList();
        if (branches.Count > 8)
        {
            branches = [.. branches.Take(7), new ChartRow("기타", branches.Skip(7).Sum(b => b.Total))];
        }

        BranchChart.Data = new ChartData([], branches);

        // 계약: 추가는 녹색·삭제는 적색이다 — diff 단과 같은 뜻 색을 쓴다
        LinesChart.Data = new ChartData(
            [new("추가", "깨끗함"), new("삭제", "충돌")],
            [.. read.Select(r => new ChartRow(r.Name, [r.Status!.Insertions, r.Status.Deletions], Tag: RepoTag + r.Id))
                .Where(row => row.Total > 0).OrderByDescending(row => row.Total).Take(8)]);
        var added = read.Sum(r => r.Status!.Insertions);
        var removed = read.Sum(r => r.Status!.Deletions);
        LinesChart.Caption = added + removed == 0 ? string.Empty : $"+{added:N0} −{removed:N0}줄";

        RefreshCalendar();
    }

    // 계약: 가장 긴 기간(2년 = 105주)을 채우는 날 수다 — ReadCommitDaysAsync 와 캘린더가 같은 값을 쓴다
    private const int CalendarDays = 735;

    // 계약: 기간은 Settings.DashboardChartKinds 에 「카드 이름 + 이 꼬리」 열쇠로 함께 적힌다
    private const string RangeSuffix = ".range";

    /// 계약: 날마다 모든 저장소의 커밋을 더한다 — 누가 넣었는지는 가르지 않고, 지금 브랜치에 있는 것만 센다
    private void RefreshCalendar()
    {
        var perDay = new Dictionary<DateOnly, Dictionary<string, int>>();
        foreach (var row in _all)
        {
            foreach (var day in row.CommitDays)
            {
                if (!perDay.TryGetValue(day, out var repos))
                {
                    perDay[day] = repos = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                repos[row.Name] = repos.GetValueOrDefault(row.Name) + 1;
            }
        }

        ChartRow Span(IEnumerable<DateOnly> span, string label)
        {
            var repos = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var day in span)
            {
                if (perDay.TryGetValue(day, out var one))
                {
                    foreach (var (name, count) in one)
                    {
                        repos[name] = repos.GetValueOrDefault(name) + count;
                    }
                }
            }

            var note = string.Join(" · ", repos.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(6).Select(kv => $"{kv.Key} {kv.Value}"));
            var covered = span.ToList();
            return new ChartRow(label, [repos.Values.Sum()], Note: note, Tag: $"{covered.Min():yyyy-MM-dd}..{covered.Max():yyyy-MM-dd}");
        }

        ChartRow Day(DateOnly day, string format) => Span([day], day.ToString(format));

        var today = DateOnly.FromDateTime(DateTime.Now);
        // 계약: 1년·2년은 주 단위로 꽉 차는 날 수(53주 · 105주)로 읽는다
        var days = CalendarChart.Range switch
        {
            "365" => 371,
            "730" => CalendarDays,
            _ => int.Parse(CalendarChart.Range, System.Globalization.CultureInfo.InvariantCulture)
        };
        var window = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1)).ToList();
        var totals = window.Where(perDay.ContainsKey).ToDictionary(d => d, d => perDay[d].Values.Sum());

        // 왜: 막대·선은 줄이 서른을 넘으면 이름표가 겹친다 — 90일은 주, 그보다 길면 달로 묶는다
        IReadOnlyList<ChartRow> Bars() => days switch
        {
            <= 31 => [.. window.Select(d => Day(d, d.Day == 1 || d == window[0] ? "M/d" : "%d"))],
            <= 92 => [.. window.GroupBy(d => d.AddDays(-(int)d.DayOfWeek)).Select(g => Span(g, g.First().ToString("M/d")))],
            _ => [.. window.GroupBy(d => (d.Year, d.Month)).Select(g => Span(g, g.Key.Month == 1 ? $"{g.Key.Year % 100}년" : $"{g.Key.Month}월"))]
        };

        IReadOnlyList<ChartRow> rows = CalendarChart.Kind switch
        {
            "column" or "line" => Bars(),
            "table" => [.. totals.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).Take(8).Select(kv => Day(kv.Key, "yyyy-MM-dd (ddd)"))],
            _ => [.. window.Select(d => Day(d, "yyyy-MM-dd"))]
        };

        CalendarChart.Data = new ChartData([], rows, Mono: true);
        if (totals.Count == 0)
        {
            CalendarChart.Caption = string.Empty;
            return;
        }

        var best = totals.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First();
        var span = CalendarChart.Ranges.First(r => r.Key == CalendarChart.Range).Label;
        CalendarChart.Caption = $"{span} {totals.Values.Sum():N0}건 · 넣은 날 {totals.Count}일 · 가장 많은 날 {best.Key:M/d} {best.Value}건";
    }
}
