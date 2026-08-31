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
        InitializeComponent();
        // Configure exception handlers
        UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            AppLifecycleHelper.HandleAppUnhandledException(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = LogStartupFailuresAsync(ActivateAsync());

        async Task LogStartupFailuresAsync(Task activation)
        {
            try
            {
                await activation;
            }
            catch (Exception ex)
            {
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
            // Configure logging before creating the window so native shell failures
            // are persisted, but do not start hosted workers until MainWindow exists.
            Host = AppLifecycleHelper.BuildHost();
            Ioc.Default.ConfigureServices(Host.Services);

            MainWindow = new Window();
            MainWindow.AppWindow.Title = "Sefirah";
            MainWindow.SetWindowIcon();
#if WINDOWS
            WindowHandle = WindowNative.GetWindowHandle(MainWindow);
            MainWindow.ExtendsContentIntoTitleBar = true;
#endif
            await Host.StartAsync();

            bool isStartupTask = false;
#if WINDOWS
            var appActivationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            isStartupTask = appActivationArguments.Data is IStartupTaskActivatedEventArgs;

            bool isStartupRegistered = ApplicationData.Current.LocalSettings.Values["isStartupRegistered"] is null;
            if (isStartupRegistered)
            {
                await AppLifecycleHelper.HandleStartupTaskAsync(true);
                ApplicationData.Current.LocalSettings.Values["isStartupRegistered"] = true;
            }

            if (appActivationArguments.Data is ProtocolActivatedEventArgs protocolArgs)
                HandleProtocolActivationArgs(protocolArgs);
#endif
            HookEventsForWindow();
            _ = Ioc.Default.GetRequiredService<ISystemTrayService>();

            var rootFrame = EnsureWindowIsInitialized();
            if (rootFrame is null)
                return;

            Ioc.Default.GetRequiredService<IAppThemeModeService>().ManageAppearance(MainWindow);

            if (isStartupTask)
            {
                var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
                var startupOption = userSettingsService.GeneralSettingsService.StartupOption;
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
                    default:
                        MainWindow.Activate();
                        MainWindow.AppWindow.Show();
                        break;
                };
            }
            else
            {
                MainWindow.Activate();
                // Wait for the Window to initialize
                await Task.Delay(10);
                MainWindow.AppWindow.Show();
            }

            rootFrame.Navigate(typeof(Views.SplashScreen));

            await Task.WhenAll(
                AppLifecycleHelper.InitializeAppComponentsAsync(),
                Task.Delay(500));

            bool isOnboarding = ApplicationData.Current.LocalSettings.Values["HasCompletedOnboarding"] == null;
            if (isOnboarding)
            {
                // Navigate to onboarding page
                rootFrame.Navigate(typeof(WelcomePage), null, new SuppressNavigationTransitionInfo());
            }
            else
            {
                // Navigate to main page
                rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
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
#endif
        MainWindow.Closed += Window_Closed;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (!HandleClosedEvents)
            return;

        if (Ioc.Default.GetService<ISystemTrayService>() is not { IsAvailable: true })
            return;

        args.Handled = true;
        MainWindow.AppWindow.Hide();
    }

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
            var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
            var isMinimized = presenter?.State is OverlappedPresenterState.Minimized;

            if (!MainWindow.Visible || isMinimized)
            {
                ShowMainWindow();
                return;
            }

            MainWindow.AppWindow.Hide();
        });
    }

    public static void ShowMainWindow()
    {
        var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
        if (presenter?.State is OverlappedPresenterState.Minimized)
            presenter.Restore();

        MainWindow.AppWindow.Show();
        MainWindow.Activate();
#if WINDOWS
        InteropHelpers.SetForegroundWindow(WindowHandle);
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
