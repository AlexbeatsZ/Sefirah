namespace Sefirah.Data.Models;

public static class AdbDeviceIdentity
{
    public static string ResolveAuthenticatedId(string? wifiAddress, IEnumerable<(string Id, string Address)> connectedPeers)
    {
        if (string.IsNullOrWhiteSpace(wifiAddress)) return string.Empty;
        var matches = connectedPeers.Where(peer => peer.Address == wifiAddress)
            .Select(peer => peer.Id).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : string.Empty;
    }

    public static bool Matches(string deviceId, string androidId, string serial, IEnumerable<string> addresses)
    {
        if (!string.IsNullOrWhiteSpace(androidId))
            return string.Equals(deviceId, androidId, StringComparison.Ordinal);

        // Legacy Wi-Fi transports can still be associated by their endpoint.
        // A USB serial/model cannot establish the identity of a paired app.
        var separator = serial.LastIndexOf(':');
        return separator > 0 && addresses.Contains(serial[..separator], StringComparer.OrdinalIgnoreCase);
    }
}
