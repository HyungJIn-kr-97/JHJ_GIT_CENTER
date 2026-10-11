using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GitCenter.App.Controls;

/// <summary>
/// 「다음에 누를 단추」를 깜빡이는 테로 가리킨다 — 일괄 작업이 실패했을 때 무엇을 눌러야 하는지 눈으로 찾게 한다.
/// 계약: 단추에 Key(이 단추의 이름)와 Active(뷰모델의 MainViewModel.NudgeAction)를 함께 단다 — 둘이 같을 때만 깜빡인다.
/// 계약: 테는 덧그림(Adorner)이라 배치를 밀지 않고 누름도 가로채지 않는다 — 단추의 모양·크기는 그대로다.
/// </summary>
public static class Nudge
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Nudge), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(string), typeof(Nudge), new PropertyMetadata(string.Empty, OnChanged));

    private static readonly DependencyProperty RingProperty = DependencyProperty.RegisterAttached(
        "Ring", typeof(RingAdorner), typeof(Nudge));

    public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    public static string GetActive(DependencyObject element) => (string)element.GetValue(ActiveProperty);

    public static void SetActive(DependencyObject element, string value) => element.SetValue(ActiveProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var key = GetKey(element);
        if (key.Length > 0 && string.Equals(key, GetActive(element), StringComparison.Ordinal))
        {
            Show(element);
        }
        else
        {
            Hide(element);
        }
    }

    private static void Show(FrameworkElement element)
    {
        if (element.GetValue(RingProperty) is not null)
        {
            return;
        }

        // 함정: 화면에 오르기 전에는 덧그림 층이 없다 — 올라온 뒤에 한 번 더 건다
        if (AdornerLayer.GetAdornerLayer(element) is not { } layer)
        {
            element.Loaded += OnLoadedOnce;
            return;
        }

        var ring = new RingAdorner(element);
        layer.Add(ring);
        element.SetValue(RingProperty, ring);
        // 함정: 단추가 든 단이 접혀도(Collapsed) 덧그림 층은 테를 그대로 그린다 — 보임을 따라 테도 숨긴다
        ring.Visibility = element.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        element.IsVisibleChanged += OnVisibleChanged;
        ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1.0, TimeSpan.FromSeconds(0.65))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnLoadedOnce;
        OnChanged(element, default);
    }

    private static void Hide(FrameworkElement element)
    {
        element.Loaded -= OnLoadedOnce;
        if (element.GetValue(RingProperty) is not RingAdorner ring)
        {
            return;
        }

        element.IsVisibleChanged -= OnVisibleChanged;
        ring.BeginAnimation(UIElement.OpacityProperty, null);
        AdornerLayer.GetAdornerLayer(element)?.Remove(ring);
        element.ClearValue(RingProperty);
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.GetValue(RingProperty) is RingAdorner ring)
        {
            ring.Visibility = element.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private sealed class RingAdorner : Adorner
    {
        public RingAdorner(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            // 왜: 강조 단추가 이미 강조색이라 같은 색 테는 묻힌다 — 경고색으로 그린다
            var color = (TryFindResource("WarnBrush") as SolidColorBrush)?.Color ?? Colors.Orange;
            var pen = new Pen(new SolidColorBrush(color), 2.5);
            var fill = new SolidColorBrush(color) { Opacity = 0.14 };
            // 왜: 옆 단추와의 간격이 5px 라 4 로 부풀리면 테가 이웃 테두리에 닿는다 — 3 이면 1px 이 남는다
            var rect = new Rect(AdornedElement.RenderSize);
            rect.Inflate(3, 3);
            drawingContext.DrawRoundedRectangle(fill, pen, rect, 9, 9);
        }
    }
}
