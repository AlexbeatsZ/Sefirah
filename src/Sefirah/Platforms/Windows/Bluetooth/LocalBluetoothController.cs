using Sefirah.Data.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Sefirah.Platforms.Windows.Bluetooth;

public sealed class LocalBluetoothController(
    BluetoothRadioManager radioManager,
    ILogger<LocalBluetoothController> logger) : ILocalBluetoothController
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    public async Task<BluetoothDeviceCatalog> GetCatalogAsync(string requestId)
    {
        try
        {
            var available = await radioManager.RefreshAsync();
            var result = new BluetoothDeviceCatalog
            {
                RequestId = requestId,
                ControllerAvailable = available,
                RadioEnabled = radioManager.IsBluetoothRadioOn,
                SupportsPerDeviceControl = false,
            };
            if (!available || !radioManager.IsBluetoothRadioOn) return result;

            var devices = new Dictionary<string, BluetoothCatalogDevice>(StringComparer.OrdinalIgnoreCase);
            await AddClassicDevicesAsync(devices);
            await AddLowEnergyDevicesAsync(devices);

            result.Devices.AddRange(devices.Values
                .OrderByDescending(device => device.IsConnected)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            return result;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to enumerate paired Bluetooth devices: {ex}");
            return new BluetoothDeviceCatalog
            {
                RequestId = requestId,
                ControllerAvailable = false,
                RadioEnabled = radioManager.IsBluetoothRadioOn,
                SupportsPerDeviceControl = false,
                ErrorCode = "catalog_failed",
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task AddClassicDevicesAsync(IDictionary<string, BluetoothCatalogDevice> devices)
    {
        DeviceInformationCollection pairedDevices;
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            pairedDevices = await DeviceInformation.FindAllAsync(selector, [IsConnectedProperty]);
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to enumerate paired classic Bluetooth devices: {ex}");
            return;
        }

        foreach (var deviceInfo in pairedDevices)
        {
            try
            {
                using var device = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
                if (device is null)
                {
                    AddOrMerge(devices, FromDeviceInformation(deviceInfo));
                    continue;
                }

                AddOrMerge(devices, new BluetoothCatalogDevice
                {
                    DeviceKey = deviceInfo.Id,
                    DisplayName = string.IsNullOrWhiteSpace(device.Name) ? deviceInfo.Name : device.Name,
                    IsConnected = GetConnectedProperty(deviceInfo) ??
                                  device.ConnectionStatus is BluetoothConnectionStatus.Connected,
                    BluetoothAddress = FormatAddress(device.BluetoothAddress),
                    IsHeadset = IsHeadset(device.ClassOfDevice),
                });
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to inspect classic Bluetooth device {deviceInfo.Id}: {ex}");
                AddOrMerge(devices, FromDeviceInformation(deviceInfo));
            }
        }
    }

    private async Task AddLowEnergyDevicesAsync(IDictionary<string, BluetoothCatalogDevice> devices)
    {
        DeviceInformationCollection pairedDevices;
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            pairedDevices = await DeviceInformation.FindAllAsync(selector, [IsConnectedProperty]);
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to enumerate paired Bluetooth LE devices: {ex}");
            return;
        }

        foreach (var deviceInfo in pairedDevices)
        {
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                if (device is null)
                {
                    AddOrMerge(devices, FromDeviceInformation(deviceInfo));
                    continue;
                }

                AddOrMerge(devices, new BluetoothCatalogDevice
                {
                    DeviceKey = deviceInfo.Id,
                    DisplayName = string.IsNullOrWhiteSpace(device.Name) ? deviceInfo.Name : device.Name,
                    IsConnected = GetConnectedProperty(deviceInfo) ??
                                  device.ConnectionStatus is BluetoothConnectionStatus.Connected,
                    BluetoothAddress = FormatAddress(device.BluetoothAddress),
                });
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to inspect Bluetooth LE device {deviceInfo.Id}: {ex}");
                AddOrMerge(devices, FromDeviceInformation(deviceInfo));
            }
        }
    }

    private static BluetoothCatalogDevice FromDeviceInformation(DeviceInformation deviceInfo) => new()
    {
        DeviceKey = deviceInfo.Id,
        DisplayName = string.IsNullOrWhiteSpace(deviceInfo.Name) ? deviceInfo.Id : deviceInfo.Name,
        IsConnected = GetConnectedProperty(deviceInfo) ?? false,
    };

    private static bool? GetConnectedProperty(DeviceInformation deviceInfo) =>
        deviceInfo.Properties.TryGetValue(IsConnectedProperty, out var propertyValue) &&
        propertyValue is bool connected
            ? connected
            : null;

    private static void AddOrMerge(
        IDictionary<string, BluetoothCatalogDevice> devices,
        BluetoothCatalogDevice candidate)
    {
        var identity = candidate.BluetoothAddress ?? candidate.DeviceKey;
        if (!devices.TryGetValue(identity, out var existing))
        {
            devices[identity] = candidate;
            return;
        }

        existing.IsConnected |= candidate.IsConnected;
        existing.IsHeadset |= candidate.IsHeadset;
        existing.BluetoothAddress ??= candidate.BluetoothAddress;
        if (string.IsNullOrWhiteSpace(existing.DisplayName))
        {
            existing.DisplayName = candidate.DisplayName;
        }
    }

    private static string? FormatAddress(ulong address) =>
        address == 0 ? null : address.ToString("X12");

    private static bool IsHeadset(BluetoothClassOfDevice classOfDevice) =>
        classOfDevice.MinorClass is
            BluetoothMinorClass.AudioVideoHandsFree or
            BluetoothMinorClass.AudioVideoHeadphones or
            BluetoothMinorClass.AudioVideoWearableHeadset;

    public async Task<BluetoothHandoffResult> ExecuteAsync(BluetoothHandoffCommand command)
    {
        if (command.Action is not "setRadio")
        {
            return Failed(command, "per_device_control_unsupported");
        }

        var enabled = command.Enabled ?? false;
        var success = await radioManager.TrySetStateAsync(enabled);
        return new BluetoothHandoffResult
        {
            OperationId = command.OperationId,
            Action = command.Action,
            Success = success,
            RadioEnabled = radioManager.IsBluetoothRadioOn,
            ErrorCode = success ? null : "radio_command_failed",
        };
    }

    private static BluetoothHandoffResult Failed(BluetoothHandoffCommand command, string errorCode) => new()
    {
        OperationId = command.OperationId,
        Action = command.Action,
        Success = false,
        ErrorCode = errorCode,
    };
}
