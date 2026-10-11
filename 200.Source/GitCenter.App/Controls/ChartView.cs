using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Controls;

/// <summary>
/// 대시보드 통계 카드의 그림 — ChartData 한 벌을 Kind 낱말에 따라 아홉 가지로 그린다.
/// 계약: 차트 라이브러리를 쓰지 않는다 — 단일 exe 크기와 테마 색(DynamicResource)을 지키려고 OnRender 로 직접 그린다.
/// 계약: 글자는 늘 글자 색(Ink·Muted)이다 — 계열 색은 옆의 점·막대만 갖는다.
/// 계약: 범주 색 8개는 자리 순서로 고정이고 돌려쓰지 않는다 — 아홉 번째부터는 자료 쪽이 「기타」로 접는다.
/// 함정: 테마가 바뀌어도 OnRender 는 저절로 안 돈다 — 브러시 DP 넷이 DynamicResource 로 묶여 다시 그리게 한다.
/// </summary>
public sealed class ChartView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = Register<ChartData?>(nameof(Data), null);
    public static readonly DependencyProperty KindProperty = Register(nameof(Kind), "hbar");
    public static readonly DependencyProperty InkProperty = Register<Brush>(nameof(Ink), Brushes.Black);
    public static readonly DependencyProperty MutedProperty = Register<Brush>(nameof(Muted), Brushes.Gray);
    public static readonly DependencyProperty TrackProperty = Register<Brush>(nameof(Track), Brushes.LightGray);
    public static readonly DependencyProperty SurfaceProperty = Register<Brush>(nameof(Surface), Brushes.White);
    public static readonly DependencyProperty AccentProperty = Register<Brush>(nameof(Accent), Brushes.SlateBlue);

    private static readonly Color[] LightPalette = Colors("#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948");
    private static readonly Color[] DarkPalette = Colors("#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767");

    public static readonly DependencyProperty RowCommandProperty = Register<ICommand?>(nameof(RowCommand), null);

    private readonly List<(Geometry Area, string Text)> _hits = [];

    // 계약: 누를 수 있는 자리만 따로 적는다 — 줄(ChartRow)을 아는 그림(막대 · 세로 · 달력)만 넣는다
    private readonly List<(Geometry Area, ChartRow Row)> _rowHits = [];
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative };
    private string? _tipText;

    public ChartView()
    {
        _tip.PlacementTarget = this;
        ClipToBounds = true;
    }

    public ChartData? Data { get => (ChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public ICommand? RowCommand { get => (ICommand?)GetValue(RowCommandProperty); set => SetValue(RowCommandProperty, value); }

    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    public Brush Muted { get => (Brush)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }

    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public Brush Surface { get => (Brush)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(ChartView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Color[] Colors(params string[] hex) => [.. hex.Select(h => (Color)ColorConverter.ConvertFromString(h))];

    // ── 그리기 ───────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        _rowHits.Clear();
        var w = ActualWidth;
        var h = ActualHeight;

        // 왜: 빈 자리도 마우스를 받아야 말풍선이 닫힌다
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 60 || h < 40)
        {
            return;
        }

        var data = Data;
        if (data is null || data.Rows.Count == 0 || data.Total <= 0)
        {
            var none = Text("표시할 값이 없습니다", 12, Muted);
            dc.DrawText(none, new Point((w - none.Width) / 2, (h - none.Height) / 2));
            return;
        }

        var area = new Rect(0, 0, w, h);
        switch (Kind)
        {
            case "donut": DrawDonut(dc, data, area); break;
            case "column": DrawColumns(dc, data, area, false); break;
            case "line": DrawColumns(dc, data, area, true); break;
            case "strip": DrawStrip(dc, data, area); break;
            case "treemap": DrawTreemap(dc, data, area); break;
            case "heat": DrawHeat(dc, data, area); break;
            case "gauge": DrawGauge(dc, data, area); break;
            case "table": DrawTable(dc, data, area); break;
            case "calendar": DrawCalendar(dc, data, area); break;
            case "month": DrawMonth(dc, data, area); break;
            default: DrawBars(dc, data, area); break;
        }
    }

    private void DrawDonut(DrawingContext dc, ChartData data, Rect area)
    {
        var legendWide = area.Width >= 300;
        var ringBox = legendWide ? new Rect(0, 0, Math.Min(area.Height, area.Width * 0.42), area.Height) : area;
        var radius = Math.Max(20, Math.Min(ringBox.Width, ringBox.Height) / 2 - 4);
        var center = new Point(ringBox.Left + ringBox.Width / 2, ringBox.Top + ringBox.Height / 2);
        var inner = radius * 0.62;
        var gap = new Pen(Surface, 2);
        var total = data.Total;

        var angle = -90.0;
        for (var i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];
            if (row.Total <= 0)
            {
                continue;
            }

            var sweep = row.Total / total * 360;
            var slice = Ring(center, radius, inner, angle, angle + sweep);
            dc.DrawGeometry(RowBrush(data, i), gap, slice);
            _hits.Add((slice, Tip(row, total)));
            angle += sweep;
        }

        var sum = Text(total.ToString("N0", CultureInfo.CurrentCulture), Math.Clamp(radius * 0.38, 13, 26), Ink, true);
        var caption = Text("합계", 10.5, Muted);
        dc.DrawText(sum, new Point(center.X - sum.Width / 2, center.Y - (sum.Height + caption.Height) / 2));
        dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y - (sum.Height + caption.Height) / 2 + sum.Height));

        if (legendWide)
        {
            DrawLegendList(dc, data, new Rect(ringBox.Right + 14, 0, area.Width - ringBox.Right - 14, area.Height));
        }
    }

    /// 계약: 색점 · 이름 · 값 · 비중 네 칸 — 도넛 옆과 띠 아래와 표가 함께 쓴다
    private void DrawLegendList(DrawingContext dc, ChartData data, Rect box)
    {
        var total = data.Total;
        var rowH = Math.Clamp(box.Height / data.Rows.Count, 17, 24);
        var top = box.Top + Math.Max(0, (box.Height - rowH * data.Rows.Count) / 2);

        for (var i = 0; i < data.Rows.Count && top + rowH <= box.Bottom + 1; i++, top += rowH)
        {
            var row = data.Rows[i];
            var mid = top + rowH / 2;
            dc.DrawEllipse(RowBrush(data, i), null, new Point(box.Left + 4, mid), 3.5, 3.5);

            var pct = Text(total <= 0 ? "0%" : $"{row.Total / total * 100:0}%", 10.5, Muted);
            var value = Text(row.Total.ToString("N0", CultureInfo.CurrentCulture), 12, Ink, true);
            var name = Text(row.Label, 11.5, row.Total > 0 ? Ink : Muted, false, Math.Max(20, box.Width - 14 - 34 - value.Width - 12));
            dc.DrawText(name, new Point(box.Left + 14, mid - name.Height / 2));
            dc.DrawText(value, new Point(box.Right - 34 - value.Width, mid - value.Height / 2));
            dc.DrawText(pct, new Point(box.Right - pct.Width, mid - pct.Height / 2));
            _hits.Add((new RectangleGeometry(new Rect(box.Left, top, box.Width, rowH)), Tip(row, total)));
        }
    }

    private void DrawBars(DrawingContext dc, ChartData data, Rect area)
    {
        var legendH = data.Series.Count > 1 ? 20 : 0;
        var plot = new Rect(area.Left, area.Top, area.Width, area.Height - legendH);
        var rowH = Math.Clamp(plot.Height / data.Rows.Count, 18, 30);
        var labelW = Math.Min(plot.Width * 0.38, data.Rows.Max(r => Text(r.Label, 11.5, Ink).Width) + 10);
        var valueW = data.Rows.Max(r => Text(r.Total.ToString("N0"), 12, Ink, true).Width) + 8;
        var barLeft = plot.Left + labelW;
        var barW = Math.Max(10, plot.Width - labelW - valueW);
        var max = data.Rows.Max(r => r.Total);
        var thick = Math.Min(10, rowH - 8);
        var top = plot.Top;

        for (var i = 0; i < data.Rows.Count && top + rowH <= plot.Bottom + 1; i++, top += rowH)
        {
            var row = data.Rows[i];
            var mid = top + rowH / 2;
            var name = Text(row.Label, 11.5, Ink, false, labelW - 8);
            dc.DrawText(name, new Point(plot.Left, mid - name.Height / 2));
            dc.DrawRoundedRectangle(Track, null, new Rect(barLeft, mid - thick / 2, barW, thick), thick / 2, thick / 2);

            var x = barLeft;
            for (var s = 0; s < row.Values.Count; s++)
            {
                var len = max <= 0 ? 0 : row.Values[s] / max * barW;
                if (len <= 0)
                {
                    continue;
                }

                // 계약: 쌓인 조각 사이는 2px 을 비운다 — 색만으로 경계를 가르지 않는다
                var seg = new Rect(x, mid - thick / 2, Math.Max(1, len - (row.Values.Count > 1 ? 2 : 0)), thick);
                dc.DrawRoundedRectangle(CellBrush(data, i, s), null, seg, 3, 3);
                x += len;
            }

            var value = Text(row.Total.ToString("N0", CultureInfo.CurrentCulture), 12, Ink, true);
            dc.DrawText(value, new Point(plot.Right - value.Width, mid - value.Height / 2));
            Hit(new RectangleGeometry(new Rect(plot.Left, top, plot.Width, rowH)), RowTip(data, row), row);
        }

        DrawSeriesLegend(dc, data, new Rect(area.Left, area.Bottom - legendH, area.Width, legendH));
    }

    private void DrawColumns(DrawingContext dc, ChartData data, Rect area, bool asLine)
    {
        var legendH = data.Series.Count > 1 && !asLine ? 20 : 0;
        var plot = new Rect(area.Left + 4, area.Top + 18, area.Width - 8, area.Height - 18 - 20 - legendH);
        if (plot.Height < 20)
        {
            return;
        }

        var n = data.Rows.Count;
        var slot = plot.Width / n;
        var max = data.Rows.Max(r => r.Total);
        var barW = Math.Clamp(slot * 0.5, 6, 34);
        dc.DrawLine(new Pen(Track, 1), new Point(plot.Left, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));

        var points = new List<Point>();
        for (var i = 0; i < n; i++)
        {
            var row = data.Rows[i];
            var cx = plot.Left + slot * (i + 0.5);
            var top = plot.Bottom - (max <= 0 ? 0 : row.Total / max * plot.Height);
            points.Add(new Point(cx, top));

            if (!asLine)
            {
                var y = plot.Bottom;
                for (var s = 0; s < row.Values.Count; s++)
                {
                    var len = max <= 0 ? 0 : row.Values[s] / max * plot.Height;
                    if (len <= 0)
                    {
                        continue;
                    }

                    var seg = new Rect(cx - barW / 2, y - len, barW, Math.Max(1, len - (row.Values.Count > 1 ? 2 : 0)));
                    dc.DrawRoundedRectangle(CellBrush(data, i, s), null, seg, 3, 3);
                    y -= len;
                }
            }

            var name = Text(row.Label, 10.5, Muted, false, slot - 4);
            dc.DrawText(name, new Point(cx - name.Width / 2, plot.Bottom + 4));
            var value = Text(row.Total.ToString("N0", CultureInfo.CurrentCulture), 11, Ink, true);
            dc.DrawText(value, new Point(cx - value.Width / 2, top - value.Height - (asLine ? 6 : 2)));
            Hit(new RectangleGeometry(new Rect(cx - slot / 2, area.Top, slot, area.Height - legendH)), RowTip(data, row), row);
        }

        if (asLine)
        {
            var stroke = new Pen(Accent, 2) { LineJoin = PenLineJoin.Round };
            for (var i = 1; i < points.Count; i++)
            {
                dc.DrawLine(stroke, points[i - 1], points[i]);
            }

            foreach (var p in points)
            {
                dc.DrawEllipse(Accent, new Pen(Surface, 2), p, 4.5, 4.5);
            }
        }

        DrawSeriesLegend(dc, data, new Rect(area.Left, area.Bottom - legendH, area.Width, legendH));
    }

    private void DrawStrip(DrawingContext dc, ChartData data, Rect area)
    {
        var total = data.Total;
        const double BandH = 16;
        var x = area.Left;
        for (var i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];
            var len = row.Total / total * area.Width;
            if (len <= 0)
            {
                continue;
            }

            var seg = new Rect(x, area.Top + 4, Math.Max(1, len - 2), BandH);
            dc.DrawRoundedRectangle(RowBrush(data, i), null, seg, 3, 3);
            _hits.Add((new RectangleGeometry(seg), Tip(row, total)));
            x += len;
        }

        DrawLegendList(dc, data, new Rect(area.Left, area.Top + BandH + 14, area.Width, area.Height - BandH - 14));
    }

    private void DrawTreemap(DrawingContext dc, ChartData data, Rect area)
    {
        var rows = data.Rows.Select((r, i) => (Row: r, Index: i)).Where(x => x.Row.Total > 0)
            .OrderByDescending(x => x.Row.Total).ToList();
        var max = rows[0].Row.Total;
        var total = data.Total;

        Split(rows, area);

        // 왜: 반씩 가르는 쪽이 한 줄로 썰어 넣는 것보다 칸이 정사각에 가깝게 나온다
        void Split(List<(ChartRow Row, int Index)> part, Rect box)
        {
            if (part.Count == 1)
            {
                var (row, index) = part[0];
                var cell = new Rect(box.Left, box.Top, Math.Max(1, box.Width - 2), Math.Max(1, box.Height - 2));
                dc.DrawRoundedRectangle(Soft(data.Mono ? Accent : RowBrush(data, index), data.Mono ? 0.22 + 0.4 * row.Total / max : 0.42), null, cell, 3, 3);
                _hits.Add((new RectangleGeometry(cell), Tip(row, total)));
                if (cell.Width > 46 && cell.Height > 20)
                {
                    var name = Text(row.Label, 11, Ink, true, cell.Width - 10);
                    dc.DrawText(name, new Point(cell.Left + 6, cell.Top + 4));
                    if (cell.Height > 38)
                    {
                        dc.DrawText(Text(row.Total.ToString("N0", CultureInfo.CurrentCulture), 11, Ink), new Point(cell.Left + 6, cell.Top + 5 + name.Height));
                    }
                }

                return;
            }

            var sum = part.Sum(x => x.Row.Total);
            var head = 0.0;
            var cut = 0;
            while (cut == 0 || (cut < part.Count - 1 && head + part[cut].Row.Total <= sum / 2))
            {
                head += part[cut++].Row.Total;
            }

            var ratio = head / sum;
            var (a, b) = box.Width >= box.Height
                ? (new Rect(box.Left, box.Top, box.Width * ratio, box.Height), new Rect(box.Left + box.Width * ratio, box.Top, box.Width * (1 - ratio), box.Height))
                : (new Rect(box.Left, box.Top, box.Width, box.Height * ratio), new Rect(box.Left, box.Top + box.Height * ratio, box.Width, box.Height * (1 - ratio)));
            Split(part[..cut], a);
            Split(part[cut..], b);
        }
    }

    private void DrawHeat(DrawingContext dc, ChartData data, Rect area)
    {
        var cols = data.Series.Count;
        if (cols == 0)
        {
            return;
        }

        const double HeadH = 20;
        var labelW = Math.Min(area.Width * 0.3, data.Rows.Max(r => Text(r.Label, 11.5, Ink).Width) + 10);
        var cellW = (area.Width - labelW) / cols;
        var cellH = Math.Clamp((area.Height - HeadH) / data.Rows.Count, 18, 34);
        var max = data.Rows.Max(r => r.Values.Max());

        for (var s = 0; s < cols; s++)
        {
            var head = Text(data.Series[s].Name, 10.5, Muted, false, cellW - 4);
            dc.DrawText(head, new Point(area.Left + labelW + cellW * s + (cellW - head.Width) / 2, area.Top + 2));
        }

        var top = area.Top + HeadH;
        for (var i = 0; i < data.Rows.Count && top + cellH <= area.Bottom + 1; i++, top += cellH)
        {
            var row = data.Rows[i];
            var name = Text(row.Label, 11.5, Ink, false, labelW - 8);
            dc.DrawText(name, new Point(area.Left, top + (cellH - name.Height) / 2));

            for (var s = 0; s < cols; s++)
            {
                var v = row.Values[s];
                var cell = new Rect(area.Left + labelW + cellW * s, top, cellW - 2, cellH - 2);

                // 계약: 히트맵은 크기를 한 색의 짙기로만 말한다 — 0 은 빈 바탕이다
                dc.DrawRoundedRectangle(v <= 0 ? Soft(Track, 0.5) : Soft(Accent, 0.18 + 0.55 * v / max), null, cell, 3, 3);
                if (v > 0)
                {
                    var num = Text(v.ToString("N0", CultureInfo.CurrentCulture), 11, Ink, true);
                    dc.DrawText(num, new Point(cell.Left + (cell.Width - num.Width) / 2, cell.Top + (cell.Height - num.Height) / 2));
                }

                _hits.Add((new RectangleGeometry(cell), $"{row.Label} · {data.Series[s].Name}  {v:N0}"));
            }
        }
    }

    /// 계약: 게이지는 「첫 줄 / 전체」 한 값만 말한다 — 대표 상태 분포에서는 깨끗한 저장소의 비율이다
    private void DrawGauge(DrawingContext dc, ChartData data, Rect area)
    {
        var first = data.Rows[0];
        var ratio = first.Total / data.Total;
        var thick = Math.Clamp(Math.Min(area.Width, area.Height) * 0.09, 8, 16);
        var radius = Math.Max(24, Math.Min(area.Width / 2, area.Height / 1.55) - thick);
        var center = new Point(area.Left + area.Width / 2, area.Top + (area.Height + radius * 0.5) / 2);

        const double Start = 150;
        const double Sweep = 240;
        dc.DrawGeometry(null, new Pen(Track, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            Arc(center, radius, Start, Start + Sweep));
        if (ratio > 0)
        {
            dc.DrawGeometry(null, new Pen(RowBrush(data, 0), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(center, radius, Start, Start + Sweep * ratio));
        }

        var pct = Text($"{ratio * 100:0}%", Math.Clamp(radius * 0.42, 15, 30), Ink, true);
        var caption = Text($"{first.Label} {first.Total:N0} / {data.Total:N0}", 11, Muted, false, area.Width - 8);
        dc.DrawText(pct, new Point(center.X - pct.Width / 2, center.Y - pct.Height / 2 - 4));
        dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + pct.Height / 2 - 2));
        _hits.Add((new RectangleGeometry(area), Tip(first, data.Total)));
    }

    private void DrawTable(DrawingContext dc, ChartData data, Rect area)
    {
        if (data.Series.Count <= 1)
        {
            DrawLegendList(dc, data, new Rect(area.Left, area.Top, area.Width, Math.Min(area.Height, data.Rows.Count * 24)));
            return;
        }

        const double HeadH = 20;
        var cols = data.Series.Count + 1;
        var labelW = area.Width * 0.3;
        var colW = (area.Width - labelW) / cols;
        var rowH = Math.Clamp((area.Height - HeadH) / data.Rows.Count, 18, 26);
        for (var s = 0; s < cols; s++)
        {
            var head = Text(s < cols - 1 ? data.Series[s].Name : "합계", 10.5, Muted, false, colW - 4);
            dc.DrawText(head, new Point(area.Left + labelW + colW * (s + 1) - head.Width - 2, area.Top + 2));
        }

        dc.DrawLine(new Pen(Track, 1), new Point(area.Left, area.Top + HeadH - 0.5), new Point(area.Right, area.Top + HeadH - 0.5));
        var top = area.Top + HeadH;
        for (var i = 0; i < data.Rows.Count && top + rowH <= area.Bottom + 1; i++, top += rowH)
        {
            var row = data.Rows[i];
            var name = Text(row.Label, 11.5, Ink, false, labelW - 8);
            dc.DrawText(name, new Point(area.Left, top + (rowH - name.Height) / 2));
            for (var s = 0; s < cols; s++)
            {
                var v = s < cols - 1 ? row.Values[s] : row.Total;
                var num = Text(v.ToString("N0", CultureInfo.CurrentCulture), 11.5, v > 0 ? Ink : Muted, s == cols - 1);
                dc.DrawText(num, new Point(area.Left + labelW + colW * (s + 1) - num.Width - 2, top + (rowH - num.Height) / 2));
            }
        }
    }

    // ── 달력 ─────────────────────────────────────────────────
    // 계약: 달력 두 가지는 줄 이름이 yyyy-MM-dd 인 날짜순 자료만 받는다 — 마지막 줄이 오늘이다.

    private static List<(DateOnly Date, ChartRow Row)> Days(ChartData data) =>
        [.. data.Rows.Select(r => (DateOnly.ParseExact(r.Label, "yyyy-MM-dd", CultureInfo.InvariantCulture), r))];

    /// 왜: 하루가 유난히 많으면 나머지가 전부 가장 옅은 칸이 된다 — 제곱근으로 눌러 네 단으로 가른다
    private Brush Level(double value, double max) => value <= 0
        ? Soft(Track, 0.6)
        : Soft(Accent, 0.2 + 0.8 * Math.Ceiling(Math.Sqrt(value / max) * 4) / 4);

    private void DrawCalendar(DrawingContext dc, ChartData data, Rect area)
    {
        const double LabelW = 22;
        const double HeadH = 16;
        const double LegendH = 20;
        var days = Days(data);
        var last = days[^1].Date;

        // 계약: 한 열이 한 주이고 주는 일요일에 시작한다 — 폭이 모자라면 가까운 주부터 들어가는 만큼만 그린다
        var start = days[0].Date.AddDays(-(int)days[0].Date.DayOfWeek);
        var weeks = (last.DayNumber - start.DayNumber) / 7 + 1;
        // 왜: 정사각 칸은 주가 많을수록 작아져 카드 아래가 빈다 — 높이는 늘 카드를 채우고, 폭은 자리가 모자랄 때만 줄인다
        var cell = Math.Clamp((area.Height - HeadH - LegendH) / 7, 8, 40);
        var cellW = Math.Clamp((area.Width - LabelW) / weeks, 5, cell);
        var fit = Math.Max(1, (int)((area.Width - LabelW) / cellW));
        if (fit < weeks)
        {
            start = start.AddDays((weeks - fit) * 7);
            weeks = fit;
        }

        var max = data.Rows.Max(r => r.Total);
        var left = area.Left + LabelW;
        var top = area.Top + HeadH;
        foreach (var (name, index) in new[] { ("월", 1), ("수", 3), ("금", 5) })
        {
            var label = Text(name, 10, Muted);
            dc.DrawText(label, new Point(area.Left, top + index * cell + (cell - 2 - label.Height) / 2));
        }

        foreach (var (date, row) in days)
        {
            if (date < start)
            {
                continue;
            }

            var x = left + (date.DayNumber - start.DayNumber) / 7 * cellW;
            var box = new Rect(x, top + (int)date.DayOfWeek * cell, Math.Max(2, cellW - 2), cell - 2);
            dc.DrawRoundedRectangle(Level(row.Total, max), null, box, 2, 2);
            Hit(new RectangleGeometry(box), Noted($"{date:yyyy-MM-dd (ddd)}  {row.Total:N0}건", row), row);
            // 왜: 짧은 기간에는 1일이 안 들어올 수 있다 — 첫 칸에도 달 이름을 적는다(1일 열과 붙으면 겹치니 뺀다)
            if (date.Day == 1 || (date == days[0].Date && date.Day < 22))
            {
                dc.DrawText(Text($"{date.Month}월", 10, Muted), new Point(x, area.Top));
            }
        }

        var lx = left;
        var ly = top + 7 * cell + 5;
        var few = Text("적음", 10, Muted);
        dc.DrawText(few, new Point(lx, ly));
        lx += few.Width + 6;
        for (var step = 0; step <= 4; step++, lx += 13)
        {
            dc.DrawRoundedRectangle(Level(step / 4.0 * step / 4.0, 1), null, new Rect(lx, ly + 1, 11, 11), 2, 2);
        }

        dc.DrawText(Text("많음", 10, Muted), new Point(lx + 4, ly));
    }

    private void DrawMonth(DrawingContext dc, ChartData data, Rect area)
    {
        const double HeadH = 16;
        var days = Days(data);
        var today = days[^1].Date;

        // 계약: 한 줄이 한 주(일요일 시작)이고 받은 기간을 통째로 그린다 — 마지막 칸이 오늘이다
        var start = days[0].Date.AddDays(-(int)days[0].Date.DayOfWeek);
        var lines = (today.DayNumber - start.DayNumber) / 7 + 1;
        var cellW = area.Width / 7;
        var cellH = (area.Height - HeadH) / lines;

        // 왜: 줄이 많아 칸이 날짜 글자보다 낮아지면 읽을 수 없다 — 그때는 잔디 판으로 그린다
        if (cellH < 16)
        {
            DrawCalendar(dc, data, area);
            return;
        }

        var max = Math.Max(1, data.Rows.Max(r => r.Total));
        string[] names = ["일", "월", "화", "수", "목", "금", "토"];
        for (var i = 0; i < 7; i++)
        {
            var name = Text(names[i], 10, Muted);
            dc.DrawText(name, new Point(area.Left + cellW * i + (cellW - 2 - name.Width) / 2, area.Top));
        }

        foreach (var (date, row) in days)
        {
            var slot = date.DayNumber - start.DayNumber;
            var box = new Rect(area.Left + slot % 7 * cellW, area.Top + HeadH + slot / 7 * cellH, cellW - 2, cellH - 2);
            var value = row.Total;
            dc.DrawRoundedRectangle(Level(value, max), null, box, 3, 3);

            // 왜: 기간이 달을 넘나든다 — 첫 칸과 매달 1일에만 달을 붙여 어느 달인지 알린다
            var label = date.Day == 1 || date == days[0].Date ? $"{date.Month}/{date.Day}" : date.Day.ToString(CultureInfo.InvariantCulture);
            dc.DrawText(Text(label, 10, Ink), new Point(box.Left + 5, box.Top + (box.Height > 22 ? 3 : (box.Height - 13) / 2)));
            if (date == today)
            {
                dc.DrawRoundedRectangle(null, new Pen(Ink, 1), box, 3, 3);
            }

            if (value > 0)
            {
                var tall = box.Height > 30;
                var num = Text(value.ToString("N0", CultureInfo.CurrentCulture), tall ? Math.Clamp(box.Height * 0.3, 11, 16) : 11, Ink, true);
                dc.DrawText(num, tall
                    ? new Point(box.Left + (box.Width - num.Width) / 2, box.Top + (box.Height - num.Height) / 2 + 4)
                    : new Point(box.Right - num.Width - 6, box.Top + (box.Height - num.Height) / 2));
            }

            Hit(new RectangleGeometry(box), Noted($"{date:yyyy-MM-dd (ddd)}  {value:N0}건", row), row);
        }
    }

    private void DrawSeriesLegend(DrawingContext dc, ChartData data, Rect box)
    {
        if (data.Series.Count <= 1 || box.Height <= 0)
        {
            return;
        }

        var x = box.Left;
        var mid = box.Top + box.Height / 2 + 2;
        for (var s = 0; s < data.Series.Count; s++)
        {
            var name = Text(data.Series[s].Name, 10.5, Muted);
            if (x + 12 + name.Width > box.Right)
            {
                break;
            }

            dc.DrawEllipse(SeriesBrush(data, s), null, new Point(x + 4, mid), 3.5, 3.5);
            dc.DrawText(name, new Point(x + 12, mid - name.Height / 2));
            x += 12 + name.Width + 14;
        }
    }

    // ── 색 ───────────────────────────────────────────────────

    private Brush Category(int index)
    {
        // 왜: 어두운 바탕에서는 같은 색상의 한 단 밝은 걸음이 읽힌다 — 바탕 밝기로 벌을 고른다
        var dark = Surface is SolidColorBrush s && (0.299 * s.Color.R + 0.587 * s.Color.G + 0.114 * s.Color.B) < 128;
        var palette = dark ? DarkPalette : LightPalette;
        return index < palette.Length ? new SolidColorBrush(palette[index]) : Muted;
    }

    private static Brush Tone(string tone) => (Brush)new StateBrushConverter().Convert(tone, typeof(Brush), null, CultureInfo.InvariantCulture);

    private Brush RowBrush(ChartData data, int row) =>
        data.Rows[row].Tone.Length > 0 ? Tone(data.Rows[row].Tone) : data.Mono ? Accent : Category(row);

    private Brush SeriesBrush(ChartData data, int series) =>
        data.Series[series].Tone.Length > 0 ? Tone(data.Series[series].Tone) : Category(series);

    private Brush CellBrush(ChartData data, int row, int series) =>
        data.Series.Count > 1 ? SeriesBrush(data, series) : RowBrush(data, row);

    private static Brush Soft(Brush brush, double opacity)
    {
        var soft = brush.Clone();
        soft.Opacity = Math.Clamp(opacity, 0, 1);
        return soft;
    }

    // ── 도형 · 글자 ──────────────────────────────────────────

    private FormattedText Text(string text, double size, Brush brush, bool bold = false, double maxWidth = 0)
    {
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0)
        {
            formatted.MaxTextWidth = Math.Max(8, maxWidth);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        return formatted;
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private static Geometry Arc(Point center, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(center, radius, from), false, false);
            ctx.ArcTo(Polar(center, radius, to), new Size(radius, radius), 0, to - from > 180, SweepDirection.Clockwise, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry Ring(Point center, double outer, double inner, double from, double to)
    {
        // 함정: 시작점과 끝점이 같은 호는 WPF 가 아무것도 안 그린다 — 한 조각이 전부일 때는 원 둘의 차로 만든다
        if (to - from >= 359.99)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outer, outer), new EllipseGeometry(center, inner, inner));
        }

        var large = to - from > 180;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(center, outer, from), true, true);
            ctx.ArcTo(Polar(center, outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Polar(center, inner, to), true, true);
            ctx.ArcTo(Polar(center, inner, from), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    // ── 말풍선 ───────────────────────────────────────────────

    private static string Noted(string head, ChartRow row) => row.Note.Length == 0 ? head : head + Environment.NewLine + row.Note;

    private static string Tip(ChartRow row, double total) => Noted(
        total <= 0 ? $"{row.Label}  {row.Total:N0}" : $"{row.Label}  {row.Total:N0}  ({row.Total / total * 100:0.#}%)", row);

    private static string RowTip(ChartData data, ChartRow row) => data.Series.Count <= 1
        ? Noted($"{row.Label}  {row.Total:N0}", row)
        : $"{row.Label}  {row.Total:N0}" + Environment.NewLine +
          string.Join(" · ", data.Series.Select((s, i) => (s.Name, Value: row.Values[i])).Where(x => x.Value > 0).Select(x => $"{x.Name} {x.Value:N0}"));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        Cursor = RowCommand is not null && RowAt(at) is { Total: > 0 } ? Cursors.Hand : null;
        string? text = null;
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Area.FillContains(at))
            {
                text = _hits[i].Text;
                break;
            }
        }

        if (text is null)
        {
            _tip.IsOpen = false;
            _tipText = null;
            return;
        }

        _tip.HorizontalOffset = at.X + 14;
        _tip.VerticalOffset = at.Y + 16;
        if (text != _tipText)
        {
            _tip.Content = _tipText = text;
        }

        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _tip.IsOpen = false;
        _tipText = null;
        Cursor = null;
    }

    private void Hit(Geometry area, string text, ChartRow row)
    {
        _hits.Add((area, text));
        _rowHits.Add((area, row));
    }

    private ChartRow? RowAt(Point at)
    {
        for (var i = _rowHits.Count - 1; i >= 0; i--)
        {
            if (_rowHits[i].Area.FillContains(at))
            {
                return _rowHits[i].Row;
            }
        }

        return null;
    }

    /// 계약: 값이 있는 줄만 눌린다 — 0 건인 칸은 건너가 봐야 빈 화면이다
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (RowCommand is { } command && RowAt(e.GetPosition(this)) is { Total: > 0 } row && command.CanExecute(row))
        {
            _tip.IsOpen = false;
            command.Execute(row);
            e.Handled = true;
        }
    }
}
