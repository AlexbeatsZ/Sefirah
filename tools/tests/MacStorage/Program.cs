using System.Net.Http;
using Sefirah.Platforms.Desktop.Mac;
using Sefirah.Data.Models;
using System.Diagnostics;

static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? throw new Exception(key);
var stage = Env("SEFIRAH_STORAGE_STAGE");
var root = Env("SEFIRAH_STORAGE_FIXTURE");
var point = Path.Combine(root, "mounted");
var rclone = Path.Combine(stage, "rclone", "rclone");
var worker = Path.Combine(stage, "rclone", "SefirahStorageWorker");
async Task<MacStorageMount> Start(string path = "/share", string? password = null, string? mountPoint = null) =>
    await MacStorageMount.StartAsync(rclone, worker, "127.0.0.1", int.Parse(Env("SEFIRAH_STORAGE_PORT")),
        "fixture", password ?? Env("SEFIRAH_STORAGE_PASSWORD"), path, mountPoint ?? point, "Sefirah 存储测试");
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
async Task Eventually(Func<bool> condition, string message)
{
    for (int i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
    Check(condition(), message);
}

if (args.Contains("network-loss"))
{
    await using var connected = await Start();
    File.WriteAllText(Path.Combine(root, "drop-sftp"), "disconnect fixture");
    await Eventually(() => File.Exists(Path.Combine(root, "sftp-stopped")), "SFTP server stopped before network-loss probe");
    using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false });
    using var networkResponse = await probe.GetAsync(new Uri(connected.Endpoint, "network-loss.txt"));
    Check(!networkResponse.IsSuccessStatusCode, "failed SFTP download returns HTTP error before sending file body");
    var failed = await Task.Run(() =>
    {
        try { File.ReadAllText(Path.Combine(point, "network-loss.txt")); return false; }
        catch (IOException) { return true; }
    }).WaitAsync(TimeSpan.FromSeconds(30));
    Check(failed, "remote SFTP loss fails file read within bounded time");
    using var uploadResponse = await probe.PutAsync(new Uri(connected.Endpoint, "failed-write.txt"), new StringContent("must fail"));
    Check(!uploadResponse.IsSuccessStatusCode, "remote SFTP loss rejects upload rather than reporting success");
    await connected.DisposeAsync();
    Check(!Directory.Exists(point), "remote SFTP loss still permits native unmount cleanup");
    return;
}

if (args.Contains("hold"))
{
    await using var held = await Start();
    if (args.Contains("open")) await MacStorageMount.RunAsync("/usr/bin/open", [point], null, CancellationToken.None);
    Console.WriteLine("MOUNTED");
    await Task.Delay(Timeout.Infinite);
    return;
}

