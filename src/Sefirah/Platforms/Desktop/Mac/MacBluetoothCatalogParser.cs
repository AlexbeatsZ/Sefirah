using System.Text.Json;
using Sefirah.Services;

namespace Sefirah.Platforms.Desktop.Mac;

internal sealed record MacBluetoothCatalogSnapshot(
    bool ControllerAvailable,
    bool RadioEnabled,
    IReadOnlyList<MacBluetoothCatalogDevice> Devices);

internal sealed record MacBluetoothCatalogDevice(
    string DeviceKey,
    string DisplayName,
    string BluetoothAddress,
    bool IsConnected,
    bool IsHeadset);

internal static class MacBluetoothCatalogParser
{
    private static readonly (string Key, bool? IsConnected)[] DeviceGroups =
    [
        ("device_connected", true),
        ("connected_devices", true),
        ("device_not_connected", false),
        ("not_connected_devices", false),
        ("devices_list", null),
    ];

    public static MacBluetoothCatalogSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("SPBluetoothDataType", out var controllers) ||
            controllers.ValueKind is not JsonValueKind.Array ||
            controllers.GetArrayLength() == 0)
        {
            return new MacBluetoothCatalogSnapshot(false, false, []);
        }

        var root = controllers[0];
        var radioEnabled = root.TryGetProperty("controller_properties", out var controller) &&
                           controller.TryGetProperty("controller_state", out var state) &&
                           IsTrue(state);
        var devices = new Dictionary<string, MacBluetoothCatalogDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, groupConnectionState) in DeviceGroups)
        {
            if (!root.TryGetProperty(key, out var group) || group.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in group.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object) continue;
                foreach (var property in item.EnumerateObject())
                {
                    AddOrMerge(devices, property.Name, property.Value, groupConnectionState);
                }
            }
        }

        return new MacBluetoothCatalogSnapshot(
            true,
            radioEnabled,
            devices.Values
                .OrderByDescending(device => device.IsConnected)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    private static void AddOrMerge(
        IDictionary<string, MacBluetoothCatalogDevice> devices,
        string displayName,
        JsonElement details,
        bool? groupConnectionState)
    {
        if (details.ValueKind is not JsonValueKind.Object ||
            !details.TryGetProperty("device_address", out var addressProperty) ||
            BluetoothDeviceIdentity.NormalizeAddress(addressProperty.GetString()) is not { } address)
        {
            return;
        }

        var isConnected = details.TryGetProperty("device_connected", out var connectedProperty)
            ? IsTrue(connectedProperty)
            : groupConnectionState ?? false;
        var candidate = new MacBluetoothCatalogDevice(
            address,
            string.IsNullOrWhiteSpace(displayName) ? address : displayName.Trim(),
            address,
            isConnected,
            HasAudioCapability(details));

        if (!devices.TryGetValue(address, out var existing))
        {
            devices[address] = candidate;
            return;
        }

        devices[address] = existing with
        {
            DisplayName = existing.DisplayName == existing.BluetoothAddress
                ? candidate.DisplayName
                : existing.DisplayName,
            IsConnected = existing.IsConnected || candidate.IsConnected,
            IsHeadset = existing.IsHeadset || candidate.IsHeadset,
        };
    }

    private static bool HasAudioCapability(JsonElement details)
    {
        foreach (var propertyName in new[] { "device_minorType", "device_majorType" })
        {
            if (details.TryGetProperty(propertyName, out var property) &&
                property.GetString() is { } value &&
                (value.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("Audio", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return details.TryGetProperty("device_isAudio", out var isAudio) && IsTrue(isAudio);
    }

    private static bool IsTrue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
        JsonValueKind.String => value.GetString() is { } text &&
                                (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                 text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                                 text.Equals("attrib_on", StringComparison.OrdinalIgnoreCase) ||
                                 text.Equals("1", StringComparison.Ordinal)),
        _ => false,
    };
}
