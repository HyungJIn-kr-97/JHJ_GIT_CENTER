using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GitCenter.App.Localization;
using GitCenter.App.ViewModels;
using Jhj.Core.Wpf.Shell;
using Serilog;

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

        ApplyDisplaySettings();
        ApplyStartupSize();
        RestorePosition();

        AppIcon.Bind(this);
        AppIcon.Bind(TitleIcon, TitleIcon.Width);

        viewModel.DisplaySettingsChanged += ApplyDisplaySettings;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRightPanelOpen))
        {
            ApplyDisplaySettings();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedRow) && ViewModel.SelectedRow is { } row)
        {
            // 왜: 대시보드에서 건너온 줄이 접힌 그룹 아래·화면 밖에 있으면 고른 것이 안 보인다 — 펴진 뒤에 그 줄로 굴린다
            Dispatcher.BeginInvoke(() => RepoList.ScrollIntoView(row), DispatcherPriority.Background);
        }
    }

    public MainViewModel ViewModel { get; }

    /// 계약: 트레이로 보낼지 끝낼지는 TrayHost 가 정한다 — 창은 「닫으려 한다」만 알린다
    public event EventHandler? CloseRequested;

    public bool MinimizeToTray => ViewModel.Settings.MinimizeToTray;

    /// <summary>창이 직접 그리는 설정(배율 · 작업 로그 단)을 지금 값으로 다시 맞춘다.</summary>
    /// 계약: 설정 창의 「적용」이 MainViewModel 을 거쳐 이것을 부른다 — 다시 시작하지 않아도 반영된다
    public void ApplyDisplaySettings()
    {
        var scale = Math.Clamp(ViewModel.Settings.UiScale, 0.8, 1.5);
        ShellScale.ScaleX = scale;
        ShellScale.ScaleY = scale;

        // 계약: 설정이 단 자체를 끄면 손잡이까지 숨긴다. 켜져 있으면 접힌 채(손잡이만)로 시작해 IsRightPanelOpen 이 편다
        var enabled = ViewModel.Settings.ShowActivityPanel;
        var showActivity = enabled && ViewModel.IsRightPanelOpen;
        ActivityPanel.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        ActivitySplitter.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        ActivityRail.Visibility = enabled && !showActivity ? Visibility.Visible : Visibility.Collapsed;

        // 함정: 칸을 숨기기만 하면 별(*) 폭이 그대로 남아 가운데 단이 안 넓어진다 — 폭과 최소폭을 함께 0 으로 둔다
        ActivityColumn.MinWidth = showActivity ? 300 : 0;
        ActivityColumn.Width = showActivity ? new GridLength(28, GridUnitType.Star) : new GridLength(0);

        // 왜: 배율을 키우면 같은 창에 들어가는 내용이 줄어든다 — 최소 크기도 함께 키워야 3단이 안 뭉개진다
        MinWidth = (showActivity ? 1180 : 880) * scale;
        MinHeight = 680 * scale;
    }

    /// 계약: 복원 크기는 사용자 화면의 작업 영역에 맞추고, 처음에는 최대화로 연다 — 규격은 080.CS앱-화면-공통.md §3
    private void ApplyStartupSize()
    {
        var settings = ViewModel.Settings;

        // 왜: 옛 기본값(1480×900)이 그대로 저장된 PC 는 고른 적이 없는 것이다 — 한 번만 화면 기준으로 다시 잡는다
        var legacyDefault = Math.Abs(settings.WindowWidth - 1480) < 0.5 && Math.Abs(settings.WindowHeight - 900) < 0.5;
        var width = legacyDefault ? 0 : settings.WindowWidth;
        var height = legacyDefault ? 0 : settings.WindowHeight;

        WindowFit.Apply(this, width, height);

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
            // 함정: 창 핸들이 생기기 전에 최대화하면 넘친 만큼 미는 계산(ApplyMaximizePadding)이 헛돈다 — 뜬 뒤에 한 번 더 잰다
            Loaded += (_, _) => Dispatcher.BeginInvoke(ApplyMaximizePadding, DispatcherPriority.Loaded);
        }
    }

    // 함정: 모니터를 뺀 뒤 그 좌표로 띄우면 창이 화면 밖에 나타나 찾을 수 없다 — 가상 화면 안일 때만 되살린다
    private void RestorePosition()
    {
        var settings = ViewModel.Settings;
        if (double.IsNaN(settings.WindowLeft) || double.IsNaN(settings.WindowTop))
        {
            return;
        }

        var left = settings.WindowLeft;
        var top = settings.WindowTop;
        var inside =
            left + Width > SystemParameters.VirtualScreenLeft &&
            top + Height > SystemParameters.VirtualScreenTop &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (!inside)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 계약: 최대화된 채 닫으면 Restore 크기를 저장한다 — 화면 전체 크기를 적으면 다음에 복원이 안 된다
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        ViewModel.Settings.WindowWidth = bounds.Width;
        ViewModel.Settings.WindowHeight = bounds.Height;
        ViewModel.Settings.WindowLeft = bounds.Left;
        ViewModel.Settings.WindowTop = bounds.Top;
        // 함정: 트레이로 숨긴 창을 닫을 때는 Minimized 일 수 있다 — 그때는 앞의 값을 그대로 둔다
        if (WindowState != WindowState.Minimized)
        {
            ViewModel.Settings.WindowMaximized = WindowState == WindowState.Maximized;
        }

        _ = ViewModel.SaveSettingsAsync();

        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── 제목 표시줄 노릇 ──────────────────────────────────────

    /// 계약: 머리띠의 빈 곳을 끌면 창이 따라온다. 두 번 누르면 최대화/이전 크기
    private void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 함정: 최대화 상태에서 DragMove 를 부르면 예외가 난다 — 끌기 시작 전에 이전 크기로 돌린다
        if (WindowState == WindowState.Maximized)
        {
            RestoreUnderCursor();
        }

        DragMove();
    }

    // 계약: 제목 표시줄이 없어도 오른쪽 클릭으로 시스템 메뉴(이동·크기·닫기)는 나와야 한다
    private void OnCaptionRightClick(object sender, MouseButtonEventArgs e) =>
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// 계약: 닫기는 끝내기가 아니다 — OnClosing 이 받아 트레이로 보낸다
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // 왜: 최대화된 창을 끌면 커서가 잡고 있던 가로 위치를 유지해야 자연스럽다 — Windows 기본 동작과 같다
    private void RestoreUnderCursor()
    {
        var cursor = PointToScreen(Mouse.GetPosition(this));
        var ratio = ActualWidth <= 0 ? 0.5 : Mouse.GetPosition(this).X / ActualWidth;

        WindowState = WindowState.Normal;
        Left = cursor.X - RestoreBounds.Width * ratio;
        Top = cursor.Y - 16;
    }

    /// 계약: 최대화·이전 크기에 맞춰 가운데 단추 그림을 바꾼다
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (Geometry)FindResource(maximized ? "IconRestore" : "IconMaximize");
        MaximizeButton.ToolTip = maximized ? Loc.T("이전 크기로") : Loc.T("최대화");

        // 왜: 상태가 바뀐 직후에는 창 크기가 아직 안 정해졌다 — 한 박자 뒤에 잰다
        Dispatcher.BeginInvoke(ApplyMaximizePadding, DispatcherPriority.Loaded);
    }

    /// 계약: 최대화된 창이 화면 밖으로 넘친 만큼 안쪽으로 민다.
    // 함정: WindowChrome 으로 최대화하면 창 영역이 작업 영역보다 테두리 두께(이 PC 8px)만큼 커져
    //       머리띠 위쪽과 좌우 가장자리가 화면 밖으로 잘린다. WM_GETMINMAXINFO 로 크기를 묶어 봐도
    //       WindowChrome 이 그 값을 덮어쓴다 — 그래서 넘친 양을 직접 재서 여백으로 돌려준다.
    private void ApplyMaximizePadding()
    {
        if (WindowState != WindowState.Maximized)
        {
            Shell.Margin = default;
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var win))
        {
            return;
        }

        var monitor = Native.MonitorFromWindow(hwnd, Native.MonitorDefaultToNearest);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 계약: 창 영역은 물리 픽셀이고 Margin 은 DIP 다 — 배율로 나눠 준다
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = info.Work;
        Shell.Margin = new Thickness(
            Math.Max(0, (work.Left - win.Left) / dpi.DpiScaleX),
            Math.Max(0, (work.Top - win.Top) / dpi.DpiScaleY),
            Math.Max(0, (win.Right - work.Right) / dpi.DpiScaleX),
            Math.Max(0, (win.Bottom - work.Bottom) / dpi.DpiScaleY));
    }

    private static class Native
    {
        public const int MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public int Flags;
        }
    }

    /// 계약: 그룹 옮기기 쪽지는 단추 아래에 뜬다 — 바깥을 누르면 저절로 닫힌다(StaysOpen=False)
    private void OnGroupPopupClick(object sender, RoutedEventArgs e) => GroupPopup.IsOpen = !GroupPopup.IsOpen;

    /// 계약: 대시보드 화면(DashboardView)의 톱니도 같은 길로 연다
    public void OpenSettings() => OnSettingsClick(this, new RoutedEventArgs());

    // 왜: 설정 창이 못 열리면 단추가 아무 반응도 안 해 고장으로 보인다 — 로그에 남기고 사용자에게도 알린다
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Information("설정 창 열기");
            var window = _settingsFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "설정 창을 열지 못했습니다");
            MessageBox.Show(
                this,
                $"설정 창을 열지 못했습니다: {ex.Message}",
                "JHJ Git Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
