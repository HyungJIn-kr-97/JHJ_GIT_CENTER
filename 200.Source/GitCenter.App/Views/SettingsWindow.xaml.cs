using System.Windows;
using System.Windows.Input;
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

    /// 계약: 제목 표시줄이 없다 — 머리 줄을 끌어 창을 옮긴다
    private void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
