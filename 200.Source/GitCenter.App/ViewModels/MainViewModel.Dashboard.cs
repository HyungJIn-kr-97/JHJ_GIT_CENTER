using CommunityToolkit.Mvvm.ComponentModel;

namespace GitCenter.App.ViewModels;

// 대시보드 통계 카드 — 자료는 이미 읽어 둔 상태(_all)에서만 센다. git 을 더 부르지 않는다.
// 계약: 카드 이름(state · group · change · top · age · branch · lines · calendar · ext · extrepo · files · active · weekday · author · hour · disk)은 Settings.DashboardChartKinds 의 열쇠다 — 바꾸면 저장값을 잃는다.
// 계약: 통계 칸은 일곱(Slots)이고 고를 수 있는 통계는 그보다 많다 — 어느 칸에 무엇을 올렸는지는 같은 사전에 「slot.<자리>」 열쇠로 적힌다.
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

    public ChartCard ExtChart { get; } = new("ext", "소스 확장자 구성", [Donut, Bars, Treemap, Table]);

    public ChartCard ExtRepoChart { get; } = new("extrepo", "저장소별 확장자", [Bars, Heat, Columns, Table]);

    public ChartCard FilesChart { get; } = new("files", "저장소 크기(추적 파일)", [Bars, Treemap, Columns, Table]);

    public ChartCard ActiveChart { get; } = new("active", "최근 30일 커밋 많은 저장소", [Bars, Columns, Treemap, Table]);

    public ChartCard WeekdayChart { get; } = new("weekday", "요일별 커밋", [Columns, Bars, Line, Table]);

    public ChartCard AuthorChart { get; } = new("author", "작성자별 커밋", [Donut, Bars, Treemap, Table]);

    public ChartCard HourChart { get; } = new("hour", "시간대별 커밋", [Columns, Line, Bars, Table]);

    public ChartCard DiskChart { get; } = new("disk", "저장소 용량(.git · MB)", [Bars, Treemap, Columns, Table]);

    /// 계약: 화면의 통계 칸 일곱(첫 줄 셋 · 둘째 줄 넷)에 지금 올라 있는 카드다 — 자리 번호가 XAML 의 Slots[n] 과 짝이다
    public System.Collections.ObjectModel.ObservableCollection<ChartCard> Slots { get; } = [];

    private const string SlotPrefix = "slot.";

    private ChartCard[] AllCharts =>
    [
        StateChart, ChangeChart, ExtChart, CalendarChart, TopChart, LinesChart, AgeChart,
        ExtRepoChart, FilesChart, ActiveChart, WeekdayChart, AuthorChart, HourChart, DiskChart, BranchChart
    ];

    /// 계약: 켜진 동안만 카드 사이 나누개가 보이고 끌린다 — 끄는 것은 화면의 「저장」·「취소」다
    [ObservableProperty]
    private bool _isDashboardEditing;

    private void InitDashboardCharts()
    {
        // 계약: 저장소·시점이 줄인 카드는 줄의 Tag(repo: · age:)로 「커밋 내역」을 연다 — 캘린더와 같은 명령이 열쇠 머리로 가른다
        foreach (var card in new[] { CalendarChart, TopChart, LinesChart, AgeChart, ExtRepoChart, FilesChart, ActiveChart, DiskChart })
        {
            card.RowCommand = OpenCommitsCommand;
        }

        RestoreSlots();

        foreach (var card in AllCharts.Append(GroupChart))
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

    // 계약: 저장된 자리가 모르는 이름이거나 겹치면 버리고, 빈 자리는 AllCharts 차례로 채운다(앞 일곱이 처음 배치다)
    private void RestoreSlots()
    {
        var all = AllCharts;
        var picked = new ChartCard?[7];
        for (var i = 0; i < picked.Length; i++)
        {
            var saved = Settings.DashboardChartKinds.GetValueOrDefault(SlotPrefix + i);
            var card = all.FirstOrDefault(c => c.Key == saved);
            picked[i] = card is not null && !picked.Contains(card) ? card : null;
        }

        Slots.Clear();
        foreach (var card in picked)
        {
            Slots.Add(card ?? all.First(c => !picked.Contains(c) && !Slots.Contains(c)));
        }

        foreach (var card in all)
        {
            card.Choices = all;
            card.PickRequested = SwapSlot;
        }
    }

    /// 계약: 고른 통계가 이미 다른 칸에 있으면 두 칸을 맞바꾼다 — 같은 통계가 두 칸에 오르지 않는다
    private void SwapSlot(ChartCard current, ChartCard target)
    {
        // 왜: 고르개가 고른 줄을 적는 도중에 그 칸의 내용을 갈면 고르개가 닫히며 옛 값을 되적는다 — 한 박자 늦춘다
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var at = Slots.IndexOf(current);
            if (at < 0)
            {
                return;
            }

            var other = Slots.IndexOf(target);
            if (other >= 0)
            {
                Slots[other] = current;
            }

            Slots[at] = target;
            for (var i = 0; i < Slots.Count; i++)
            {
                Settings.DashboardChartKinds[SlotPrefix + i] = Slots[i].Key;
            }

            _ = SaveSettingsAsync();
        });
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

        RefreshFileCharts();
        RefreshCommitCharts();

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

    /// 계약: 추적 중인 파일만 센다 — 「상태 새로 고침」 전에는 비어 있다
    private void RefreshFileCharts()
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (extension, count) in _all.SelectMany(r => r.FileExtensions))
        {
            var label = extension.Length == 0 ? NoExtension : extension;
            totals[label] = totals.GetValueOrDefault(label) + count;
        }

        var ranked = totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var files = ranked.Sum(kv => kv.Value);

        // 계약: 범주 색은 여덟뿐이다 — 여덟째 자리부터는 「기타」 한 줄로 접는다
        var slices = ranked.Select(kv => new ChartRow(kv.Key, kv.Value)).ToList();
        if (slices.Count > 8)
        {
            var rest = ranked.Skip(7).ToList();
            var note = string.Join(" · ", rest.Take(8).Select(kv => $"{kv.Key} {kv.Value:N0}"));
            slices = [.. slices.Take(7), new ChartRow("기타", [rest.Sum(kv => kv.Value)], Note: note)];
        }

        ExtChart.Data = new ChartData([], slices);
        ExtChart.Caption = files == 0 ? string.Empty : $"추적 파일 {files:N0}개 · 확장자 {ranked.Count}종";

        var biggest = _all.Where(r => r.FileExtensions.Count > 0)
            .Select(r => (Row: r, Files: r.FileExtensions.Values.Sum()))
            .OrderByDescending(x => x.Files).ThenBy(x => x.Row.Name, StringComparer.Ordinal).Take(8).ToList();

        // 계약: 저장소별 확장자의 계열은 전체에서 많은 다섯 + 「기타」다 — 저장소마다 계열이 달라지면 색을 견줄 수 없다
        var top = ranked.Take(5).Select(kv => kv.Key).ToList();
        int Count(RepoRow row, string label) => row.FileExtensions.GetValueOrDefault(label == NoExtension ? string.Empty : label);
        ExtRepoChart.Data = new ChartData(
            [.. top.Select(label => new ChartSeries(label)), new ChartSeries("기타")],
            [.. biggest.Select(x => new ChartRow(x.Row.Name,
                [.. top.Select(label => (double)Count(x.Row, label)), x.Files - top.Sum(label => Count(x.Row, label))],
                Tag: RepoTag + x.Row.Id))]);

        FilesChart.Data = new ChartData([],
            [.. biggest.Select(x => new ChartRow(x.Row.Name, [x.Files], Tag: RepoTag + x.Row.Id))], Mono: true);
        FilesChart.Caption = files == 0 ? string.Empty : $"추적 파일 {files:N0}개";
    }

    /// 계약: 이미 읽어 둔 커밋 날짜(CommitDays · 최근 2년)만 센다 — 지금 브랜치의 커밋이고 작성자를 가르지 않는다
    private void RefreshCommitCharts()
    {
        var since = DateOnly.FromDateTime(DateTime.Now).AddDays(-29);
        ActiveChart.Data = new ChartData([],
            [.. _all.Select(r => new ChartRow(r.Name, [r.CommitDays.Count(d => d >= since)], Tag: RepoTag + r.Id))
                .Where(row => row.Total > 0).OrderByDescending(row => row.Total).Take(8)], Mono: true);
        var recent = _all.Sum(r => r.CommitDays.Count(d => d >= since));
        ActiveChart.Caption = recent == 0 ? string.Empty : $"{recent:N0}건";

        // 계약: 월요일부터 센다 — 커밋 캘린더의 줄 차례와 같다
        DayOfWeek[] week = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        var perDay = _all.SelectMany(r => r.CommitDays).GroupBy(d => d.DayOfWeek).ToDictionary(g => g.Key, g => g.Count());
        var names = System.Globalization.CultureInfo.CurrentUICulture.DateTimeFormat;
        WeekdayChart.Data = new ChartData([],
            [.. week.Select(day => new ChartRow(names.GetAbbreviatedDayName(day), [perDay.GetValueOrDefault(day)]))], Mono: true);
        WeekdayChart.Caption = perDay.Count == 0 ? string.Empty : "최근 2년";

        // 계약: 작성자는 이름 그대로 센다 — 같은 사람이 이름을 달리 쓴 커밋은 다른 줄이 된다
        var authors = _all.SelectMany(r => r.CommitFacts.Authors).GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(g => new ChartRow(g.Key, g.Sum(kv => kv.Value)))
            .OrderByDescending(row => row.Total).ThenBy(row => row.Label, StringComparer.Ordinal).ToList();
        var people = authors.Count;
        if (authors.Count > 8)
        {
            authors = [.. authors.Take(7), new ChartRow("기타", authors.Skip(7).Sum(a => a.Total))];
        }

        AuthorChart.Data = new ChartData([], authors);
        AuthorChart.Caption = people == 0 ? string.Empty : $"최근 2년 · {people}명";

        // 왜: 스물네 칸은 이름표가 겹친다 — 두 시간씩 묶어 열두 줄로 낸다
        var hours = new int[24];
        foreach (var row in _all)
        {
            for (var h = 0; h < 24; h++)
            {
                hours[h] += row.CommitFacts.Hours[h];
            }
        }

        HourChart.Data = new ChartData([],
            [.. Enumerable.Range(0, 12).Select(i => new ChartRow($"{i * 2}시", [hours[i * 2] + hours[i * 2 + 1]]))], Mono: true);
        var peak = Array.IndexOf(hours, hours.Max());
        HourChart.Caption = hours.Sum() == 0 ? string.Empty : $"최근 2년 · 가장 많은 때 {peak}시";

        var sized = _all.Where(r => r.ObjectSizeKib > 0).OrderByDescending(r => r.ObjectSizeKib).ToList();
        DiskChart.Data = new ChartData([],
            [.. sized.Take(8).Select(r => new ChartRow(r.Name, [Math.Round(r.ObjectSizeKib / 1024d, 1)], Tag: RepoTag + r.Id))], Mono: true);
        DiskChart.Caption = sized.Count == 0 ? string.Empty : $"합계 {sized.Sum(r => r.ObjectSizeKib) / 1024d:N0} MB";
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
