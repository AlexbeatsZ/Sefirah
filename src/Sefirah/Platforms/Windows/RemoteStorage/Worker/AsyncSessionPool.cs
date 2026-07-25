using System.Collections.Concurrent;

namespace Sefirah.Platforms.Windows.RemoteStorage.Worker;

internal sealed class AsyncSessionPool<TKey>(
    Action<TKey, Exception> onUnexpectedError)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Session> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stopping;

    public bool Has(TKey key) => _sessions.ContainsKey(key);

    public async Task StartAsync(TKey key, Func<CancellationToken, Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping)
            {
                return;
            }

            if (_sessions.TryRemove(key, out var previous))
            {
                previous.RequestStop();
                await previous.Completion;
                previous.Dispose();
            }

            var session = new Session(key, action, onUnexpectedError);
            _sessions[key] = session;
            _ = ObserveCompletionAsync(key, session);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(TKey key)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryRemove(key, out var session))
            {
                return;
            }

            session.RequestStop();
            await session.Completion;
            session.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync()
    {
        Session[] sessions;

        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();

            foreach (var session in sessions)
            {
                session.RequestStop();
            }
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(sessions.Select(session => session.Completion));
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    private async Task ObserveCompletionAsync(TKey key, Session session)
    {
        await session.Completion;

        await _gate.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(key, out var current) &&
                ReferenceEquals(current, session))
            {
                _sessions.TryRemove(key, out _);
            }

            session.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _disposed;

        public Session(
            TKey key,
            Func<CancellationToken, Task> action,
            Action<TKey, Exception> onUnexpectedError)
        {
            Completion = Task.Run(async () =>
            {
                try
                {
                    await action(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                {
                    // Expected shutdown.
                }
                catch (Exception ex)
                {
                    onUnexpectedError(key, ex);
                }
            });
        }

        public Task Completion { get; }

        public void RequestStop()
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationTokenSource.Dispose();
        }
    }
}
