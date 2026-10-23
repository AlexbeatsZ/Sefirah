using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// SFTP -> private loopback WebDAV -> the macOS built-in network filesystem.
/// Deliberately independent of Uno so the actual mount can be integration-tested.
/// </summary>
internal sealed class MacStorageMount : IAsyncDisposable
{
    private readonly Process worker;
    private readonly Task output;
    private readonly Task errors;
    private MacStorageProxy? proxy;
    private int disposed;
    public string MountPoint { get; }
    public Uri Endpoint { get; private set; } = null!;
    public bool IsMounted => !worker.HasExited && proxy is { IsRunning: true } && DriveInfo.GetDrives().Any(d =>
        string.Equals(Path.TrimEndingDirectorySeparator(d.Name), MountPoint, StringComparison.Ordinal));

    private MacStorageMount(Process worker, string mountPoint, Task output, Task errors)
    {
        this.worker = worker;
        MountPoint = mountPoint;
        this.output = output;
        this.errors = errors;
    }

    public static async Task<MacStorageMount> StartAsync(
        string rclone, string workerExecutable, string host, int port, string username,
        string password, string remotePath, string mountPoint, string volumeName,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var ct = timeout.Token;
        // rclone expects its reversible password encoding. Feed stdin, never argv
        // or a config file; the bridge receives the result in its environment.
        var obscured = await RunAsync(rclone, ["obscure", "-"], password, ct);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var cacheDirectory = Path.Combine("/tmp", ".agents", "sefirah-storage-read-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(workerExecutable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] {
            mountPoint, cacheDirectory, rclone, "serve", "webdav", ":sftp:" + remotePath,
            "--config", "/dev/null", "--addr", "127.0.0.1:0", "--baseurl", "/" + token,
            "--dir-cache-time", "2s", "--poll-interval", "0", "--vfs-cache-mode", "off",
            "--buffer-size", "1M", "--sftp-disable-hashcheck", "--sftp-shell-type", "none",
            "--contimeout", "8s", "--timeout", "15s", "--retries", "1", "--low-level-retries", "1",
            "--log-level", "INFO", "--log-format", "",
        }) start.ArgumentList.Add(arg);
        start.Environment["RCLONE_SFTP_HOST"] = host;
        start.Environment["RCLONE_SFTP_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["RCLONE_SFTP_USER"] = username;
        start.Environment["RCLONE_SFTP_PASS"] = obscured.Trim();
        start.Environment.Remove("SSH_AUTH_SOCK");
        // A WebDAV/VFS root can look like an empty directory even when the SFTP
        // path is absent. Confirm the phone's actual directory before mounting.
        await RunAsync(rclone, ["lsf", ":sftp:" + remotePath, "--config", "/dev/null", "--max-depth", "1",
            "--dirs-only", "--sftp-disable-hashcheck", "--sftp-shell-type", "none", "--contimeout", "8s",
            "--timeout", "15s", "--retries", "1", "--low-level-retries", "1"], null, ct, start.Environment);
        if (File.Exists(mountPoint) || new DirectoryInfo(mountPoint).LinkTarget is not null ||
            (Directory.Exists(mountPoint) && Directory.EnumerateFileSystemEntries(mountPoint).Any()))
            throw new IOException("The storage mountpoint must be an empty local directory.");
        Directory.CreateDirectory(mountPoint);
        var worker = Process.Start(start) ?? throw new IOException("Could not start the storage bridge.");
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Drain both pipes for the worker's lifetime. Do not log URLs, passwords,
        // remote filenames or the loopback capability token to the application log.
        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                const string marker = "WebDav Server started on [";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0 && Uri.TryCreate(line[(index + marker.Length)..].TrimEnd(']'), UriKind.Absolute, out var address))
                    ready.TrySetResult(new Uri($"http://127.0.0.1:{address.Port}/{token}/"));
            }
            ready.TrySetException(new IOException("The storage bridge stopped before becoming ready."));
        }
        var mount = new MacStorageMount(worker, mountPoint,
            DrainAsync(worker.StandardOutput), DrainAsync(worker.StandardError));
        try
        {
            var backend = await ready.Task.WaitAsync(ct);
            mount.proxy = await MacStorageProxy.StartAsync(Path.Combine(Path.GetDirectoryName(workerExecutable)!, "SefirahStorageProxy"), backend, cacheDirectory, ct);
            mount.Endpoint = mount.proxy.Endpoint;
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), mount.Endpoint);
            request.Headers.Add("Depth", "1");
            using var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode != 207) throw new IOException("The phone storage is not accessible.");
            await RunAsync("/sbin/mount_webdav", ["-S", "-v", volumeName, "-o", "nodev,nosuid",
                mount.Endpoint.AbsoluteUri, mountPoint], null, ct);
            if (!mount.IsMounted) throw new IOException("macOS did not mount the phone storage.");
            return mount;
        }
        catch
        {
            await mount.DisposeAsync();
            throw;
        }
    }

    internal static async Task<string> RunAsync(string executable, string[] arguments, string? input, CancellationToken ct,
        IDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, RedirectStandardInput = input is not null,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the storage helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), ct); process.StandardInput.Close(); }
            await process.WaitForExitAsync(ct);
            var result = await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new IOException($"Storage helper {Path.GetFileName(executable)} exited with code {process.ExitCode}.");
            return result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Close proxy connections before asking rclone to drain its HTTP server.
        // Otherwise an in-flight DAV request can hold the worker's shutdown open.
        var stopProxy = proxy?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        try
        {
            if (!worker.HasExited)
            {
                Kill(worker.Id, 15); // worker unmounts before stopping rclone
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await worker.WaitForExitAsync(timeout.Token);
            }
            await Task.WhenAll(output, errors, stopProxy);
        }
        finally
        {
            await stopProxy;
            worker.Dispose();
            // Never recurse: a failed unmount must not delete remote contents.
            try { Directory.Delete(MountPoint, recursive: false); } catch (IOException) { }
        }
    }

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
