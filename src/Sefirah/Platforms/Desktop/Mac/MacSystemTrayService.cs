namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="ISystemTrayService"/>. The native bridge owns
/// the AppKit objects and sends status-item clicks back to the managed UI dispatcher.
/// </summary>
public sealed class MacSystemTrayService : ISystemTrayService
{
    private readonly ILogger<MacSystemTrayService> _logger;
    private bool disposed;

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
            IsAvailable = MacAppLifecycleHelper.CreateStatusItem();
            if (IsAvailable)
                _logger.Info("macOS status bar item initialized with a main-window action");
            else
                _logger.Warn("macOS status bar item could not be initialized");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to create macOS status bar item: {ex.Message}");
            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        MacAppLifecycleHelper.RemoveStatusItem();
        IsAvailable = false;
    }
}
