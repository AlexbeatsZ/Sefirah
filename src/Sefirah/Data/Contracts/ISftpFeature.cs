using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface ISftpFeature : IFeature
{
    /// <summary>
    /// Starts device storage access (macOS network volume, Linux GVfs/sshfs, or Windows sync root).
    /// </summary>
    Task Mount(PairedDevice device, SftpServerInfo info);

    /// <summary>
    /// Opens the device filesystem via the preferred local mount/sync path when available.
    /// </summary>
    Task BrowseAsync(PairedDevice device);

    /// <summary>
    /// Launches an sftp:// URI for an external client and copies it to the clipboard.
    /// </summary>
    Task BrowseUriAsync(PairedDevice device);

    /// <summary>Reconciles an in-session storage access setting change.</summary>
    Task SetStorageAccessAsync(PairedDevice device)
    {
        if (!device.DeviceSettings.StorageAccess) Remove(device.Id);
        return Task.CompletedTask;
    }

    void Remove(string deviceId);

    /// <summary>Moves active native volumes after changing their base directory.</summary>
    Task RefreshStorageLocationAsync() => Task.CompletedTask;

    Task RemoveAll();
}
