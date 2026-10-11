using System.IO;
using System.Windows;
using GitCenter.App.Views;
using Jhj.Core.Wpf.Shell;
using Jhj.Core.Wpf.Tray;
using Serilog;

namespace GitCenter.App.Services;

/// <summary>
/// 트레이 아이콘과 창 하나를 묶는다 — 창을 닫아도 앱은 트레이에 남는다.
/// 계약: 아이콘·메뉴·풍선·창 올리기는 JHJ_CS_CORE 의 TrayShell 이 한다 — 여기는 **이 앱의 메뉴 줄과 닫기 정책**만 갖는다.
/// 계약: 「끝내기」만 실제로 앱을 끝낸다 — 창 닫기는 숨기기다(설정에서 끌 수 있다).
/// 계약: anchorToTaskbar 는 false 다 — 보통 창이라 사용자가 옮겨 둔 자리를 건드리지 않는다.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly MainWindow _window;
    private TrayShell? _shell;

    public TrayHost(MainWindow window)
    {
        _window = window;
        _window.CloseRequested += OnCloseRequested;
    }

    public void Start()
    {
        _shell = new TrayShell(
            _window,
            iconFactory: AppIcon.ForTray,
            menuFactory: BuildMenu,
            pinToTaskbar: true,
            anchorToTaskbar: false);

        // 계약: 설정에서 아이콘을 바꾸면 트레이도 같이 바뀐다 — 창 머리·작업 표시줄은 AppIcon.Bind 가 맡는다
        AppIcon.Changed += OnIconChanged;
    }

    private void OnIconChanged() => _window.Dispatcher.BeginInvoke(() => _shell?.RefreshIcon());

    public void ShowWindow() => _shell?.Show();

    public void ShowBalloon(string title, string message) => _shell?.ShowBalloon(title, message);

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_window.MinimizeToTray)
        {
            _window.Hide();
            return;
        }

        Shutdown();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new TrayMenuItem(Loc.T("창 열기"), ShowWindow, Bold: true),
        new TrayMenuItem(Loc.T("상태 새로 고침"), () => _ = _window.ViewModel.RefreshStatusAsync()),
        TrayMenuItem.Separator,
        new TrayMenuItem(Loc.T("끝내기"), Shutdown),
    ];

    // 함정: TrayShell.Dispose 가 창을 닫는다 — 먼저 CloseRequested 를 떼지 않으면 다시 여기로 들어온다
    private void Shutdown()
    {
        Dispose();
        System.Windows.Application.Current.Shutdown(0);
    }

    public void Dispose()
    {
        AppIcon.Changed -= OnIconChanged;
        _window.CloseRequested -= OnCloseRequested;
        _shell?.Dispose();
        _shell = null;
    }
}
