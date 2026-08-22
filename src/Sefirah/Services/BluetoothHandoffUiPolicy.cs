namespace Sefirah.Services;

internal static class BluetoothHandoffUiPolicy
{
    public static bool IsConnected(string? activeEndpointId) =>
        !string.IsNullOrEmpty(activeEndpointId);

    public static bool IsEndpointAvailable(
        string? endpointId,
        IReadOnlySet<string> unavailableEndpointIds) =>
        endpointId is not null && !unavailableEndpointIds.Contains(endpointId);

    public static bool CanSwitchTo(
        string? sourceEndpointId,
        string? targetEndpointId,
        IReadOnlyCollection<string> supportedEndpointIds) =>
        targetEndpointId is not null &&
        targetEndpointId != sourceEndpointId &&
        SupportsEndpoint(targetEndpointId, supportedEndpointIds);

    public static bool SupportsEndpoint(
        string endpointId,
        IReadOnlyCollection<string> supportedEndpointIds) =>
        supportedEndpointIds.Count == 0 || supportedEndpointIds.Contains(endpointId);

    public static bool IsOtherTarget(
        string candidateEndpointId,
        string? sourceEndpointId,
        string? localEndpointId) =>
        candidateEndpointId != sourceEndpointId &&
        candidateEndpointId != localEndpointId;
}
