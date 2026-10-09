using System.Windows;
using System.Windows.Controls;
using GitCenter.App.Views;
using H.NotifyIcon;
using Serilog;

namespace GitCenter.App.Services;

/// <summary>
/// 트레이 아이콘과 창 하나를 묶는다 — 창을 닫아도 앱은 트레이에 남는다.
/// 계약: 「끝내기」만 실제로 앱을 끝낸다 — 창 닫기는 숨기기다(설정에서 끌 수 있다).
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly MainWindow _window;
    private TaskbarIcon? _icon;

    public TrayHost(MainWindow window)
    {
        _window = window;
        _window.CloseRequested += OnCloseRequested;
    }

    public void Start()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "Git 통합 관리 센터",
            Icon = LoadIcon(),
            ContextMenu = BuildMenu()
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowWindow();
        _icon.ForceCreate();
    }

    public void ShowWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "트레이 알림 실패");
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_window.MinimizeToTray)
        {
            _window.Hide();
            return;
        }

        Shutdown();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "창 열기" };
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);

        var refresh = new MenuItem { Header = "상태 새로 고침" };
        refresh.Click += async (_, _) => await _window.ViewModel.RefreshStatusAsync().ConfigureAwait(true);
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "끝내기" };
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private void Shutdown()
    {
        _icon?.Dispose();
        _icon = null;
        System.Windows.Application.Current.Shutdown(0);
    }

    /// 함정: 트레이는 16 × 화면 배율 크기로 읽는다 — 크기를 지정하지 않으면 256 판을 줄여 그려 흐려진다
    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app-icon.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null)
            {
                return null;
            }

            var side = (int)Math.Round(16 * (SystemParameters.PrimaryScreenHeight > 0
                ? System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX
                : 1.0));

            return new System.Drawing.Icon(stream, new System.Drawing.Size(Math.Max(16, side), Math.Max(16, side)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "트레이 아이콘을 읽지 못했습니다");
            return null;
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
