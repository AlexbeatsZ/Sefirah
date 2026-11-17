namespace Sefirah.Data.Models;

public static class AdbDeviceIdentity
{
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
