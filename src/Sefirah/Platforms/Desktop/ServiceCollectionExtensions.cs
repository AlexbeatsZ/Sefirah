using Sefirah.Platforms.Desktop.Bluetooth;
using Sefirah.Platforms.Desktop.Features;
using Sefirah.Platforms.Desktop.Mac;
using Sefirah.Platforms.Desktop.Services;

namespace Sefirah.Platforms.Desktop;

/// <summary>
/// Extension methods for registering Desktop-specific services and features.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        services.AddHostedService<Sefirah.Platforms.Windows.Control.ControlApiWorker>();
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IPlatformNotificationHandler, MacNotificationHandler>();
            services.AddFeature<IMediaFeature, MacMediaFeature>();
            services.AddFeature<IBatteryFeature, MacBatteryFeature>();
            services.AddFeature<ISftpFeature, SftpFeature>();
            services.AddSingleton<IAppShortcutService, AppShortcutService>();
            services.AddSingleton<IPhoneLineService, PhoneLineService>();
            services.AddSingleton<IBluetoothPairingService, BluetoothPairingService>();
            services.AddSingleton<ILocalBluetoothController, MacBluetoothController>();
            services.AddSingleton<BluetoothPairingService>(sp => (BluetoothPairingService)sp.GetRequiredService<IBluetoothPairingService>());
            services.AddSingleton<ISystemTrayService, MacSystemTrayService>();
        }
        else
        {
            services.AddSingleton<IPlatformNotificationHandler, NotificationHandler>();
            services.AddFeature<IMediaFeature, MediaFeature>();
            services.AddFeature<IBatteryFeature, BatteryFeature>();
            services.AddFeature<ISftpFeature, SftpFeature>();
            services.AddSingleton<IAppShortcutService, AppShortcutService>();
            services.AddSingleton<IPhoneLineService, PhoneLineService>();
            services.AddSingleton<IBluetoothPairingService, BluetoothPairingService>();
            services.AddSingleton<ILocalBluetoothController, LocalBluetoothController>();
            services.AddSingleton<BluetoothPairingService>(sp => (BluetoothPairingService)sp.GetRequiredService<IBluetoothPairingService>());
            services.AddSingleton<ISystemTrayService, SystemTrayService>();
        }

        return services;
    }
}
