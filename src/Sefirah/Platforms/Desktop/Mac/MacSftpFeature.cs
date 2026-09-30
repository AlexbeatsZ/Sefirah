using Sefirah.Data.Models;
using Sefirah.Utils;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Exposes SFTP links without trying to run Linux mount utilities on macOS.</summary>
public sealed class MacSftpFeature : ISftpFeature
{
    private readonly Dictionary<string, (string Host, SftpServerInfo Info)> sessions = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public Task Mount(PairedDevice device, SftpServerInfo info)
    {
        if (!string.IsNullOrEmpty(device.Address)) sessions[device.Id] = (device.Address, info);
        return Task.CompletedTask;
    }
    public Task BrowseAsync(PairedDevice device) => BrowseUriAsync(device);
    public async Task BrowseUriAsync(PairedDevice device)
    {
        if (!sessions.TryGetValue(device.Id, out var session)) return;
        var path = session.Info.Paths.Count == 1 ? session.Info.Paths[0] : "/";
        var uri = SftpUriHelper.CreateBrowseUri(session.Host, session.Info.Port,
            session.Info.Username, session.Info.Password, path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(uri.AbsoluteUri);
        Clipboard.SetContent(data);
        await Launcher.LaunchUriAsync(uri);
    }
    public void Remove(string deviceId) => sessions.Remove(deviceId);
    public Task RemoveAll() { sessions.Clear(); return Task.CompletedTask; }
}
