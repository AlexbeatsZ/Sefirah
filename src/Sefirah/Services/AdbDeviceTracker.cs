using System.Net;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace Sefirah.Services;

/// <summary>
/// Replacement for AdvancedSharpAdbClient's DeviceMonitor. The library monitor parses each
/// device with a strict serial regex and its loop dies on the first ArgumentException, so one
/// duplicated mDNS transport stops all device tracking. This tracker uses
/// <see cref="AdbDeviceList"/>, skips unparsable lines and reconnects after socket failures.
/// </summary>
public sealed class AdbDeviceTracker(Func<Task> ensureServerAsync, Action<string, Exception?> log) : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource cts = new();
    private readonly HashSet<string> reportedInvalidLines = [];
    private List<DeviceData> devices = [];
    private Task? loop;

    public event EventHandler<DeviceDataConnectEventArgs>? DeviceConnected;
    public event EventHandler<DeviceDataConnectEventArgs>? DeviceDisconnected;
    public event EventHandler<DeviceDataChangeEventArgs>? DeviceChanged;

    public static async Task<List<DeviceData>> GetDevicesAsync(Action<string>? onInvalidLine, CancellationToken cancellationToken = default)
    {
        using var socket = CreateSocket();
        await socket.SendAdbRequestAsync("host:devices-l", cancellationToken);
        _ = await socket.ReadAdbResponseAsync(cancellationToken);
        return AdbDeviceList.Parse(await socket.ReadStringAsync(cancellationToken), onInvalidLine);
    }

    /// <summary>Starts tracking; <paramref name="knownDevices"/> are already handled by the caller.</summary>
    public void Start(IEnumerable<DeviceData> knownDevices)
    {
        if (loop is not null) return;
        devices = [.. knownDevices];
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    private static AdbSocket CreateSocket() => new(new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort));

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = CreateSocket();
                using var registration = token.Register(socket.Dispose);
                await socket.SendAdbRequestAsync("host:track-devices-l", token);
                _ = await socket.ReadAdbResponseAsync(token);
                while (!token.IsCancellationRequested)
                    Apply(await socket.ReadStringAsync(token));
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log("ADB device tracking interrupted; reconnecting", ex);
            }

            try
            {
                await Task.Delay(RetryDelay, token);
                await ensureServerAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log("ADB server restart failed", ex);
            }
        }
    }

    private void Apply(string reply)
    {
        var current = AdbDeviceList.Parse(reply, line =>
        {
            if (reportedInvalidLines.Add(line))
                log($"Ignoring unparsable adb device entry '{line}'", null);
        });
        var changes = AdbDeviceList.Diff(devices, current);
        devices = current;

        foreach (var device in changes.Disconnected)
            Raise(() => DeviceDisconnected?.Invoke(this, new DeviceDataConnectEventArgs(device, false)));
        foreach (var (device, oldState) in changes.Changed)
            Raise(() => DeviceChanged?.Invoke(this, new DeviceDataChangeEventArgs(device, device.State, oldState)));
        foreach (var device in changes.Connected)
            Raise(() => DeviceConnected?.Invoke(this, new DeviceDataConnectEventArgs(device, true)));
    }

    private void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception ex) { log("ADB device event handler failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        if (loop is not null)
        {
            try { await loop; } catch { }
        }
        cts.Dispose();
    }
}
