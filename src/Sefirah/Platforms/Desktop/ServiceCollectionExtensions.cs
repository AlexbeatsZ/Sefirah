using Sefirah.Platforms.Desktop.Bluetooth;
using Sefirah.Platforms.Desktop.Features;
using Sefirah.Platforms.Desktop.Services;
using Sefirah.Platforms.Desktop.Mac;

namespace Sefirah.Platforms.Desktop;

/// <summary>
/// Extension methods for registering Desktop-specific services and features.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IPlatformNotificationHandler, MacNotificationHandler>();
            services.AddFeature<IMediaFeature, MediaFeature>();
            services.AddFeature<IAudioFeature, MacAudioFeature>();
            services.AddFeature<IBatteryFeature, MacBatteryFeature>();
            services.AddFeature<ISftpFeature, MacSftpFeature>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IAppShortcutService, AppShortcutService>();
            services.AddSingleton<IPhoneLineService, PhoneLineService>();
            services.AddSingleton<IBluetoothPairingService, BluetoothPairingService>();
            services.AddSingleton<BluetoothPairingService>(sp => (BluetoothPairingService)sp.GetRequiredService<IBluetoothPairingService>());
            services.AddSingleton<ISystemTrayService, MacSystemTrayService>();
            return services;
        }
        services.AddSingleton<IPlatformNotificationHandler, NotificationHandler>();
        services.AddFeature<IMediaFeature, MediaFeature>();
        services.AddFeature<IAudioFeature, AudioFeature>();
        services.AddFeature<IBatteryFeature, BatteryFeature>();
        services.AddFeature<ISftpFeature, SftpFeature>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAppShortcutService, AppShortcutService>();
        services.AddSingleton<IPhoneLineService, PhoneLineService>();
        services.AddSingleton<IBluetoothPairingService, BluetoothPairingService>();
        services.AddSingleton<BluetoothPairingService>(sp => (BluetoothPairingService)sp.GetRequiredService<IBluetoothPairingService>());
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        return services;
    }
}
