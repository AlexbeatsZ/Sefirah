namespace Sefirah.Helpers;

public static class WindowExtensions
{
    /// <summary>
    /// Applies the packaged application icon to the window's title bar and shell surfaces.
    /// </summary>
#if !HAS_UNO
    public static void SetWindowIcon(this Window window)
    {
#if WINDOWS
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "SefirahDark.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
#endif
    }
#endif
}
