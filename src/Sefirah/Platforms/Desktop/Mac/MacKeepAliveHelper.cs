using System.Runtime.InteropServices;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// Configures native macOS AppKit lifecycle callbacks to prevent the application
/// from terminating when the window is closed or hidden, keeping the app alive in the status bar.
/// </summary>
public static class MacKeepAliveHelper
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool ShouldTerminateCallback();

    [DllImport("libUnoNativeMac.dylib", EntryPoint = "uno_set_application_should_terminate_after_last_window_closed_callback")]
    private static extern void SetShouldTerminateCallback(ShouldTerminateCallback callback);

    private static readonly ShouldTerminateCallback KeepAliveDelegate = () =>
    {
        Console.WriteLine("[DEBUG] applicationShouldTerminateAfterLastWindowClosed invoked -> returning false to stay alive in menu bar.");
        return false;
    };

    private static bool _initialized;

    public static void EnsureMacAppKeepsRunning()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            SetShouldTerminateCallback(KeepAliveDelegate);
            if (!_initialized)
            {
                _initialized = true;
                Console.WriteLine("[DEBUG] Set application_should_terminate_after_last_window_closed to false.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not set terminate callback: {ex.Message}");
        }
    }
}
