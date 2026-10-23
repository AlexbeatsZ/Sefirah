using Sefirah.Data.Models;
using Sefirah.Utils;
using System.Collections.Concurrent;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Mounts phone storage as native Finder network volumes.</summary>
public sealed class MacSftpFeature : ISftpFeature, IDisposable
{
    private readonly ILogger<MacSftpFeature> logger;
    private readonly ISessionManager sessionManager;
    private readonly string resourcesRoot;
    private readonly Func<string> storageRoot;

    public MacSftpFeature(ILogger<MacSftpFeature> logger, ISessionManager sessionManager, IUserSettingsService userSettingsService)
        : this(logger, sessionManager, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
            () => userSettingsService.GeneralSettingsService.RemoteStoragePath) { }

    internal MacSftpFeature(ILogger<MacSftpFeature> logger, ISessionManager sessionManager, string resourcesRoot, string storageRoot)
        : this(logger, sessionManager, resourcesRoot, () => storageRoot) { }

    internal MacSftpFeature(ILogger<MacSftpFeature> logger, ISessionManager sessionManager, string resourcesRoot, Func<string> storageRoot)
    {
        this.logger = logger;
        this.sessionManager = sessionManager;
        this.resourcesRoot = resourcesRoot;
        this.storageRoot = storageRoot;
    }

    private sealed record Session(string Host, string Name, SftpServerInfo Info, CancellationTokenSource Lifetime)
    {
        public List<MacStorageMount> Mounts { get; } = [];
        public string? Directory { get; set; }
    }
    private readonly ConcurrentDictionary<string, Session> sessions = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();

