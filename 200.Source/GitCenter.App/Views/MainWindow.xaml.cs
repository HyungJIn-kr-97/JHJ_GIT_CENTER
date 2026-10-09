using System.ComponentModel;
using System.Windows;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Views;

public partial class MainWindow : Window
{
    private readonly Func<SettingsWindow> _settingsFactory;

    public MainWindow(MainViewModel viewModel, Func<SettingsWindow> settingsFactory)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _settingsFactory = settingsFactory;
        DataContext = viewModel;

        Width = Math.Max(MinWidth, viewModel.Settings.WindowWidth);
        Height = Math.Max(MinHeight, viewModel.Settings.WindowHeight);
    }

    public MainViewModel ViewModel { get; }

    /// 계약: 트레이로 보낼지 끝낼지는 TrayHost 가 정한다 — 창은 「닫으려 한다」만 알린다
    public event EventHandler? CloseRequested;

    public bool MinimizeToTray => ViewModel.Settings.MinimizeToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        ViewModel.Settings.WindowWidth = ActualWidth;
        ViewModel.Settings.WindowHeight = ActualHeight;
        _ = ViewModel.SaveSettingsAsync();

        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = _settingsFactory();
        window.Owner = this;
        window.ShowDialog();
    }
}
