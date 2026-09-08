using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppLifecycle;
using Sefirah.Helpers;
using Sefirah.Views;
using Sefirah.Views.Onboarding;
using Windows.ApplicationModel.Activation;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Sefirah.Data.Models;
using Sefirah.Views.WindowViews;


#if WINDOWS
using Sefirah.Platforms.Windows.Helpers;
using Sefirah.Platforms.Windows.Interop;
#endif

namespace Sefirah;
public partial class App : Application
{
    public static TaskCompletionSource? SplashScreenLoadingTCS { get; private set; }
    public static bool HandleClosedEvents { get; set; } = true;
    public static nint WindowHandle { get; private set; }
    public static Window MainWindow { get; private set; } = null!;
    protected IHost? Host { get; private set; }
    private Frame? _rootFrame;
    
    // Track open DeviceSettingsWindow instances
    private static readonly Dictionary<string, DeviceSettingsWindow> DeviceSettingsWindows = [];

    public App()
    {
        Console.WriteLine("[DEBUG] App constructor starting...");
        InitializeComponent();
        Console.WriteLine("[DEBUG] App InitializeComponent finished.");
        // Configure exception handlers
        UnhandledException += (sender, e) =>
        {
            Console.Error.WriteLine($"[FATAL] UnhandledException: {e.Exception}");
            AppLifecycleHelper.HandleAppUnhandledException(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Console.Error.WriteLine($"[FATAL] AppDomain.UnhandledException: {e.ExceptionObject}");
            AppLifecycleHelper.HandleAppUnhandledException(e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Console.Error.WriteLine($"[FATAL] TaskScheduler.UnobservedTaskException: {e.Exception}");
            AppLifecycleHelper.HandleAppUnhandledException(e.Exception);
            e.SetObserved();
        };
#if !WINDOWS
        if (OperatingSystem.IsMacOS())
        {
            Sefirah.Platforms.Desktop.Mac.MacKeepAliveHelper.EnsureMacAppKeepsRunning();
        }
#endif
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Console.WriteLine("[DEBUG] App.OnLaunched entered.");
        _ = LogStartupFailuresAsync(ActivateAsync());

        async Task LogStartupFailuresAsync(Task activation)
        {
            try
            {
                Console.WriteLine("[DEBUG] Awaiting ActivateAsync()...");
                await activation;
                Console.WriteLine("[DEBUG] ActivateAsync() completed successfully.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] Startup failure: {ex}");
                Console.Error.Flush();
                // Fire-and-forget activation failures would otherwise be swallowed
                // silently, leaving the splash screen visible with no diagnosis.
                AppLifecycleHelper.HandleAppUnhandledException(ex);
                try
                {
                    if (Host is not null)
                        await Host.StopAsync();
                }
                catch (Exception stopException)
                {
                    AppLifecycleHelper.HandleAppUnhandledException(
                        new AggregateException("Host shutdown failed after app activation failed", ex, stopException));
                }
                finally
                {
                    Current.Exit();
                }
            }
        }

        async Task ActivateAsync()
        {
            Console.WriteLine("[DEBUG] ActivateAsync: Building host...");
            Host = AppLifecycleHelper.BuildHost();
            Console.WriteLine("[DEBUG] ActivateAsync: Configuring services...");
            Ioc.Default.ConfigureServices(Host.Services);
            Console.WriteLine("[DEBUG] ActivateAsync: Creating MainWindow...");
            MainWindow = new Window();
            MainWindow.AppWindow.Title = "Sefirah";
            MainWindow.SetWindowIcon();
#if WINDOWS
            WindowHandle = WindowNative.GetWindowHandle(MainWindow);
            MainWindow.ExtendsContentIntoTitleBar = true;
#else
            if (OperatingSystem.IsMacOS())
            {
                Sefirah.Platforms.Desktop.Mac.MacKeepAliveHelper.EnsureMacAppKeepsRunning();
            }
#endif
            Console.WriteLine("[DEBUG] ActivateAsync: Starting host...");
            await Host.StartAsync();
            Console.WriteLine("[DEBUG] ActivateAsync: Host started.");

            bool isStartupTask = false;
            var startupOption = StartupOptions.Disabled;
#if WINDOWS
            var appActivationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            isStartupTask = appActivationArguments.Kind == ExtendedActivationKind.StartupTask ||
                            appActivationArguments.Data is IStartupTaskActivatedEventArgs;

            var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
            startupOption = userSettingsService.GeneralSettingsService.StartupOption;
            await AppLifecycleHelper.HandleStartupTaskAsync(startupOption != StartupOptions.Disabled);

            if (appActivationArguments.Data is ProtocolActivatedEventArgs protocolArgs)
                HandleProtocolActivationArgs(protocolArgs);
#endif
            Console.WriteLine("[DEBUG] ActivateAsync: Hooking events & getting tray...");
            HookEventsForWindow();
            _ = Ioc.Default.GetRequiredService<ISystemTrayService>();

            Console.WriteLine("[DEBUG] ActivateAsync: Initializing root frame...");
            var rootFrame = EnsureWindowIsInitialized();
            if (rootFrame is null)
                return;

            Ioc.Default.GetRequiredService<IAppThemeModeService>().ManageAppearance(MainWindow);

            if (isStartupTask)
            {
                switch (startupOption)
                {
                    case StartupOptions.InTray:
                        // Don't activate or show the window
                        break;
                    case StartupOptions.Minimized:
                        // Need to show the window first, then minimize it
                        MainWindow.Activate();
                        await Task.Delay(200);
                        OverlappedPresenter overlappedPresenter = (MainWindow.AppWindow.Presenter as OverlappedPresenter) ?? OverlappedPresenter.Create();
                        if (overlappedPresenter.IsMinimizable)
                        {
                            overlappedPresenter.Minimize();
                        }
                        break;
                    case StartupOptions.Maximized:
                        MainWindow.Activate();
                        MainWindow.AppWindow.Show();
                        if (MainWindow.AppWindow.Presenter is OverlappedPresenter maximizedPresenter && maximizedPresenter.IsMaximizable)
                            maximizedPresenter.Maximize();
                        break;
                    default:
                        MainWindow.Activate();
                        MainWindow.AppWindow.Show();
                        break;
                };
            }
            else
            {
                MainWindow.Activate();
#if WINDOWS
                // Wait for the Window to initialize
                await Task.Delay(10);
                MainWindow.AppWindow.Show();
#endif
            }

            Console.WriteLine("[DEBUG] ActivateAsync: Navigating splash screen...");
            rootFrame.Navigate(typeof(Views.SplashScreen));

            Console.WriteLine("[DEBUG] ActivateAsync: Initializing app components...");
            await Task.WhenAll(
                Task.Run(AppLifecycleHelper.InitializeAppComponentsAsync),
                Task.Delay(500));
            Console.WriteLine("[DEBUG] ActivateAsync: App components initialized.");

            bool isOnboarding = ApplicationData.Current.LocalSettings.Values["HasCompletedOnboarding"] == null;
            Console.WriteLine($"[DEBUG] ActivateAsync: isOnboarding = {isOnboarding}");
            if (isOnboarding)
            {
                Console.WriteLine("[DEBUG] ActivateAsync: Navigating to WelcomePage...");
                // Navigate to onboarding page
                rootFrame.Navigate(typeof(WelcomePage), null, new SuppressNavigationTransitionInfo());
                Console.WriteLine("[DEBUG] ActivateAsync: Navigated to WelcomePage.");
            }
            else
            {
                Console.WriteLine("[DEBUG] ActivateAsync: Navigating to MainPage...");
                // Navigate to main page
                rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
                Console.WriteLine("[DEBUG] ActivateAsync: Navigated to MainPage.");
            }
        }
    }

    public Frame? EnsureWindowIsInitialized()
    {
        try
        {
            // Do not rebuild the root content on a later activation.
            if (_rootFrame is null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                var rootFrame = new Frame { CacheSize = 1 };
                rootFrame.NavigationFailed += OnNavigationFailed;

                // Host the custom title bar at the window level so every page gets
                // the native caption strip instead of each page drawing its own.
                var rootGrid = new Grid();
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var titleBar = new UserControls.TitleBar();
                Grid.SetRow(titleBar, 0);
                rootGrid.Children.Add(titleBar);

                Grid.SetRow(rootFrame, 1);
                rootGrid.Children.Add(rootFrame);

                // Place the frame in the current Window
                MainWindow.Content = rootGrid;
#if WINDOWS
                MainWindow.SetTitleBar(titleBar);
#endif
                _rootFrame = rootFrame;
            }

            return _rootFrame;
        }

        catch (COMException)
        {
            return null;
        }
    }


#if WINDOWS

    /// <summary>
    /// Gets invoked when the application is activated.
    /// </summary>
    public async Task OnActivatedAsync(AppActivationArguments activatedEventArgs)
    {
        // InitializeApplication accesses UI, needs to be called on UI thread
        await MainWindow.DispatcherQueue.EnqueueAsync(() => InitializeApplicationAsync(activatedEventArgs));
    }

    /// <summary>Parses sefirah://&lt;package&gt; and launches scrcpy for that package.</summary>
    private static async void HandleProtocolActivationArgs(ProtocolActivatedEventArgs protocolArgs)
    {
        var package = protocolArgs.Uri.Host;
        if (string.IsNullOrEmpty(package)) return;
        var screenMirror = Ioc.Default.GetRequiredService<IScreenMirrorService>();
        screenMirror.LaunchAppByPackage(package);
    }

    public static async Task InitializeApplicationAsync(AppActivationArguments activatedEventArgs)
    {
        try
        {
            switch (activatedEventArgs.Data)
            {
                case ProtocolActivatedEventArgs protocolArgs:
                    HandleProtocolActivationArgs(protocolArgs);
                    break;
                case ShareTargetActivatedEventArgs shareArgs:
                    MainWindow.AppWindow.Show();
                    MainWindow.Activate();
                    await HandleShareTargetActivation(shareArgs);
                    break;
                default:
                    MainWindow.AppWindow.Show();
                    MainWindow.Activate();
                    break;
            }
        }
        catch (COMException)
        {
            // Data not available 
            // Can happen when share data operation is not completed
            return;
        }
    }

#endif

    private void HookEventsForWindow()
    {
#if WINDOWS
        MainWindow.Activated += Window_Activated;
        MainWindow.AppWindow.Closing += MainWindow_Closing;
#endif
    }

#if WINDOWS
    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        Console.WriteLine($"[DEBUG] MainWindow_Closing called! HandleClosedEvents={HandleClosedEvents}");
        if (!HandleClosedEvents)
            return;

        if (Ioc.Default.GetService<ISystemTrayService>() is not { IsAvailable: true })
        {
            Console.WriteLine("[DEBUG] MainWindow_Closing: System tray is unavailable; allowing the window to close.");
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }
#endif

    public static void TrayStartScrcpy()
    {
        var device = Ioc.Default.GetRequiredService<IDeviceManager>().ActiveDevice;
        if (device is not null)
            _ = Ioc.Default.GetRequiredService<IScreenMirrorService>().StartScrcpy(device);
    }

    public static void TrayToggleWindow()
    {
        MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
#if WINDOWS
            var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
            var isMinimized = presenter?.State is OverlappedPresenterState.Minimized;

            if (!MainWindow.Visible || isMinimized)
            {
                ShowMainWindow();
                return;
            }

            MainWindow.AppWindow.Hide();
#else
            ShowMainWindow();
#endif
        });
    }

