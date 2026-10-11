using System.Windows;
using System.Windows.Controls;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Views;

/// <summary>
/// 대시보드 화면 — 여기는 「UI 편집」의 칸 비율만 다룬다(그림은 XAML · 수는 MainViewModel.Dashboard).
/// 계약: 비율은 별(*) 무게로 읽고 쓴다 — 창 크기·배율이 달라도 같은 배치로 되살아난다.
/// 계약: 편집 중에 바꾼 크기는 「배치 저장」을 눌러야 남는다 — 「취소」는 저장돼 있던 배치로 되돌린다.
/// </summary>
public partial class DashboardView : UserControl
{
    // 계약: 열쇠는 Settings.DashboardLayout 과 같고, 값은 처음 배치의 무게다
    private static readonly Dictionary<string, double[]> Defaults = new()
    {
        ["rows"] = [34, 33, 33],
        ["top"] = [34, 22, 22, 22],
        ["middle"] = [40, 20, 20, 20],
        ["bottom"] = [34, 33, 33]
    };

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyLayout(ViewModel?.Settings.DashboardLayout);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnEditLayoutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.IsDashboardEditing = true;
        }
    }

    private async void OnSaveLayoutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.Settings.DashboardLayout["rows"] = Weights(LayoutRows.RowDefinitions.Select(r => r.ActualHeight));
        vm.Settings.DashboardLayout["top"] = Weights(TopRow.ColumnDefinitions.Select(c => c.ActualWidth));
        vm.Settings.DashboardLayout["middle"] = Weights(MiddleRow.ColumnDefinitions.Select(c => c.ActualWidth));
        vm.Settings.DashboardLayout["bottom"] = Weights(BottomRow.ColumnDefinitions.Select(c => c.ActualWidth));
        vm.IsDashboardEditing = false;
        await vm.SaveSettingsAsync().ConfigureAwait(true);
    }

    private void OnCancelLayoutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            ApplyLayout(vm.Settings.DashboardLayout);
            vm.IsDashboardEditing = false;
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e) => ApplyLayout(null);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenSettings();

    // 왜: 끌고 난 뒤의 별 값은 WPF 가 제멋대로 키운다 — 실제 크기를 100 기준 비율로 고쳐 적어야 파일이 읽힌다
    private static double[] Weights(IEnumerable<double> sizes)
    {
        var cards = sizes.Where((_, i) => i % 2 == 0).ToArray();
        var sum = cards.Sum();
        return sum <= 0 ? [] : [.. cards.Select(v => Math.Round(v / sum * 100, 2))];
    }

    private void ApplyLayout(IReadOnlyDictionary<string, double[]>? saved)
    {
        Apply("rows", LayoutRows.RowDefinitions.Count, (i, v) => LayoutRows.RowDefinitions[i].Height = v);
        Apply("top", TopRow.ColumnDefinitions.Count, (i, v) => TopRow.ColumnDefinitions[i].Width = v);
        Apply("middle", MiddleRow.ColumnDefinitions.Count, (i, v) => MiddleRow.ColumnDefinitions[i].Width = v);
        Apply("bottom", BottomRow.ColumnDefinitions.Count, (i, v) => BottomRow.ColumnDefinitions[i].Width = v);

        void Apply(string key, int count, Action<int, GridLength> set)
        {
            var cards = (count + 1) / 2;

            // 계약: 칸 수가 안 맞거나 0 이하가 섞인 저장값은 버린다 — 카드 구성이 바뀐 뒤의 옛 파일이다
            var weights = saved is not null && saved.TryGetValue(key, out var found) && found.Length == cards && found.All(v => v > 0)
                ? found
                : Defaults[key];
            for (var i = 0; i < cards; i++)
            {
                set(i * 2, new GridLength(weights[i], GridUnitType.Star));
            }
        }
    }
}
