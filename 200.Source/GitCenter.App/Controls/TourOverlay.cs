using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GitCenter.App.Controls;

// 도움말 안내 층 — 화면 요소를 하나씩 비추고(나머지는 어둡게) 그 옆에 설명 쪽지를 띄운다.
// 계약: 이 층은 화면을 고치지 않는다 — 비출 요소와 글은 부르는 쪽(MainWindow.Tour.cs)이 건네고, 여기는 그리기·넘기기만 한다.
// 계약: 넘기기는 「이전」·「다음」 단추와 ←·→·Enter, 닫기는 「닫기」와 Esc 다. 어두운 곳을 눌러도 다음으로 간다.

/// <summary>안내 한 걸음 — Target 이 null 이면 비추는 곳 없이 쪽지만 가운데 띄운다.</summary>
public sealed record TourStep(FrameworkElement? Target, string Title, string Body);

public sealed class TourOverlay : Grid
{
    private const double Gap = 10;
    private const double NoteWidth = 340;

    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)) };
    private readonly Border _ring = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
    private readonly Border _note = new() { Width = NoteWidth, Padding = new Thickness(16, 14, 16, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly TextBlock _count = new() { FontSize = 11 };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private readonly Button _previous = new() { Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _next = new();
    private readonly Button _close = new();

    private IReadOnlyList<TourStep> _steps = [];
    private int _index;

    public TourOverlay()
    {
        _ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _note.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _note.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _previous.SetResourceReference(StyleProperty, "GhostButton");
        _close.SetResourceReference(StyleProperty, "GhostButton");
        _next.SetResourceReference(StyleProperty, "PrimaryButton");

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_close, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_previous, Dock.Right);
        buttons.Children.Add(_close);
        buttons.Children.Add(_next);
        buttons.Children.Add(_previous);

        _note.Child = new StackPanel { Children = { _count, _title, _body, buttons } };
        _note.HorizontalAlignment = HorizontalAlignment.Left;
        _note.VerticalAlignment = VerticalAlignment.Top;
        _ring.HorizontalAlignment = HorizontalAlignment.Left;
        _ring.VerticalAlignment = VerticalAlignment.Top;

        Children.Add(_dim);
        Children.Add(_ring);
        Children.Add(_note);

        Focusable = true;
        FocusVisualStyle = null;

        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _close.Click += (_, _) => Close();
        _dim.MouseLeftButtonDown += (_, e) => { Move(1); e.Handled = true; };
        SizeChanged += (_, _) => Reflow();
        KeyDown += OnKey;
    }

    /// 계약: 단추 글은 부르는 쪽이 번역해 건넨다 — 이 층은 문구를 갖지 않는다
    public void Start(IReadOnlyList<TourStep> steps, string previous, string next, string done, string close)
    {
        if (steps.Count == 0)
        {
            return;
        }

        _steps = steps;
        _index = 0;
        _previous.Content = previous;
        _close.Content = close;
        _nextText = next;
        _doneText = done;
        Visibility = Visibility.Visible;
        Focus();

        // 함정: 펴진 직후에는 크기가 0 이다 — 배치가 끝난 뒤에 자리를 잡는다
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Reflow));
    }

    private string _nextText = string.Empty;
    private string _doneText = string.Empty;

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _steps = [];
    }

    private void Move(int delta)
    {
        var next = _index + delta;
        if (next >= _steps.Count)
        {
            Close();
            return;
        }

        _index = Math.Max(0, next);
        Reflow();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Move(-1); break;
            case Key.Right or Key.Enter or Key.Space: Move(1); break;
            default: return;
        }

        e.Handled = true;
    }

    private void Reflow()
    {
        if (_steps.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var step = _steps[_index];
        _count.Text = $"{_index + 1} / {_steps.Count}";
        _title.Text = step.Title;
        _body.Text = step.Body;
        _previous.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = _index == _steps.Count - 1 ? _doneText : _nextText;

        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        var hole = HoleOf(step.Target, full);

        _dim.Data = hole is { } rect
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(rect, 8, 8))
            : new RectangleGeometry(full);

        _ring.Visibility = hole is null ? Visibility.Collapsed : Visibility.Visible;
        if (hole is { } ring)
        {
            _ring.Margin = new Thickness(ring.X, ring.Y, 0, 0);
            _ring.Width = ring.Width;
            _ring.Height = ring.Height;
        }

        // 함정: DesiredSize 는 여백(Margin)을 포함한다 — 앞 걸음의 자리가 남아 있으면 쪽지가 실제보다 크게 재진다
        _note.Margin = default;
        _note.Measure(new Size(NoteWidth, double.PositiveInfinity));
        var note = _note.DesiredSize;
        var at = Place(hole, note, full);
        _note.Margin = new Thickness(at.X, at.Y, 0, 0);
    }

    private Rect? HoleOf(FrameworkElement? target, Rect full)
    {
        if (target is null || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            return null;
        }

        var rect = target.TransformToVisual(this).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        rect.Inflate(4, 4);
        rect.Intersect(full);
        return rect.IsEmpty ? null : rect;
    }

    // 왜: 쪽지가 비춘 곳을 가리면 안내가 뜻을 잃는다 — 아래·위·오른쪽·왼쪽 차례로 들어갈 자리를 찾고, 없으면 비춘 곳 안쪽 아래에 둔다
    private static Point Place(Rect? hole, Size note, Rect full)
    {
        if (hole is not { } h)
        {
            return new Point((full.Width - note.Width) / 2, (full.Height - note.Height) / 2);
        }

        double ClampX(double x) => Math.Clamp(x, Gap, Math.Max(Gap, full.Width - note.Width - Gap));
        double ClampY(double y) => Math.Clamp(y, Gap, Math.Max(Gap, full.Height - note.Height - Gap));

        if (h.Bottom + Gap + note.Height <= full.Height - Gap)
        {
            return new Point(ClampX(h.Left), h.Bottom + Gap);
        }

        if (h.Top - Gap - note.Height >= Gap)
        {
            return new Point(ClampX(h.Left), h.Top - Gap - note.Height);
        }

        if (h.Right + Gap + note.Width <= full.Width - Gap)
        {
            return new Point(h.Right + Gap, ClampY(h.Top));
        }

        if (h.Left - Gap - note.Width >= Gap)
        {
            return new Point(h.Left - Gap - note.Width, ClampY(h.Top));
        }

        return new Point(ClampX(h.Right - note.Width - Gap * 2), ClampY(h.Bottom - note.Height - Gap * 2));
    }
}
