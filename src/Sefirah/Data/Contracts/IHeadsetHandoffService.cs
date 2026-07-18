using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface IHeadsetHandoffService
{
    IReadOnlyList<HeadsetConfiguration> Configurations { get; }
    BluetoothHandoffState? CurrentState { get; }
    event EventHandler<BluetoothHandoffState>? StateChanged;
    event EventHandler? ConfigurationsChanged;

    Task<BluetoothDeviceCatalog> GetCatalogAsync(string endpointId, CancellationToken cancellationToken = default);
    Task<BluetoothDiscoveryReport> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<BluetoothHandoffState> HandoffAsync(string headsetId, string targetEndpointId, CancellationToken cancellationToken = default);
    Task<BluetoothHandoffState> DisconnectAsync(string headsetId, string endpointId, CancellationToken cancellationToken = default);
    Task<BluetoothHandoffResult> ExecuteCommandAsync(string endpointId, BluetoothHandoffCommand command, CancellationToken cancellationToken = default);
    void HandleCatalog(PairedDevice sourceDevice, BluetoothDeviceCatalog catalog);
    void HandleResult(PairedDevice sourceDevice, BluetoothHandoffResult result);
    void HandleRequest(PairedDevice sourceDevice, BluetoothHandoffRequest request);
    void HandleDisconnectRequest(PairedDevice sourceDevice, BluetoothDisconnectRequest request);
    void HandleRefreshRequest(PairedDevice sourceDevice, BluetoothHandoffRefreshRequest request);
    void HandleVisibilityRequest(PairedDevice sourceDevice, BluetoothHeadsetVisibilityRequest request);
    void SaveConfigurations(IEnumerable<HeadsetConfiguration> configurations);
    Task SendConfigurationAsync(PairedDevice? targetDevice = null);
}
