using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Bluetooth;

public sealed class LocalBluetoothController : ILocalBluetoothController
{
    public Task<BluetoothDeviceCatalog> GetCatalogAsync(string requestId) => Task.FromResult(new BluetoothDeviceCatalog
    {
        RequestId = requestId,
        ControllerAvailable = false,
        RadioEnabled = false,
        SupportsPerDeviceControl = false,
        ErrorCode = "platform_unsupported",
    });

    public Task<BluetoothHandoffResult> ExecuteAsync(BluetoothHandoffCommand command) => Task.FromResult(new BluetoothHandoffResult
    {
        OperationId = command.OperationId,
        Action = command.Action,
        Success = false,
        ErrorCode = "platform_unsupported",
    });
}