    public static void ShowMainWindow()
    {
#if WINDOWS
        var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
        if (presenter?.State is OverlappedPresenterState.Minimized)
            presenter.Restore();

        MainWindow.AppWindow.Show();
        MainWindow.Activate();
        InteropHelpers.SetForegroundWindow(WindowHandle);
#else
        MainWindow.Activate();
#endif
    }

    public static void TrayExitApplication()
    {
        HandleClosedEvents = false;
        Ioc.Default.GetService<ISystemTrayService>()?.Dispose();
        MainWindow?.Close();
        Current.Exit();
    }

#if WINDOWS
    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState is WindowActivationState.CodeActivated ||
            args.WindowActivationState is WindowActivationState.PointerActivated)
            return;

        ApplicationData.Current.LocalSettings.Values["INSTANCE_ACTIVE"] = -Environment.ProcessId;
    }

    public static async Task HandleShareTargetActivation(ShareTargetActivatedEventArgs args)
    {
        var shareOperation = args.ShareOperation;
        var fileTransferService = Ioc.Default.GetRequiredService<IFileTransferService>();
        var items = await shareOperation.Data.GetStorageItemsAsync();
        shareOperation.ReportDataRetrieved();
        shareOperation.ReportCompleted();
        fileTransferService.SendFilesWithPicker(items);
    }
#endif

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        => AppLifecycleHelper.HandleAppUnhandledException(
            new InvalidOperationException(
                "Failed to load Page " + e.SourcePageType.FullName,
                e.Exception));

    /// <summary>
    /// Opens DeviceSettingsWindow for the specified device.
    /// </summary>
    public static DeviceSettingsWindow OpenDeviceSettingsWindow(PairedDevice device)
    {
        if (DeviceSettingsWindows.TryGetValue(device.Id, out var existingWindow))
        {
            // Window exists, activate it
            existingWindow.Activate();
            return existingWindow;
        }

        // Create new window
        var newWindow = new DeviceSettingsWindow(device);
        DeviceSettingsWindows[device.Id] = newWindow;
        newWindow.Activate();
        return newWindow;
    }

    /// <summary>
    /// Removes DeviceSettingsWindow when it is closed.
    /// </summary>
    public static void RemoveDeviceSettingsWindow(string deviceId)
    {
        DeviceSettingsWindows.Remove(deviceId);
    }
}
