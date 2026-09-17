using System.Runtime.InteropServices;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// Bridges the Uno window to the AppKit lifecycle that a persistent macOS menu-bar app needs.
/// The native bridge is packaged next to the managed desktop executable.
/// </summary>
public static class MacAppLifecycleHelper
{
    private const string NativeLibrary = "libSefirahMacLifecycle.dylib";
    private const string LoginStartupArgument = "--login-startup";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCallback();

    private static readonly NativeCallback ReopenCallback = OnOpenRequested;
    private static readonly NativeCallback StatusItemCallback = OnOpenRequested;
    private static bool reopenHandlerAttempted;

    public static bool IsLoginStartup { get; private set; }

    public static void SetLaunchArguments(IEnumerable<string> args)
        => IsLoginStartup = args.Contains(LoginStartupArgument, StringComparer.Ordinal);

    public static void EnsureReopenHandler()
    {
        if (!OperatingSystem.IsMacOS() || reopenHandlerAttempted)
            return;

        reopenHandlerAttempted = true;
        try
        {
            var result = InstallReopenHandler(ReopenCallback);
            Console.WriteLine($"[DEBUG] macOS application reopen handler result: {result}.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] macOS lifecycle bridge is unavailable: {ex.Message}");
        }
    }

    public static bool CreateStatusItem()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            return CreateStatusItemNative(StatusItemCallback) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not create the macOS status item: {ex.Message}");
            return false;
        }
    }

    public static void RemoveStatusItem()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            RemoveStatusItemNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not remove the macOS status item: {ex.Message}");
        }
    }

    public static bool IsMainWindowVisible()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            return IsMainWindowVisibleNative() != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not inspect the macOS main window: {ex.Message}");
            return false;
        }
    }

    public static void ShowMainWindow()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            ShowMainWindowNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not show the macOS main window: {ex.Message}");
        }
    }

    public static void HideMainWindow()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            HideMainWindowNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not hide the macOS main window: {ex.Message}");
        }
    }

    /// <summary>
    /// Reconciles the per-user launch agent. Its embedded launcher starts Sefirah with an
    /// explicit argument so a login launch can be distinguished from a user opening the app.
    /// </summary>
    public static int ReconcileLaunchAtLogin(bool enable)
    {
        if (!OperatingSystem.IsMacOS())
            return -1;

        try
        {
            return ReconcileLaunchAtLoginNative(enable ? 1 : 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WARN] Could not reconcile macOS launch at login: {ex.Message}");
            return -2;
        }
    }

    private static void OnOpenRequested()
    {
        try
        {
            if (App.Current is App && App.MainWindow is not null)
                App.MainWindow.DispatcherQueue.TryEnqueue(App.ShowMainWindow);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] macOS open request failed: {ex.Message}");
        }
    }

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_install_reopen_handler")]
    private static extern int InstallReopenHandler(NativeCallback callback);

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_create_status_item")]
    private static extern int CreateStatusItemNative(NativeCallback callback);

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_remove_status_item")]
    private static extern void RemoveStatusItemNative();

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_main_window_is_visible")]
    private static extern int IsMainWindowVisibleNative();

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_show_main_window")]
    private static extern void ShowMainWindowNative();

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_hide_main_window")]
    private static extern void HideMainWindowNative();

    [DllImport(NativeLibrary, EntryPoint = "sefirah_macos_reconcile_launch_at_login")]
    private static extern int ReconcileLaunchAtLoginNative(int enable);
}
