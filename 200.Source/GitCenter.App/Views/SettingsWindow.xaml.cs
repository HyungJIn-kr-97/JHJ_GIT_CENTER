using System.Windows;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Views;

public partial class SettingsWindow : Window
{
    private readonly Services.HotkeyService _hotkey;

    public SettingsWindow(SettingsViewModel viewModel, Services.HotkeyService hotkey)
    {
        InitializeComponent();
        DataContext = viewModel;
        _hotkey = hotkey;
    }

    // 계약: 창을 닫을 때 단축키를 다시 등록한다 — 설정에서 바꿔도 다시 시작하지 않아도 되게
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _hotkey.Register();
        Close();
    }
}
