using System.Collections.Concurrent;

namespace Sefirah.Services;

public sealed class ConnectionAuthenticationGate
{
    private readonly ConcurrentDictionary<string, object> deviceLocks = new(StringComparer.Ordinal);

    public void Execute(string deviceId, Action action)
    {
        lock (deviceLocks.GetOrAdd(deviceId, static _ => new object()))
        {
            action();
        }
    }

    public T Execute<T>(string deviceId, Func<T> action)
    {
        lock (deviceLocks.GetOrAdd(deviceId, static _ => new object()))
        {
            return action();
        }
    }
}
