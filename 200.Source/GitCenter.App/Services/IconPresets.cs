using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jhj.Core.Wpf.Shell;

namespace GitCenter.App.Services;

/// <summary>
/// 이 앱이 코드로 그리는 아이콘 모양 — 설정 「아이콘」에서 내장 .ico 대신 고를 수 있다.
/// 계약: 100×100 좌표로 그리고 요청받은 픽셀 수로 늘린다 — 크기마다 따로 그려져 흐려지지 않는다.
/// 계약: 24px 이하는 같은 그림을 고리 대신 채운 점·굵은 획으로 그린다 — 모양을 바꾸지 않고 굵기만 올린다.
/// 계약: 「가지 모양」은 함께 실은 app-icon-glyph.ico 다(070.아이콘-규격 §1 「두 그림」).
/// 규격 — JHJ_DEV/000.AGENTS_MD/080.CS앱-화면/080.CS앱-화면-공통.md §2
/// </summary>
internal static class IconPresets
{
    public const string Connect = "connect";
    public const string Network = "network";

    public const string Glyph = "glyph";
    public const string GoldJ = "gold-j";
    public const string Classic = "classic";

    // 계약: 기본 그림(app-icon.ico)은 금박 JG 다 — 모든 크기에 두 글자다(make_jhj_icon.py --style foil --mono JG)
    // 계약: 「JHJ + 가지」는 금박으로 바꾸기 전의 기본 그림이다 — 고르던 사람이 잃지 않게 선택지로 남긴다
    public static AppIconPreset[] All =>
    [
        AppIcon.IcoPreset(GoldJ, "금박 J", "pack://application:,,,/Resources/app-icon-gold-j.ico"),
        AppIcon.IcoPreset(Classic, "JHJ + 가지", "pack://application:,,,/Resources/app-icon-classic.ico"),
        AppIcon.IcoPreset(Glyph, "가지 모양", "pack://application:,,,/Resources/app-icon-glyph.ico"),
        new AppIconPreset(Connect, "연결", px => Draw(px, DrawConnect)),
        new AppIconPreset(Network, "그물", px => Draw(px, DrawNetwork)),
    ];

    private static readonly Brush Ink = Frozen(new SolidColorBrush(Colors.White));

    private static BitmapSource Draw(int pixels, Action<DrawingContext, bool> glyph)
    {
        pixels = Math.Max(16, pixels);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 100.0, pixels / 100.0));

            // 계약: 바탕은 violet 팔레트의 밝은 단 → 어두운 단이다 — 내장 아이콘(make_jhj_icon.py --palette violet)과 한 가족으로 보인다
            var body = Frozen(new LinearGradientBrush(
                Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x5B, 0x21, 0xB6), 90));
            dc.DrawRoundedRectangle(body, null, new Rect(0, 0, 100, 100), 23, 23);

            var sheen = Frozen(new LinearGradientBrush(
                Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90));
            dc.DrawRoundedRectangle(sheen, null, new Rect(0, 0, 100, 54), 23, 23);

            // 왜: 밝은 안쪽 테 한 줄이 어두운 작업 표시줄·창 머리 위에서 윤곽을 또렷하게 세운다
            var rim = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 2);
            rim.Freeze();
            dc.DrawRoundedRectangle(null, rim, new Rect(1, 1, 98, 98), 22, 22);

            glyph(dc, pixels <= 24);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // 계약: 점 색은 셋뿐이다 — 금(기준 줄기) · 하늘(들어오는 가지) · 분홍(셋째 점). 바탕이 보라라 셋 다 밝은 단을 쓴다
    private static readonly Color Gold = Color.FromRgb(0xFC, 0xD3, 0x4D);
    private static readonly Color Sky = Color.FromRgb(0x67, 0xE8, 0xF9);
    private static readonly Color Rose = Color.FromRgb(0xF9, 0xA8, 0xD4);

    /// 줄기 하나에 가지 하나가 휘어 들어온다 — 줄기는 흰 실선, 가지는 하늘색에서 흰색으로 번진다
    private static void DrawConnect(DrawingContext dc, bool small)
    {
        var top = small ? new Point(31, 24) : new Point(33, 27);
        var bottom = small ? new Point(31, 76) : new Point(33, 73);
        var side = small ? new Point(70, 30) : new Point(68, 32);
        var width = small ? 10 : 6;

        // 왜: 가지 줄의 색이 점에서 줄기로 옮겨 가야 「어디서 와서 어디로 합쳐지나」가 읽힌다
        var flow = Frozen(new LinearGradientBrush(Sky, Colors.White, new Point(1, 0), new Point(0, 1)));
        dc.DrawGeometry(null, Pen(width, flow), Curve(side, new Point(side.X, 58), new Point(top.X, 46), new Point(top.X, 64)));
        dc.DrawLine(Pen(width), top, bottom);

        var radius = small ? 12 : 10;
        Node(dc, top, radius, Colors.White, hollow: !small);
        Node(dc, bottom, radius, Gold, hollow: false);
        Node(dc, side, radius, Sky, hollow: false);
    }

    /// 점 셋이 서로 이어진다 — 두 변은 실선, 밑변은 점선이라 「묶였지만 따로 움직인다」로 읽힌다
    private static void DrawNetwork(DrawingContext dc, bool small)
    {
        var top = new Point(50, 26);
        var left = new Point(25, 70);
        var right = new Point(75, 70);
        var width = small ? 8 : 5;

        var soft = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)));
        dc.DrawLine(Pen(width, soft), top, left);
        dc.DrawLine(Pen(width, soft), top, right);

        // 함정: 작은 판에서 점선은 점 사이가 뭉개져 얼룩으로 보인다 — 24px 이하는 실선으로 그린다
        var baseLine = Pen(width, soft);
        if (!small)
        {
            baseLine = Pen(width, soft, new DashStyle([0.1, 2.1], 0));
        }

        dc.DrawLine(baseLine, left, right);

        var radius = small ? 13 : 10.5;
        Node(dc, top, radius, Gold, hollow: false);
        Node(dc, left, radius, Sky, hollow: false);
        Node(dc, right, radius, Rose, hollow: false);
    }

    /// 흰 테를 두른 점 하나. hollow 면 속을 바탕색으로 비워 고리로 보인다
    private static void Node(DrawingContext dc, Point center, double radius, Color color, bool hollow)
    {
        dc.DrawEllipse(Ink, null, center, radius, radius);
        var inner = radius * (hollow ? 0.52 : 0.68);
        var fill = hollow ? Color.FromRgb(0x7C, 0x4D, 0xE8) : color;
        dc.DrawEllipse(Frozen(new SolidColorBrush(fill)), null, center, inner, inner);
    }

    private static Pen Pen(double thickness, Brush? brush = null, DashStyle? dash = null)
    {
        var pen = new Pen(brush ?? Ink, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (dash is not null)
        {
            pen.DashStyle = dash;
        }

        pen.Freeze();
        return pen;
    }

    private static Geometry Curve(Point start, Point c1, Point c2, Point end)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.BezierTo(c1, c2, end, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static T Frozen<T>(T brush)
        where T : Brush
    {
        brush.Freeze();
        return brush;
    }
}
