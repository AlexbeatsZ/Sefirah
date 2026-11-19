using System.Text.RegularExpressions;
using AdvancedSharpAdbClient.Models;

namespace Sefirah.Services;

/// <summary>
/// Parses <c>host:devices-l</c> / <c>host:track-devices-l</c> replies without the
/// AdvancedSharpAdbClient serial regex, which rejects valid mDNS instance names such as
/// <c>adb-xxxx (2)._adb-tls-connect._tcp</c> and aborts the whole device list.
/// </summary>
public static partial class AdbDeviceList
{
    private static readonly string[] Properties = ["usb", "product", "model", "device", "features", "transport_id"];

    // The serial is the shortest prefix followed by a known adb state token, so serials
    // may contain spaces and parentheses (duplicate mDNS service instances).
    [GeneratedRegex(@"^(?<serial>\S(?:.*?\S)?)\s+(?<state>device|connecting|offline|unknown|bootloader|recovery|rescue|sideload|download|authorizing|unauthorized|host|detached|no permissions)(?:\s+(?<rest>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();

    public static DeviceData? ParseLine(string line)
    {
        var match = LineRegex().Match(line.Trim());
        if (!match.Success) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var message = new List<string>();
        foreach (var token in match.Groups["rest"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            var key = separator > 0 ? token[..separator] : string.Empty;
            if (Properties.Contains(key, StringComparer.OrdinalIgnoreCase))
                values[key] = token[(separator + 1)..];
            else
                message.Add(token);
        }

        return new DeviceData
        {
            Serial = match.Groups["serial"].Value,
            State = DeviceData.GetStateFromString(match.Groups["state"].Value),
            Usb = values.GetValueOrDefault("usb", string.Empty),
            Product = values.GetValueOrDefault("product", string.Empty),
            Model = values.GetValueOrDefault("model", string.Empty),
            Name = values.GetValueOrDefault("device", string.Empty),
            Features = values.GetValueOrDefault("features", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries),
            TransportId = values.GetValueOrDefault("transport_id", string.Empty),
            Message = string.Join(' ', message),
        };
    }

    /// <summary>Parses every line, reporting (not throwing on) lines that are not device entries.</summary>
    public static List<DeviceData> Parse(string? reply, Action<string>? onInvalidLine = null)
    {
        List<DeviceData> devices = [];
        foreach (var line in (reply ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var device = ParseLine(line);
            if (device is null)
                onInvalidLine?.Invoke(line);
            else if (devices.All(existing => existing.Serial != device.Serial))
                devices.Add(device);
        }
        return devices;
    }

    public sealed record Changes(
        IReadOnlyList<DeviceData> Connected,
        IReadOnlyList<DeviceData> Disconnected,
        IReadOnlyList<(DeviceData Device, DeviceState OldState)> Changed);

    /// <summary>Same transitions as DeviceMonitor.UpdateDevices, keyed by serial.</summary>
    public static Changes Diff(IReadOnlyCollection<DeviceData> previous, IReadOnlyCollection<DeviceData> current)
    {
        var old = previous.ToDictionary(device => device.Serial);
        var now = current.ToDictionary(device => device.Serial);
        return new Changes(
            current.Where(device => !old.ContainsKey(device.Serial)).ToList(),
            previous.Where(device => !now.ContainsKey(device.Serial)).ToList(),
            current.Where(device => old.TryGetValue(device.Serial, out var before) && before.State != device.State)
                .Select(device => (device, old[device.Serial].State)).ToList());
    }
}
