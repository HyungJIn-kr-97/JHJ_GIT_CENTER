using System.IO;
using System.Reflection;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GitCenter.App;

public partial class App : System.Windows.Application
{
    private const string AppName = "JHJ_Git-Center";

    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private TrayHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        BootstrapLogger();
        RegisterExceptionHandlers();
        // 계약: 창이 만들어지기 전에 한 번은 불러야 한다 — 테마 키가 없으면 창 XAML 로딩이 실패한다
        ThemeManager.Apply(ThemeManager.DefaultPalette, ThemeManager.ResolveIsDark(ThemeManager.SystemMode));

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Log.Information("{App} 시작 (v{Version}, PID {Pid})", AppName, version, Environment.ProcessId);

        _singleInstance = new SingleInstanceCoordinator(AppName);
        if (!_singleInstance.IsPrimary)
        {
            // 왜: 두 번째 실행은 첫 번째 창을 띄우고 조용히 사라진다 — 아무 반응이 없으면 고장으로 보인다
            _ = Task.Run(async () =>
            {
                try
                {
                    await SingleInstanceCoordinator
                        .SignalPrimaryAsync(_singleInstance.PipeName, ActivationMessage.ShowWindow, TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 첫 인스턴스가 막 끝났을 수 있다
                }
                finally
                {
                    Dispatcher.Invoke(() => Shutdown(0));
                }
            });

            return;
        }

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var settingsStore = new JsonSettingsStore();
            var settings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                ThemeManager.Apply(settings.Palette, ThemeManager.ResolveIsDark(settings.Theme));
                Loc.SetLanguage(settings.Language);

                // 계약: 작업 관리자 「시작 앱」에서 끈 것을 설정이 덮어쓰지 않게 — 거기서 껐으면 설정을 끈다
                if (settings.StartAtLogin && StartupRegistry.IsDisabledByTaskManager())
                {
                    settings.StartAtLogin = false;
                }

                StartupRegistry.Set(settings.StartAtLogin);

                BuildHost(settingsStore, settings);

                var window = _host!.Services.GetRequiredService<MainWindow>();
                _tray = _host.Services.GetRequiredService<TrayHost>();
                _tray.Start();
                _host.Services.GetRequiredService<HotkeyService>().Register();

                window.Show();

                var main = _host.Services.GetRequiredService<MainViewModel>();
                _ = main.RescanAsync();

                LogFireAndForget(StartListenerAsync(), "SingleInstanceListener");
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "시작 실패");
            System.Windows.MessageBox.Show(
                $"시작하지 못했습니다: {ex.Message}\n\n{ex.StackTrace}",
                "Git 통합 관리 센터",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

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

        await _singleInstance.StartListenerAsync(async message =>
        {
            if (message == ActivationMessage.ShowWindow)
            {
                await Dispatcher.InvokeAsync(() => _tray?.ShowWindow());
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("끝남 (ExitCode={ExitCode})", e.ApplicationExitCode);

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

    private static void BootstrapLogger() => Log.Logger = Configure(new LoggerConfiguration()).CreateLogger();

    /// <summary>로그 설정은 한 곳뿐이다 — 부트스트랩과 호스트가 같은 것을 쓴다.</summary>
    /// 계약: 경로를 코드에서 만든다 — appsettings.json 에 %LOCALAPPDATA% 를 적으면 그대로 폴더 이름이 되는 수가 있다
    private static LoggerConfiguration Configure(LoggerConfiguration logger)
    {
        var logDir = Path.Combine(JsonSettingsStore.DataRoot, "logs");
        Directory.CreateDirectory(logDir);

        return logger
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDir, AppName + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext();
    }

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

    /// 왜: 띄워 두고 잊은 작업의 예외가 조용히 사라지지 않게 — 로그에는 남긴다
    private static void LogFireAndForget(Task task, string operation)
    {
        task.ContinueWith(
            t => Log.Error(t.Exception!.GetBaseException(), "배경 작업 {Operation} 실패", operation),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
