using Sefirah.Platforms.Windows.RemoteStorage.Worker;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

var pool = new AsyncSessionPool<string>((_, exception) =>
    throw new InvalidOperationException("A provider session failed unexpectedly.", exception));

var activeCount = 0;
var maximumActiveCount = 0;

Task ProviderLoop(TaskCompletionSource started, CancellationToken cancellationToken)
{
    return RunAsync();

    async Task RunAsync()
    {
        var active = Interlocked.Increment(ref activeCount);
        UpdateMaximum(ref maximumActiveCount, active);
        started.SetResult();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Widen the replacement race: the old provider is still unwinding after cancellation.
            await Task.Delay(10);
        }
        finally
        {
            Interlocked.Decrement(ref activeCount);
        }
    }
}

for (var generation = 0; generation < 50; generation++)
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await pool.StartAsync("device-root", token => ProviderLoop(started, token));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(pool.Has("device-root"), $"Generation {generation} was removed by its predecessor.");
}

Assert(maximumActiveCount == 1, $"Expected one active provider, observed {maximumActiveCount}.");

await pool.StopAsync("device-root");

Assert(!pool.Has("device-root"), "Stopped provider remained registered.");
Assert(activeCount == 0, $"Expected no active providers, observed {activeCount}.");

var channel = Channel.CreateUnbounded<Func<Task>>();
using var queue = new TaskQueue(channel.Reader, NullLogger.Instance);
var workAfterFailureCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var queueTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var queueTaskCompleted = false;

queue.Start(CancellationToken.None);
await channel.Writer.WriteAsync(() => throw new IOException("Simulated SFTP transport failure."));
await channel.Writer.WriteAsync(() =>
{
    workAfterFailureCompleted.SetResult();
    return Task.CompletedTask;
});
await workAfterFailureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

await channel.Writer.WriteAsync(async () =>
{
    queueTaskStarted.SetResult();
    await Task.Delay(100);
    queueTaskCompleted = true;
});
await queueTaskStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

var stopwatch = Stopwatch.StartNew();
await queue.Stop();
stopwatch.Stop();

Assert(queueTaskCompleted, "Stopping the queue returned before its active work completed.");
Assert(
    stopwatch.Elapsed >= TimeSpan.FromMilliseconds(75),
    $"Queue stop did not await the real async loop ({stopwatch.ElapsedMilliseconds} ms).");

Console.WriteLine("Sync provider lifecycle regression: PASS");

static void UpdateMaximum(ref int target, int candidate)
{
    while (true)
    {
        var current = Volatile.Read(ref target);
        if (candidate <= current ||
            Interlocked.CompareExchange(ref target, candidate, current) == current)
        {
            return;
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
