using System.Text.RegularExpressions;

namespace Sefirah.Services;

public static class BluetoothDeviceIdentity
{
    private static readonly Regex AddressPattern = new(
        @"(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}",
        RegexOptions.Compiled);

    public static string CatalogKey(string? bluetoothAddress, string deviceKey, string displayName) =>
        ResolveAddress(bluetoothAddress, deviceKey) is { } address
            ? $"address:{address}"
            : $"name:{displayName.Trim()}";

    public static string? ResolveAddress(string? bluetoothAddress, string? deviceKey) =>
        NormalizeAddress(bluetoothAddress) ?? ExtractAddress(deviceKey);

    public static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 12 ? normalized : null;
    }

    public static string? ExtractAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var matches = AddressPattern.Matches(value);
        return matches.Count == 0 ? NormalizeAddress(value) : NormalizeAddress(matches[^1].Value);
    }
}
