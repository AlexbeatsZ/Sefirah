using System.Threading.Channels;

namespace Sefirah.Platforms.Windows.RemoteStorage.Worker;

public sealed partial class TaskQueue(ChannelReader<Func<Task>> taskReader) : IDisposable
{
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private CancellationTokenSource? _linkedTokenSource;
    private Task? _runningTask = null;

    public void Start(CancellationToken stoppingToken)
    {
        if (_runningTask is not null)
        {
            throw new InvalidOperationException("Task queue is already running.");
        }

        _linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _disposeTokenSource.Token);
        _runningTask = RunAsync(_linkedTokenSource.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await taskReader.WaitToReadAsync(cancellationToken))
            {
                while (taskReader.TryRead(out var func))
                {
                    await func();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected shutdown.
        }
    }

    public Task Stop()
    {
        _disposeTokenSource.Cancel();
        return _runningTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposeTokenSource.Cancel();
        _linkedTokenSource?.Dispose();
        _disposeTokenSource.Dispose();
    }
}
