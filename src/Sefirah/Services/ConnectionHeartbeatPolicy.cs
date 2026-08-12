using Sefirah.Data.Contracts;

namespace Sefirah.Services;

internal static class ConnectionHeartbeatPolicy
{
    public static bool ShouldSendTo(IReadOnlyCollection<string> capabilities) =>
        capabilities.Contains(ProtocolCapabilities.ConnectionHeartbeatV1);
}
