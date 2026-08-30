using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace Sefirah.Platforms.Windows.Helpers;

public static class WindowExtensions
{
    /// <summary>
    /// Applies the packaged application icon to the window's title bar and shell surfaces.
    /// </summary>
    public static void SetWindowIcon(this Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "SefirahDark.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
    }
}
