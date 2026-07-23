using Sefirah.Data.Contracts;

namespace Sefirah.Services;

public static class RemoteActionPolicy
{
    public static bool CanExecute(IEnumerable<string> capabilities) =>
        capabilities.Contains(
            ProtocolCapabilities.RemoteActionsControllerV1,
            StringComparer.Ordinal);
}
