using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Owns the bundled download-validation proxy and its private read cache.</summary>
internal sealed class MacStorageProxy : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task output;
    private readonly Task errors;
    private readonly string cacheDirectory;
    public Uri Endpoint { get; }
    public bool IsRunning => !process.HasExited;

    private MacStorageProxy(Process process, Uri endpoint, string cacheDirectory, Task output, Task errors)
    {
        this.process = process;
        Endpoint = endpoint;
        this.cacheDirectory = cacheDirectory;
        this.output = output;
        this.errors = errors;
    }

    public static async Task<MacStorageProxy> StartAsync(string executable, Uri backend, string cacheDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(backend.AbsoluteUri);
        start.ArgumentList.Add(cacheDirectory);
        var process = Process.Start(start) ?? throw new IOException("Could not start the storage download validator.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (!Uri.TryCreate(line, UriKind.Absolute, out var endpoint) || endpoint.Host != "127.0.0.1")
                throw new IOException("The storage download validator did not start.");
            return new MacStorageProxy(process, endpoint, cacheDirectory, process.StandardOutput.ReadToEndAsync(), errors);
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                Kill(process.Id, 15);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            await Task.WhenAll(output, errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
            try { Directory.Delete(cacheDirectory, recursive: false); } catch (IOException) { }
        }
    }

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
