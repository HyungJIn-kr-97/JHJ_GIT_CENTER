using System.Windows;
using System.Windows.Input;
using GitCenter.App.ViewModels;
using Jhj.Core.Wpf.Hotkeys;

namespace GitCenter.App.Views.SettingsTabs;

/// <summary>
/// 설정 「일반」 탭 — 규격은 JHJ_CS_CORE/300.Docs/030.설정-창-기본-규격.md §2.
/// 계약: 단축키 입력란의 키·초점 신호를 Core 의 HotkeySlotEditor 로 넘기는 일만 한다 — 판정·등록은 그쪽이다.
/// </summary>
public partial class GeneralTab : System.Windows.Controls.UserControl
{
    public GeneralTab() => InitializeComponent();

    private HotkeySlotEditor? Editor => (DataContext as SettingsViewModel)?.Hotkeys;

    private static HotkeySlotRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as HotkeySlotRow;

    // 왜: 완료 상태의 줄까지 넘기면 편집기가 Tab 을 삼켜 초점이 입력란에 갇힌다
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is { IsEditing: true } row)
        {
            Editor?.KeyDown(row, e);
        }
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is { IsEditing: true } row)
        {
            Editor?.KeyUp(row, e);
        }
    }

    // 왜: 입력란이 키를 받는 동안 단축키가 살아 있으면 지금 키를 누르는 순간 창이 불려 나온다
    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RowOf(sender) is { IsEditing: true })
        {
            Editor?.BeginCapture();
        }
    }

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e) => Editor?.EndCapture();
}
