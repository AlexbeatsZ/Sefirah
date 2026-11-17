using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Sefirah.Services;

/// <summary>Coordinates USB-to-Wi-Fi setup without restarting adbd on every device event.</summary>
public sealed class WirelessAdbSetup(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> deviceLocks = new();
    private readonly ConcurrentDictionary<string, (string BootId, DateTimeOffset Time)> enableAttempts = new();
    private static readonly TimeSpan EnableRetryDelay = TimeSpan.FromMinutes(1);

    public async Task<string?> ConnectAsync(
        string serial,
        Func<string, Task<string>> readShell,
        Func<string, int, Task<bool>> connect,
        Func<Task<bool>> enableTcpip,
        Action<string> log)
    {
        var deviceLock = deviceLocks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        if (!await deviceLock.WaitAsync(0))
            return null;

        try
        {
            var host = GetWifiAddress(await readShell("ip -o -4 addr show wlan0"));
            if (host is null)
            {
                log($"Skipping wireless ADB for {serial}: no current Wi-Fi IPv4 address");
                return null;
            }

            var port = GetTcpPort(await readShell("getprop service.adb.tcp.port"));
            if (port is null)
                port = GetTcpPort(await readShell("getprop persist.adb.tcp.port"));

            var targetPort = port ?? 5555;
            var endpoint = $"{host}:{targetPort}";
            if (await connect(host, targetPort))
                return endpoint;

            // An unreachable listener does not mean TCP mode is disabled.
            // Reissuing tcpip here would reset both USB and wireless transports.
            if (port is not null)
            {
                log($"Wireless ADB connection to {endpoint} failed; TCP mode is already enabled on {serial}");
                return null;
            }

            var bootId = (await readShell("cat /proc/sys/kernel/random/boot_id")).Trim();
            var now = clock.GetUtcNow();
            if (enableAttempts.TryGetValue(serial, out var previous) &&
                previous.BootId == bootId && now - previous.Time < EnableRetryDelay)
            {
                log($"Deferring repeated wireless ADB enable attempt for {serial}");
                return null;
            }

            // Record before executing: the command itself generates disconnect/online events.
            enableAttempts[serial] = (bootId, now);
            if (!await enableTcpip())
                return null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(500 * attempt);
                if (await connect(host, targetPort))
                    return endpoint;
            }

            log($"Wireless ADB connection to {endpoint} failed after enabling TCP mode on {serial}");
            return null;
        }
        finally
        {
            deviceLock.Release();
        }
    }

    internal static string? GetWifiAddress(string output)
    {
        foreach (Match match in Regex.Matches(output, @"\binet\s+([0-9.]+)/"))
        {
            if (IPAddress.TryParse(match.Groups[1].Value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                return address.ToString();
        }
        return null;
    }

    private static int? GetTcpPort(string output) =>
        int.TryParse(output.Trim(), out var port) && port is > 0 and <= 65535 ? port : null;
}
