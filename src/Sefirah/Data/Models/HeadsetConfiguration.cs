namespace Sefirah.Data.Models;

public class HeadsetConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public bool IsHeadset { get; set; }
    public string? BluetoothAddress { get; set; }
    public Dictionary<string, string> EndpointDeviceKeys { get; set; } = [];
    public string? ActiveEndpointId { get; set; }
}
