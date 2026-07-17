using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface ILocalBluetoothController
{
    Task<BluetoothDeviceCatalog> GetCatalogAsync(string requestId);

    Task<BluetoothHandoffResult> ExecuteAsync(BluetoothHandoffCommand command);
}
