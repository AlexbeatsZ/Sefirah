using System.Runtime.InteropServices;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="ISystemTrayService"/> creating an NSStatusItem
/// in the macOS menu bar via AppKit P/Invoke.
/// </summary>
public sealed class MacSystemTrayService : ISystemTrayService
{
    private readonly ILogger<MacSystemTrayService> _logger;
    private nint _statusItem;

    public bool IsAvailable { get; private set; }

    public MacSystemTrayService(ILogger<MacSystemTrayService> logger)
    {
        _logger = logger;
        if (OperatingSystem.IsMacOS())
        {
            InitializeTray();
        }
    }

    private void InitializeTray()
    {
        try
        {
            var clsStatusBar = objc_getClass("NSStatusBar");
            if (clsStatusBar == 0) return;

            var selSystemStatusBar = sel_registerName("systemStatusBar");
            var statusBar = objc_msgSend(clsStatusBar, selSystemStatusBar);
            if (statusBar == 0) return;

            var selStatusItem = sel_registerName("statusItemWithLength:");
            // NSSquareStatusItemLength = -2
            _statusItem = objc_msgSend_nfloat(statusBar, selStatusItem, -2);
            if (_statusItem == 0) return;

            var selButton = sel_registerName("button");
            var button = objc_msgSend(_statusItem, selButton);
            if (button != 0)
            {
                var nsStringCls = objc_getClass("NSString");
                var selUtf8 = sel_registerName("stringWithUTF8String:");
                var strTitle = Marshal.StringToCoTaskMemUTF8("⚡");
                try
                {
                    var nsTitle = objc_msgSend_IntPtr(nsStringCls, selUtf8, strTitle);
                    var selSetTitle = sel_registerName("setTitle:");
                    objc_msgSend_void_IntPtr(button, selSetTitle, nsTitle);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(strTitle);
                }
            }

            IsAvailable = true;
            _logger.Info("macOS status bar item initialized");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to create macOS status bar item: {ex.Message}");
            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        if (_statusItem != 0)
        {
            try
            {
                var clsStatusBar = objc_getClass("NSStatusBar");
                var selSystemStatusBar = sel_registerName("systemStatusBar");
                var statusBar = objc_msgSend(clsStatusBar, selSystemStatusBar);
                var selRemove = sel_registerName("removeStatusItem:");
                objc_msgSend_void_IntPtr(statusBar, selRemove, _statusItem);
            }
            catch
            {
                // ignore
            }
            _statusItem = 0;
            IsAvailable = false;
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nfloat(nint receiver, nint selector, double arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_IntPtr(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(nint receiver, nint selector, nint arg);
}
