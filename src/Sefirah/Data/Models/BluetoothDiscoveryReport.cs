namespace Sefirah.Data.Models;

public sealed class BluetoothDiscoveryReport
{
    public List<BluetoothEndpointCatalog> Endpoints { get; set; } = [];
    public List<HeadsetConfiguration> Headsets { get; set; } = [];
}

public sealed class BluetoothEndpointCatalog
{
    public required string EndpointId { get; set; }
    public required string DisplayName { get; set; }
    public required BluetoothDeviceCatalog Catalog { get; set; }
}
