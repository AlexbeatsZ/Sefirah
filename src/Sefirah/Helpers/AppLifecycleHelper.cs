using Sefirah.Data.AppDatabase;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Models;
#if WINDOWS
using Sefirah.Platforms.Windows;
#else
using Sefirah.Platforms.Desktop;
#endif
using Sefirah.Services;
using Sefirah.Services.Transfer;
using Sefirah.Services.Settings;
using Sefirah.Services.Socket;
using Sefirah.ViewModels;
using Sefirah.ViewModels.Settings;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Sefirah.Helpers;

/// <summary>
/// Provides static helper to manage app lifecycle.
/// </summary>
public static class AppLifecycleHelper
{
    /// <summary>
    /// Gets application package version.
    /// </summary>
    public static Version AppVersion { get; } =
        new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision);

    public static async Task InitializeAppComponentsAsync()
    {
        try
        {
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Resolving discoveryService...");
            var discoveryService = Ioc.Default.GetRequiredService<IDiscoveryService>();
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Resolving networkService...");
            var networkService = Ioc.Default.GetRequiredService<INetworkService>();
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Resolving deviceManager...");
            var deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Resolving adbService...");
            var adbService = Ioc.Default.GetRequiredService<IAdbService>();
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Resolving phoneLineService...");
            var phoneLineService = Ioc.Default.GetRequiredService<IPhoneLineService>();
#if WINDOWS
            var notificationHandler = Ioc.Default.GetRequiredService<IPlatformNotificationHandler>();
            await notificationHandler.RegisterForNotifications();
            await Microsoft.Windows.AppNotifications.AppNotificationManager.Default
                .RemoveByTagAndGroupAsync("app-update", "update");
#endif

            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Initializing DeviceManager...");
            await deviceManager.Initialize();

            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Initializing Features...");
            await Task.WhenAll(Ioc.Default.GetServices<IFeature>().Select(feature => feature.InitializeAsync()));

            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Starting NetworkService...");
            await networkService.StartServerAsync();

            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Starting DiscoveryService...");
            await discoveryService.StartDiscoveryAsync();

            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Starting Adb and PhoneLine...");
            _ = Task.WhenAll(
                adbService.StartAsync(),
                phoneLineService.InitializeAsync()
            );
            Console.WriteLine("[DEBUG] InitializeAppComponentsAsync: Complete.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] InitializeAppComponentsAsync failed: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Builds the generic host with Serilog logging and all application services.
    /// </summary>
    public static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs", "Log_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7
            )
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);

        builder.Services.AddSefirahServices();

        return builder.Build();
    }

    public static IServiceCollection AddSefirahServices(this IServiceCollection services)
    {
        return services

                .AddSingleton<ILogger>(sp => sp.GetRequiredService<ILogger<App>>())

                .AddSingleton<IStringLocalizer, ResourceLocalizer>()

                // Settings Services
                .AddSingleton<IUserSettingsService, UserSettingsService>()
                .AddSingleton<IGeneralSettingsService, GeneralSettingsService>(sp => new GeneralSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
                .AddSingleton<IAppThemeModeService, AppThemeModeService>()

                // Database and Repositories
                .AddSingleton<DatabaseContext>()
                .AddSingleton<DeviceRepository>()
                .AddSingleton<RemoteAppRepository>()
                .AddSingleton<ContactRepository>()
                .AddSingleton<SmsRepository>()
                .AddSingleton<CallLogRepository>()
                .AddSingleton<NotificationRepository>()

                // Platform-specific services
                .AddPlatformServices()
                // Services
                .AddSingleton<IDeviceManager, DeviceManager>()
                .AddSingleton(sp => (ITcpServerProvider)sp.GetRequiredService<INetworkService>())
                .AddSingleton(sp => (ISessionManager)sp.GetRequiredService<INetworkService>())
                .AddSingleton<IMdnsService, MdnsService>()
                .AddSingleton<IDiscoveryService, DiscoveryService>()
                .AddSingleton<INetworkService, NetworkService>()
                .AddSingleton<IHeadsetHandoffService, HeadsetHandoffService>()

                .AddFeature<INotificationFeature, NotificationFeature>()
                .AddFeature<IBatteryAlertFeature, BatteryAlertFeature>()
                .AddFeature<IClipboardFeature, ClipboardFeature>()
                .AddFeature<IRemoteMediaFeature, RemoteMediaFeature>()
                .AddFeature<IActionFeature, ActionFeature>()
                .AddSingleton<IFileTransferService, FileTransferService>()
                .AddFeature<ISmsFeature, SmsFeature>()
                .AddFeature<ICallFeature, CallFeature>()

                .AddSingleton<IMessageHandler, MessageHandler>()
                .AddSingleton<Lazy<IMessageHandler>>(sp => new Lazy<IMessageHandler>(() => sp.GetRequiredService<IMessageHandler>()))
                .AddSingleton<IAdbService, AdbService>()
                .AddSingleton<IScreenMirrorService, ScreenMirrorService>()

                // ViewModels
                .AddSingleton<MainPageViewModel>()
                .AddSingleton<DevicesViewModel>()
                .AddSingleton<AppsViewModel>()
                .AddSingleton<MessagesViewModel>()
                .AddSingleton<CallsPageViewModel>()
                .AddSingleton<HeadsetHandoffViewModel>()
                ;
    }

    /// <summary>
    /// Shows exception on the Debug Output.
    /// </summary>
    public static void HandleAppUnhandledException(Exception? ex)
    {
        ILogger? logger = null;
        try
        {
            logger = Ioc.Default.GetService<ILogger>();
        }
        catch (InvalidOperationException)
        {
            // The app can fail before the service provider is configured.
        }

        if (logger is not null)
        {
            logger.LogCritical(ex, "Unhandled exception");
            return;
        }

        Log.Logger.Fatal(ex, "Unhandled exception before dependency injection was initialized");
    }

    public static async Task HandleStartupTaskAsync(bool enable)
    {
#if WINDOWS
        var startupTask = await StartupTask.GetAsync("8B5D3E3F-9B69-4E8A-A9F7-BFCA793B9AF0");

        if (enable)
        {
            if (startupTask.State is StartupTaskState.Disabled)
                await startupTask.RequestEnableAsync();
        }
        else
        {
            if (startupTask.State is StartupTaskState.Enabled)
                startupTask.Disable();
        }
#endif
    }
}
