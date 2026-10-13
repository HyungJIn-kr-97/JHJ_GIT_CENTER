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

        // 계약: 열 때는 실제 등록값으로 줄을 세우고, 닫을 때는 저장하지 않은 입력을 버린다 — 뷰모델은 창보다 오래 산다
        Loaded += (_, _) => viewModel.Hotkeys.Rebuild();
        Closed += (_, _) => viewModel.Hotkeys.CloseEditing();

        // 왜: 파일 두 곳만 읽는 판정이라 열 때마다 다시 본다 — gh 로그인을 바꾼 뒤 앱을 다시 띄우지 않아도 된다
        Loaded += (_, _) => ShareDeployButton.Visibility = _deploy.CanDeploy ? Visibility.Visible : Visibility.Collapsed;

        // 계약: AppLinks 가 빈 문자열을 주는 단추는 숨긴다 — winget 미등록·비공개 상태에서 빈 값을 복사하지 않게(030 규격 §4-1)
        Loaded += (_, _) =>
        {
            ShareWingetButton.Visibility = Jhj.Core.App.AppLinks.WingetCommand.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShareLinkButton.Visibility = Jhj.Core.App.AppLinks.SetupFileUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShareDownloadButton.Visibility = ShareLinkButton.Visibility;
        };
    }

    private readonly Jhj.Core.App.AppDeploy _deploy = new(Services.AppUpdate.Repository);

    private void Tell(string message)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.TestResult = message;
        }
    }

    private void CopyShared(string text, string doneMessage)
    {
        try
        {
            Clipboard.SetText(text);
            Tell(Loc.T(doneMessage));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 함정: 클립보드는 다른 프로그램이 잡고 있으면 던진다 — 원격 데스크톱·클립보드 관리 도구에서 흔하다
            Tell(Loc.T("Could not copy to the clipboard."));
        }
    }

    private void OnShareWingetClick(object sender, RoutedEventArgs e) =>
        CopyShared(Jhj.Core.App.AppLinks.WingetCommand, "Copied the winget command");

    private void OnShareLinkClick(object sender, RoutedEventArgs e) =>
        CopyShared(Jhj.Core.App.AppLinks.SetupFileUrl, "Copied the setup file link");

    private void OnShareDownloadClick(object sender, RoutedEventArgs e)
    {
        if (!Jhj.Core.Windows.Shell.ShellOpener.OpenUrl(Jhj.Core.App.AppLinks.SetupFileUrl))
        {
            Tell(Loc.T("Could not open the link."));
        }
    }

    // 계약: 계정 확인 → 이력 시작 기록 → 배포 콘솔 순서는 Core 가 지킨다 — 여기서는 결과만 창 바닥 줄에 알린다
    private async void OnShareDeployClick(object sender, RoutedEventArgs e)
    {
        // 계약: release.ps1 이 묻지 않고 도는 인자를 받으면 앱 안 배포 화면을 여닫는다 — 못 받는 옛 스크립트면 콘솔 배포 그대로다
        if (_deploy.SupportsInApp)
        {
            SetDeployOpen(DeployHost.Visibility != Visibility.Visible);
            return;
        }

        ShareDeployButton.IsEnabled = false;
        var (outcome, detail) = await _deploy.StartAsync();
        ShareDeployButton.IsEnabled = true;
        Tell(Jhj.Core.Wpf.Localization.AppDeployText.Describe(outcome, detail));
    }

    private Jhj.Core.Wpf.Deploy.DeployPanelViewModel? _deployViewModel;

    // 계약: 배포 화면을 닫아도 돌고 있는 배포는 계속 돈다 — 열려 있는 동안 「배포하기」 단추에 강조색 바탕이 든다
    // 함정: 뷰모델은 이 창과 함께 사라진다 — 설정 창을 닫았다 열면 진행 표시는 이어지지 않는다(배포 자체는 끝까지 간다)
    private void SetDeployOpen(bool open)
    {
        if (open && _deployViewModel is null)
        {
            _deployViewModel = new Jhj.Core.Wpf.Deploy.DeployPanelViewModel(_deploy);
            _deployViewModel.CloseRequested += (_, _) => SetDeployOpen(false);
            DeployView.DataContext = _deployViewModel;
        }

        DeployHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            ShareDeployButton.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
        }
        else
        {
            ShareDeployButton.ClearValue(BackgroundProperty);
        }
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