    public Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        return Task.CompletedTask;
    }

    public async Task Mount(PairedDevice device, SftpServerInfo info)
    {
        if (string.IsNullOrEmpty(device.Address) || shutdown.IsCancellationRequested) return;
        await gate.WaitAsync();
        try
        {
            if (!device.IsConnected || shutdown.IsCancellationRequested) return;
            if (sessions.TryGetValue(device.Id, out var previous))
            {
                if (previous.Host == device.Address && previous.Name == device.Name &&
                    previous.Info.Port == info.Port && previous.Info.Username == info.Username &&
                    previous.Info.Password == info.Password && previous.Info.Paths.SequenceEqual(info.Paths) &&
                    previous.Info.PathNames.SequenceEqual(info.PathNames) && device.DeviceSettings.StorageAccess &&
                    previous.Mounts.Count > 0 && previous.Mounts.All(m => m.IsMounted)) return;
                Cancel(previous);
                await UnmountAsync(previous);
                previous.Lifetime.Dispose();
            }
            var session = new Session(device.Address, device.Name, info,
                CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token));
            sessions[device.Id] = session;
            if (device.DeviceSettings.StorageAccess)
                await MountVolumesAsync(device.Id, session);
        }
        finally { gate.Release(); }
    }

    private async Task MountVolumesAsync(string deviceId, Session session)
    {
        var rclone = Path.Combine(resourcesRoot, "rclone", "rclone");
        var worker = Path.Combine(resourcesRoot, "rclone", "SefirahStorageWorker");
        if (!File.Exists(rclone) || !File.Exists(worker) || !File.Exists(Path.Combine(resourcesRoot, "rclone", "SefirahStorageProxy")))
        {
            logger.Warn("Bundled storage bridge is missing; external SFTP browsing remains available.");
            return;
        }
        var paths = session.Info.Paths.Count > 0 ? session.Info.Paths : ["/"];
        try
        {
            var directory = Path.GetFullPath(Path.Combine(storageRoot(), SafeName(session.Name)));
            // Normal paths match Windows: RemoteDevices/phone[/shared storage].
            // Only colliding names need a device suffix; never cover local files.
            if (sessions.Values.Any(s => s != session && string.Equals(s.Directory, directory, StringComparison.OrdinalIgnoreCase)) ||
                IsOccupied(directory, paths.Count == 1))
                directory += " (" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(deviceId)).AsSpan(0, 8)) + ")";
            if (IsOccupied(directory, paths.Count == 1)) throw new IOException("The device storage folder is already in use.");
            session.Directory = directory;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < paths.Count; i++)
            {
                var label = paths.Count == 1 ? session.Name :
                    $"{session.Name} - {(session.Info.PathNames.Count > i ? session.Info.PathNames[i] : $"Volume {i + 1}")}";
                label = SafeName(label);
                var folderName = SafeName(session.Info.PathNames.Count > i ? session.Info.PathNames[i] : "Storage");
                if (!usedNames.Add(folderName)) folderName += $" ({i + 1})";
                session.Mounts.Add(await MacStorageMount.StartAsync(rclone, worker, session.Host,
                    session.Info.Port, session.Info.Username, session.Info.Password, paths[i],
                    paths.Count == 1 ? directory : Path.Combine(directory, folderName), label, session.Lifetime.Token));
            }
            logger.Info($"Mounted {session.Mounts.Count} phone storage volume(s) for {session.Name}.");
        }
        catch (Exception ex)
        {
            await UnmountAsync(session);
            logger.Warn($"Could not mount storage for {session.Name}: {ex.Message} External SFTP browsing remains available.");
        }
    }

    private static bool IsOccupied(string path, bool requireEmpty) => File.Exists(path) ||
        new DirectoryInfo(path).LinkTarget is not null ||
        (requireEmpty && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());

    public async Task RefreshStorageLocationAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var (id, session) in sessions)
            {
                if (session.Lifetime.IsCancellationRequested || session.Mounts.Count == 0) continue;
                await UnmountAsync(session);
                await MountVolumesAsync(id, session);
            }
        }
        catch (Exception ex) { logger.Warn($"Could not change phone storage location: {ex.Message}"); }
        finally { gate.Release(); }
    }

    public async Task BrowseAsync(PairedDevice device)
    {
        string? folder = null;
        await gate.WaitAsync();
        try
        {
            if (sessions.TryGetValue(device.Id, out var session) && device.DeviceSettings.StorageAccess)
            {
                // Finder ejects a volume independently of the control connection.
                if (session.Mounts.Count == 0 || session.Mounts.Any(m => !m.IsMounted))
                {
                    await UnmountAsync(session);
                    await MountVolumesAsync(device.Id, session);
                }
                folder = session.Mounts.Count == 1 ? session.Mounts[0].MountPoint :
                    session.Mounts.Count > 1 ? Path.GetDirectoryName(session.Mounts[0].MountPoint) : null;
            }
        }
        finally { gate.Release(); }
        // Uno's LaunchFolderPathAsync is a stub on macOS. Use the system opener.
        if (folder is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await MacStorageMount.RunAsync("/usr/bin/open", [folder], null, timeout.Token);
        }
        else await BrowseUriAsync(device);
    }

    public async Task BrowseUriAsync(PairedDevice device)
    {
        Session? session;
        await gate.WaitAsync();
        try { sessions.TryGetValue(device.Id, out session); }
        finally { gate.Release(); }
        if (session is null) return;
        var path = session.Info.Paths.Count == 1 ? session.Info.Paths[0] : "/";
        var uri = SftpUriHelper.CreateBrowseUri(session.Host, session.Info.Port,
            session.Info.Username, session.Info.Password, path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(uri.AbsoluteUri);
        Clipboard.SetContent(data);
        await Launcher.LaunchUriAsync(uri);
    }
    public void Remove(string deviceId)
    {
        if (sessions.TryRemove(deviceId, out var session))
        {
            // Cancel a mount attempt immediately, rather than waiting behind it.
            Cancel(session);
            _ = RemoveSessionAsync(session);
        }
    }

    public async Task SetStorageAccessAsync(PairedDevice device)
    {
        if (!device.DeviceSettings.StorageAccess && sessions.TryGetValue(device.Id, out var pending)) Cancel(pending);
        await gate.WaitAsync();
        try
        {
            if (!sessions.TryGetValue(device.Id, out var previous)) return;
            Cancel(previous);
            await UnmountAsync(previous);
            previous.Lifetime.Dispose();
            var session = new Session(previous.Host, device.Name, previous.Info,
                CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token));
            sessions[device.Id] = session;
            if (device.IsConnected && device.DeviceSettings.StorageAccess) await MountVolumesAsync(device.Id, session);
        }
        catch (Exception ex) { logger.Warn($"Could not change phone storage access: {ex.Message}"); }
        finally { gate.Release(); }
    }

    private static void Cancel(Session session)
    {
        try { session.Lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RemoveSessionAsync(Session session)
    {
        await gate.WaitAsync();
        try
        {
            await UnmountAsync(session);
            session.Lifetime.Dispose();
        }
        catch (Exception ex) { logger.Warn($"Could not remove phone storage: {ex.Message}"); }
        finally { gate.Release(); }
    }

    public async Task RemoveAll()
    {
        var removed = new List<Session>();
        foreach (var id in sessions.Keys)
        {
            if (!sessions.TryRemove(id, out var session)) continue;
            Cancel(session);
            removed.Add(session);
        }
        await gate.WaitAsync();
        try
        {
            foreach (var session in removed)
            {
                await UnmountAsync(session);
                session.Lifetime.Dispose();
            }
        }
        finally { gate.Release(); }
    }

    private async Task UnmountAsync(Session session)
    {
        var directory = session.Directory;
        foreach (var mount in session.Mounts)
        {
            try { await mount.DisposeAsync(); }
            catch (Exception ex) { logger.Warn($"Could not unmount phone storage: {ex.Message}"); }
        }
        session.Mounts.Clear();
        session.Directory = null;
        if (directory is not null) { try { Directory.Delete(directory, recursive: false); } catch (IOException) { } }
    }

    private static string SafeName(string value)
    {
        var safe = new string(value.Select(c => c == '/' || c == ':' || char.IsControl(c) ? '-' : c).Take(64).ToArray()).Trim(' ', '.');
        return safe.Length > 0 ? safe : "Storage";
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (!device.IsConnected) Remove(device.Id);
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (shutdown.IsCancellationRequested) return;
        shutdown.Cancel();
        sessionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        // Native workers also watch the parent PID, including SIGKILL/crashes.
        RemoveAll().Wait(TimeSpan.FromSeconds(10));
    }
}
