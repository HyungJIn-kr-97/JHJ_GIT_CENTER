using System.IO;
using System.Windows;
using GitCenter.App.Localization;
using GitCenter.App.Services;
using GitCenter.App.ViewModels;
using GitCenter.App.Views;
using GitCenter.Application.Abstractions;
using GitCenter.Application.Services;
using GitCenter.Application.Settings;
using GitCenter.Infrastructure.Git;
using GitCenter.Infrastructure.Settings;
using GitCenter.Infrastructure.Sources;
using Jhj.Core.App;
using Jhj.Core.Updates;
using Jhj.Core.Windows.Install;
using Jhj.Core.Wpf.Boot;
using Jhj.Core.Wpf.Localization;
using Jhj.Core.Wpf.Shell;
using Jhj.Core.Wpf.Theme;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GitCenter.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TrayHost? _tray;
    private GithubUpdateService? _updates;
    private UpdateOptions? _updateOptions;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        // ①~③ 신원 → 옛 이름 이사(이 앱은 옛 이름이 없다) → 테마 브러시
        // 계약: 창이 만들어지기 전에 한 번은 불러야 한다 — 테마 키가 없으면 창 XAML 로딩이 실패한다
        JhjTheme.Extend = AddGitBrushes;
        JhjBoot.Begin(new JhjApp
        {
            Name = "JHJ_Git-Center",
            DisplayName = "JHJ Git 통합 관리 센터",
            DefaultPalette = JhjPalettes.Violet,
            // 계약: 공개 저장소 좌표 — 「이슈 열기」·링크 주소(AppLinks)가 여기서 나온다
            Repository = AppUpdate.Repository,
            SetupAssetName = AppUpdate.SetupAssetName,
            WingetId = AppUpdate.WingetId,
        });

        // 계약: 명암이 "system" 인 동안 Windows 의 밝게/어둡게 전환을 따라간다
        JhjTheme.FollowSystem();

        JhjBoot.BootstrapLogger();
        RegisterExceptionHandlers();

        // 계약: Core 화면 부품(UpdatePanel)의 문구 — 빠지면 그 구역만 영어로 나온다
        Loc.Register(CoreStrings.Catalog);
        Loc.Register(LocStrings.Catalog);

        // 계약: 자가 업데이트는 설치본(%LOCALAPPDATA%\JHJ_Git-Center\app)에서만 돈다 — 받은 exe 를 처음 띄우면 설치할지 묻는다
        if (SelfInstaller.TryInstallAndRelaunch(ConfirmInstall, WarnInstallFailed))
        {
            Shutdown(0);
            return;
        }

        // ⑦ 이미 떠 있으면 그쪽 창을 띄우고 조용히 사라진다 — 아무 반응이 없으면 고장으로 보인다
        if (JhjBoot.TryHandOffToRunningInstance(out _singleInstance))
        {
            Shutdown(0);
            return;
        }

        _ = InitializeAsync();
    }

    /// 계약: 저장소 상태 다섯 가지의 색 — 공통 뜻 색(Ok·Info·Warn·Danger·Dirty)에 git 이름을 붙인 것이다
    private static void AddGitBrushes(ResourceDictionary dict, JhjTone tone, bool dark)
    {
        dict["BehindBrush"] = dict["InfoBrush"];
        dict["AheadBrush"] = dict["WarnBrush"];
    }

    private async Task InitializeAsync()
    {
        try
        {
            _updateOptions = AppUpdate.FromConfiguration(BuildStartupConfiguration());
            _updates = new GithubUpdateService(_updateOptions);
            var settingsStore = new JsonSettingsStore();

            // 왜: 설정을 못 읽는 결함도 업데이트로 고칠 수 있어야 한다 — 읽기에 실패하면 자동 업데이트를 켠 것으로 보고 적용부터 한다
            AppSettings? earlySettings = null;
            try
            {
                earlySettings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "업데이트 적용 전 설정 읽기 실패");
            }

            if ((earlySettings?.AutoUpdateEnabled ?? true)
                && await _updates.TryApplyPendingUpdateAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Log.Information("받아 둔 업데이트를 적용하려고 끝냅니다");
                await Dispatcher.InvokeAsync(() => Shutdown(0));
                return;
            }

            var settings = earlySettings ?? await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                JhjTheme.ApplyPreference(settings.Palette, settings.Theme);
                Loc.SetLanguage(settings.Language);

                // 계약: 창이 만들어지기 전에 아이콘 출처를 정한다 — 창 머리·작업 표시줄·트레이가 여기서 받아 간다
                AppIcon.Configure("pack://application:,,,/Resources/app-icon.ico", IconPresets.All);
                AppIcon.Apply(settings.AppIcon);

                // 계약: 작업 관리자 「시작 앱」과 양방향으로 맞춘다 — 거기서 껐으면 설정을 끈다
                settings.StartAtLogin = StartupRegistry.Sync(settings.StartAtLogin);

                BuildHost(settingsStore, settings);

                var window = _host!.Services.GetRequiredService<MainWindow>();
                _tray = _host.Services.GetRequiredService<TrayHost>();
                _tray.Start();
                _host.Services.GetRequiredService<HotkeyService>().Register();

                // 왜: 예약 업데이트 타이머가 SettingsViewModel.Update 안에 있다 — 설정 창을 열기 전에도 돌게 지금 만든다
                var settingsViewModel = _host.Services.GetRequiredService<SettingsViewModel>();
                settingsViewModel.Notify = _tray.ShowBalloon;
                settingsViewModel.PinTray = _tray.SetPinned;

                Jhj.Core.Diagnostics.DiagnosticsLog.Enabled = settings.DiagnosticsEnabled;
                Jhj.Core.Diagnostics.DiagnosticsLog.Record("app", "started v" + AppVersion.Text);

                window.Show();

                var main = _host.Services.GetRequiredService<MainViewModel>();
                _ = main.RescanAsync();

                JhjBoot.LogFireAndForget(StartListenerAsync(), "SingleInstanceListener");
            });

            if (settings.AutoUpdateEnabled)
            {
                JhjBoot.LogFireAndForget(CheckForUpdateAsync(), "UpdateCheck");
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "시작 실패");
            System.Windows.MessageBox.Show(
                $"시작하지 못했습니다: {ex.Message}\n\n{ex.StackTrace}",
                "JHJ Git Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // 계약: 호스트보다 먼저 읽는다 — 업데이트 적용은 호스트·창을 만들기 전에 끝나야 한다
    private static IConfiguration BuildStartupConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

    // 계약: 새 버전을 받아 두기만 한다 — 교체는 다음 실행 때 TryApplyPendingUpdateAsync 가 한다
    private async Task CheckForUpdateAsync()
    {
        if (_updates is not { } updates)
        {
            return;
        }

        // 왜: 멈춘 내려받기가 확인 잠금을 끝없이 쥐지 않게 시간을 끊는다
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await Task.Run(() => updates.CheckAndStageUpdateAsync(cts.Token)).ConfigureAwait(false);
        Log.Information("업데이트 확인: {Result}", result);
    }

    // 계약: 묻고 알리는 것은 앱 몫이다 — Core 의 SelfInstaller 는 WPF 를 모른다. 본문(설치본 버전 → 이번 버전)은 Core 의 InstallPromptText 다
    private static bool ConfirmInstall(InstallPrompt prompt) =>
        Environment.GetCommandLineArgs().Contains(AppUpdate.SilentInstallArg, StringComparer.OrdinalIgnoreCase) ||
        System.Windows.MessageBox.Show(
            InstallPromptText.Describe(prompt),
            "JHJ Git Center",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void WarnInstallFailed(string message) =>
        System.Windows.MessageBox.Show(message, "JHJ Git Center", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void BuildHost(ISettingsStore settingsStore, AppSettings settings)
    {
        // 함정: 부트스트랩 로거가 같은 파일을 잡고 있으면 호스트 로거가 그 파일을 못 열고 조용히 아무것도 안 쓴다 — 먼저 닫는다
        Log.CloseAndFlush();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            // 함정: IServiceProvider 를 받는 UseSerilog 는 로거를 「늦게」 만든다 — ILogger<T> 를 아무도 주입받지 않으면 끝내 안 만들어져 로그가 통째로 사라진다
            .UseSerilog((context, logger) => Configure(logger))
            .ConfigureServices(services =>
            {
                services.AddSingleton(settingsStore);
                services.AddSingleton(settings);
                services.AddSingleton(_updateOptions!);
                services.AddSingleton<IClock, SystemClock>();

                services.AddSingleton<IGitRunner, GitRunner>();
                services.AddSingleton<IGitService, GitService>();

                services.AddSingleton<IRepoSource, RootScanSource>();
                services.AddSingleton<IRepoSource, ManualSource>();
                services.AddSingleton<IRepoSource, GitHubDesktopSource>();
                services.AddSingleton<IRepoSource, GitHubAccountSource>();
                services.AddSingleton<IRepoSource, CommandSource>();
                services.AddSingleton<RepoRegistry>();
                services.AddSingleton<BulkOperationRunner>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddTransient<SettingsWindow>();
                services.AddSingleton<Func<SettingsWindow>>(sp => sp.GetRequiredService<SettingsWindow>);
                services.AddSingleton<TrayHost>();
                services.AddSingleton<HotkeyService>();
            })
            .Build();

        _host.Start();
    }

    private async Task StartListenerAsync()
    {
        if (_singleInstance is null)
        {
            return;
        }

        await _singleInstance.StartListenerAsync(
            _ => Dispatcher.InvokeAsync(() => _tray?.ShowWindow()).Task,
            CancellationToken.None).ConfigureAwait(false);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("끝남 (ExitCode={ExitCode})", e.ApplicationExitCode);
        JhjTheme.StopFollowingSystem();

        try
        {
            if (_host is not null)
            {
                await _host.StopAsync().ConfigureAwait(false);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "호스트 종료 중 오류");
        }

        _tray?.Dispose();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>호스트 로거 설정 — 부트스트랩(JhjBoot)과 같은 파일에 이어 쓴다.</summary>
    /// 계약: 경로를 코드에서 만든다 — appsettings.json 에 %LOCALAPPDATA% 를 적으면 그대로 폴더 이름이 되는 수가 있다
    private static LoggerConfiguration Configure(LoggerConfiguration logger)
    {
        var logDir = AppPaths.Ensure(AppPaths.Logs);

        return logger
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDir, JhjApp.Current.Name + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext();
    }

    // 왜: 문구가 한국어라 Core 의 기본 처리기를 쓰지 않는다 — 나머지 동작은 JhjBoot 와 같다
    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "처리되지 않은 UI 예외");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "관찰되지 않은 작업 예외");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "처리되지 않은 도메인 예외 (IsTerminating={IsTerminating})", args.IsTerminating);
                Log.CloseAndFlush();
            }
        };
    }
}
