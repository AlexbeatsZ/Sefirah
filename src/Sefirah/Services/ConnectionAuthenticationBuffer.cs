using System.Collections.Concurrent;

namespace Sefirah.Services;

/// <summary>
/// Defers frames which arrive behind an authentication frame until the connection has
/// been attached to a device. This prevents coalesced TCP frames from being routed as
/// unknown or left in the receive buffer until unrelated traffic arrives.
/// </summary>
public sealed class ConnectionAuthenticationBuffer<TMessage>
{
    private readonly ConcurrentDictionary<Guid, Attempt> attempts = [];

    public sealed class Attempt : IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<TMessage> deferred = new();
        private readonly CancellationTokenSource cancellation = new();
        private bool completed;

        internal Attempt()
        {
            CancellationToken = cancellation.Token;
        }

        public CancellationToken CancellationToken { get; }

        internal bool TryDefer(TMessage message)
        {
            lock (gate)
            {
                if (completed)
                    return false;

                deferred.Enqueue(message);
                return true;
            }
        }

        internal IReadOnlyList<TMessage> Complete(bool discard)
        {
            lock (gate)
            {
                if (completed)
                    return [];

                completed = true;
                if (discard)
                {
                    deferred.Clear();
                    return [];
                }

                var messages = deferred.ToArray();
                deferred.Clear();
                return messages;
            }
        }

        internal void Cancel()
        {
            cancellation.Cancel();
            Complete(discard: true);
        }

        public void Dispose() => cancellation.Dispose();
    }

    public Attempt Start(Guid connectionId)
    {
        var attempt = new Attempt();
        while (true)
        {
            if (attempts.TryAdd(connectionId, attempt))
                return attempt;

            if (attempts.TryGetValue(connectionId, out var previous) &&
                attempts.TryUpdate(connectionId, attempt, previous))
            {
                previous.Cancel();
                previous.Dispose();
                return attempt;
            }
        }
    }

    public bool TryDefer(Guid connectionId, TMessage message)
    {
        while (attempts.TryGetValue(connectionId, out var attempt))
        {
            if (attempt.TryDefer(message))
                return true;

            Thread.Yield();
        }

        return false;
    }

    public IReadOnlyList<TMessage> Complete(Guid connectionId, Attempt attempt)
    {
        if (!attempts.TryRemove(KeyValuePair.Create(connectionId, attempt)))
            return [];

        var messages = attempt.Complete(discard: false);
        attempt.Dispose();
        return messages;
    }

    public void Cancel(Guid connectionId)
    {
        if (!attempts.TryRemove(connectionId, out var attempt))
            return;

        attempt.Cancel();
        attempt.Dispose();
    }
}
