namespace Sefirah.Services;

public enum ConnectionDirection
{
    Incoming,
    Outgoing,
}

public static class ConnectionCollisionPolicy
{
    public static ConnectionDirection PreferredDirection(string localDeviceId, string remoteDeviceId)
    {
        return string.CompareOrdinal(localDeviceId, remoteDeviceId) < 0
            ? ConnectionDirection.Outgoing
            : ConnectionDirection.Incoming;
    }

    public static bool ShouldAcceptCandidate(
        string localDeviceId,
        string remoteDeviceId,
        ConnectionDirection? existingDirection,
        ConnectionDirection candidateDirection)
    {
        if (existingDirection is null)
            return true;
        if (existingDirection == candidateDirection)
            return true;

        var preferred = PreferredDirection(localDeviceId, remoteDeviceId);
        return existingDirection != preferred && candidateDirection == preferred;
    }
}