await using (var mount = await Start())
{
    Check(mount.IsMounted, "native macOS WebDAV volume mounted");
    Check(File.ReadAllText(Path.Combine(point, "原始 [1].txt")) == "phone fixture\n", "read UTF-8 filename through mounted SFTP");
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
    using var denied = await http.GetAsync(new Uri(mount.Endpoint, "/"));
    Check((int)denied.StatusCode == 404, "loopback service rejects missing capability token");
    Directory.CreateDirectory(Path.Combine(point, "新建目录"));
    var file = Path.Combine(point, "新建目录", "写入 [2] #%.txt");
    File.WriteAllText(file, "first write\n");
    await Eventually(() => File.Exists(Path.Combine(root, "share", "新建目录", "写入 [2] #%.txt")), "Finder filesystem create reaches SFTP");
    Check(File.ReadAllText(file) == "first write\n", "read new file");
    File.WriteAllText(file, "overwrite with longer data\n");
    await Eventually(() => File.ReadAllText(Path.Combine(root, "share", "新建目录", "写入 [2] #%.txt")) == "overwrite with longer data\n", "overwrite reaches SFTP before close completes");
    using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
    {
        stream.Position = 5;
        stream.Write("SEEK"u8);
        stream.Flush(flushToDisk: true);
    }
    await Eventually(() => File.ReadAllText(Path.Combine(root, "share", "新建目录", "写入 [2] #%.txt")).Contains("SEEK"), "random writes flush through WebDAV to SFTP");
    var renamed = Path.Combine(point, "新建目录", "重命名.txt");
    File.Move(file, renamed);
    await Eventually(() => File.Exists(Path.Combine(root, "share", "新建目录", "重命名.txt")), "rename reaches SFTP");
    File.Delete(renamed);
    Directory.Delete(Path.Combine(point, "新建目录"));
    await Eventually(() => !Directory.Exists(Path.Combine(root, "share", "新建目录")), "delete file and directory reaches SFTP");
    Check(File.ReadAllText(Path.Combine(root, "outside.txt")) == "untouched", "only advertised storage subtree exposed");
    var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
    var largeFile = Path.Combine(point, "binary.bin");
    using (var stream = new FileStream(largeFile, FileMode.Create, FileAccess.Write))
    {
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }
    Check(File.ReadAllBytes(largeFile).SequenceEqual(payload), "multi-megabyte binary read preserves complete contents");
    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(mount.Endpoint, "binary.bin"));
    rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(8192, 16383);
    using var ranged = await http.SendAsync(rangeRequest);
    Check((int)ranged.StatusCode == 206 && (await ranged.Content.ReadAsByteArrayAsync()).SequenceEqual(payload[8192..16384]), "validated HTTP range read preserves byte offsets");
    Directory.CreateDirectory(Path.Combine(point, "absolute-move"));
    using var moveRequest = new HttpRequestMessage(new HttpMethod("MOVE"), new Uri(mount.Endpoint, "absolute-move/"));
    moveRequest.Headers.Add("Destination", new Uri(mount.Endpoint, "renamed-folder/").AbsoluteUri);
    using var movedFolder = await http.SendAsync(moveRequest);
    Check(movedFolder.IsSuccessStatusCode && Directory.Exists(Path.Combine(root, "share", "renamed-folder")), "absolute WebDAV destination renames folder like Finder");
}
Check(!Directory.Exists(point), "dispose unmounts volume and removes empty mountpoint");
Directory.CreateDirectory(point);
File.WriteAllText(Path.Combine(point, "local.txt"), "keep local data");
var occupiedRejected = false;
try { await using var unexpected = await Start(); }
catch (IOException) { occupiedRejected = true; }
Check(occupiedRejected && File.ReadAllText(Path.Combine(point, "local.txt")) == "keep local data", "occupied local mountpoint is rejected without covering user files");
File.Delete(Path.Combine(point, "local.txt"));
Directory.Delete(point);
await using (var first = await Start())
await using (var second = await Start("/second", mountPoint: Path.Combine(root, "second-mount")))
{
    Check(first.IsMounted && second.IsMounted, "multiple advertised volumes mount independently");
    Check(File.ReadAllText(Path.Combine(second.MountPoint, "second.txt")) == "second volume", "second volume maps to its own SFTP root");
    await MacStorageMount.RunAsync("/sbin/umount", [first.MountPoint], null, CancellationToken.None);
    Check(!first.IsMounted && second.IsMounted, "Finder eject of one volume leaves the other intact");
}
await using (var remounted = await Start()) Check(remounted.IsMounted, "ejected storage can be mounted again");
foreach (var (path, password, name) in new[] {
    ("/share", "incorrect", "wrong password"), ("/missing", Env("SEFIRAH_STORAGE_PASSWORD"), "missing storage root") })
{
    var failed = false;
    try { await using var unexpected = await Start(path, password); }
    catch (IOException) { failed = true; }
    catch (OperationCanceledException) { failed = true; }
    Check(failed && !Directory.Exists(point), name + " fails with mountpoint cleanup");
}
var manager = new TestSessionManager();
var device = new PairedDevice();
var info = new SftpServerInfo { Username = "fixture", Password = Env("SEFIRAH_STORAGE_PASSWORD"),
    Port = int.Parse(Env("SEFIRAH_STORAGE_PORT")), Paths = ["/share", "/second"], PathNames = ["Internal", "SD card"] };
