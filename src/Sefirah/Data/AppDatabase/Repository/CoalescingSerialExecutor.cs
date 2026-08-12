namespace Sefirah.Data.AppDatabase.Repository;

/// <summary>
/// Serializes expensive work and coalesces duplicate keys while preserving the latest update.
/// </summary>
internal sealed class CoalescingSerialExecutor<TKey> where TKey : notnull
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock sync = new();
    private readonly Dictionary<TKey, PendingWork> pending = [];

    public Task RunAsync(TKey key, Func<Task> action)
    {
        PendingWork work;
        lock (sync)
        {
            if (pending.TryGetValue(key, out work!))
            {
                work.LatestAction = action;
                work.HasPendingUpdate = true;
                return work.Completion.Task;
            }

            work = new PendingWork(action);
            pending.Add(key, work);
        }

        _ = ProcessAsync(key, work);
        return work.Completion.Task;
    }

    private async Task ProcessAsync(TKey key, PendingWork work)
    {
        Exception? failure = null;
        try
        {
            await gate.WaitAsync();
            try
            {
                while (true)
                {
                    Func<Task> action;
                    lock (sync)
                    {
                        action = work.LatestAction;
                        work.HasPendingUpdate = false;
                    }

                    await action();

                    lock (sync)
                    {
                        if (work.HasPendingUpdate) continue;
                        pending.Remove(key);
                        break;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            lock (sync)
            {
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                    pending.Remove(key);
            }
        }
        finally
        {
            if (failure is null)
                work.Completion.TrySetResult();
            else
                work.Completion.TrySetException(failure);
        }
    }

    private sealed class PendingWork(Func<Task> action)
    {
        public Func<Task> LatestAction { get; set; } = action;
        public bool HasPendingUpdate { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
