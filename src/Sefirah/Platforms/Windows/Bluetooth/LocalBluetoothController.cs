using Sefirah.Data.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Sefirah.Platforms.Windows.Bluetooth;

public sealed class LocalBluetoothController(
    BluetoothRadioManager radioManager,
    ILogger<LocalBluetoothController> logger) : ILocalBluetoothController
{
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

            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var pairedDevices = await DeviceInformation.FindAllAsync(selector);
            foreach (var deviceInfo in pairedDevices)
            {
                using var device = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
                if (device?.ClassOfDevice.MajorClass is not BluetoothMajorClass.AudioVideo) continue;

                result.Devices.Add(new BluetoothAudioDevice
                {
                    DeviceKey = deviceInfo.Id,
                    DisplayName = string.IsNullOrWhiteSpace(device.Name) ? deviceInfo.Name : device.Name,
                    IsConnected = device.ConnectionStatus is BluetoothConnectionStatus.Connected,
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to enumerate Bluetooth audio devices: {ex}");
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