var featureRoot = Path.Combine(root, "feature");
int Volumes() => DriveInfo.GetDrives().Count(d => d.Name.StartsWith(featureRoot + "/", StringComparison.Ordinal));
using (var feature = new MacSftpFeature(new TestLogger<MacSftpFeature>(), manager, stage, () => featureRoot))
{
    await feature.InitializeAsync();
    await feature.Mount(device, info);
    Check(Volumes() == 2, "feature mounts each advertised storage volume");
    Check(DriveInfo.GetDrives().Any(d => d.Name.TrimEnd('/') == Path.Combine(featureRoot, "测试手机 - device", "Internal")), "friendly device and share paths match Windows without device IDs");
    var workers = Process.GetProcessesByName("SefirahStorageWorker").Select(p => { using (p) return p.Id; }).Order().ToArray();
    await feature.Mount(device, info);
    Check(workers.SequenceEqual(Process.GetProcessesByName("SefirahStorageWorker").Select(p => { using (p) return p.Id; }).Order()), "duplicate connection info reuses existing workers");
    device.DeviceSettings.StorageAccess = false;
    await feature.SetStorageAccessAsync(device);
    Check(Volumes() == 0, "disable storage access unmounts all device volumes");
    device.DeviceSettings.StorageAccess = true;
    await feature.SetStorageAccessAsync(device);
    Check(Volumes() == 2, "re-enable storage access remounts without reconnecting phone");
    var oldRoot = featureRoot;
    featureRoot = Path.Combine(root, "new-location");
    await feature.RefreshStorageLocationAsync();
    Check(Volumes() == 2 && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(oldRoot + "/", StringComparison.Ordinal)), "changing storage location immediately moves active volumes");
    var sameName = new PairedDevice { Id = "other-phone", Name = device.Name };
    await feature.Mount(sameName, info);
    Check(Volumes() == 4, "phones with equal display names use separate device directories");
    feature.Remove(sameName.Id);
    await Eventually(() => Volumes() == 2, "removing same-name device preserves the original mounts");
    if (args.Contains("ui"))
    {
        await feature.BrowseAsync(device);
        Check(Windows.System.Launcher.UriLaunches == 0, "Browse files opens Finder without launching an external SFTP URI");
    }
    await feature.BrowseUriAsync(device);
    Check(Windows.System.Launcher.UriLaunches == 1, "explicit external SFTP entry remains available");
    manager.Disconnect(device);
    await Eventually(() => Volumes() == 0, "phone disconnect unmounts its volumes");
    device.IsConnected = true;
    await feature.Mount(device, info);
    Check(Volumes() == 2, "phone reconnect restores storage volumes");
    await feature.RemoveAll();
    Check(Volumes() == 0, "Remove all storage mounts removes every volume");
    var occupied = Path.Combine(featureRoot, "测试手机 - device");
    Directory.CreateDirectory(occupied);
    File.WriteAllText(Path.Combine(occupied, "local.txt"), "keep local data");
    info.Paths = ["/share"];
    await feature.Mount(device, info);
    Check(Volumes() == 1 && File.ReadAllText(Path.Combine(occupied, "local.txt")) == "keep local data", "single storage uses a safe alternate name when a local device folder contains files");
    await feature.RemoveAll();
    File.Delete(Path.Combine(occupied, "local.txt"));
    Directory.Delete(occupied);
    info.Paths = ["/share", "/missing"];
    await feature.Mount(device, info);
    Check(Volumes() == 0, "failure of one advertised share rolls back partial device mounts");
    await feature.RemoveAll();
    using var stalled = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    stalled.Start();
    info.Paths = ["/share"];
    info.Port = ((System.Net.IPEndPoint)stalled.LocalEndpoint).Port;
    var pendingMount = feature.Mount(device, info);
    await Task.Delay(150);
    manager.Disconnect(device);
    await pendingMount.WaitAsync(TimeSpan.FromSeconds(5));
    await feature.RemoveAll();
    Check(Volumes() == 0, "disconnect cancels startup against a stalled SFTP endpoint");
}
Console.WriteLine("ALL STORAGE CHECKS PASSED");
