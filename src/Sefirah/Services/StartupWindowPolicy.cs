using Sefirah.Data.Enums;

namespace Sefirah.Services;

public enum InitialWindowState
{
    Default,
    Hidden,
    Minimized,
    Maximized
}

/// <summary>
/// Keeps launch-source detection separate from the user's preferred login behavior.
/// A manual launch must always reveal the app, even when login startup is configured
/// to stay in the tray.
/// </summary>
public static class StartupWindowPolicy
{
    public static InitialWindowState Resolve(bool isLoginStartup, StartupOptions startupOption)
    {
        if (!isLoginStartup || startupOption is StartupOptions.Disabled)
            return InitialWindowState.Default;

        return startupOption switch
        {
            StartupOptions.InTray => InitialWindowState.Hidden,
            StartupOptions.Minimized => InitialWindowState.Minimized,
            StartupOptions.Maximized => InitialWindowState.Maximized,
            _ => InitialWindowState.Default
        };
    }
}
