namespace Sefirah.Services.Socket;

internal sealed class SerializedSocketSender : IDisposable
{
    internal const int DefaultMaxQueuedFrames = 64;
    internal const long DefaultMaxFrameBytes = 8L * 1024 * 1024;
    internal const long DefaultMaxQueuedBytes = 8L * 1024 * 1024;
    internal const long DefaultMaxControlFrameBytes = 64L * 1024;

    private readonly object gate = new();
    private readonly Queue<byte[]> applicationQueue = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Func<byte[], bool> sendAsync;
    private readonly Func<bool> isConnected;
    private readonly Func<long> bytesPending;
    private readonly Func<long> bytesSending;
    private readonly int maxQueuedFrames;
    private readonly long maxQueuedBytes;
    private int queuedFrames;
    private long queuedBytes;
    private bool stopped;

    public SerializedSocketSender(
        Func<byte[], bool> sendAsync,
        Func<bool> isConnected,
        Func<long> bytesPending,
        Func<long> bytesSending,
        int maxQueuedFrames = DefaultMaxQueuedFrames,
        long maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(isConnected);
        ArgumentNullException.ThrowIfNull(bytesPending);
        ArgumentNullException.ThrowIfNull(bytesSending);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedBytes);

        this.sendAsync = sendAsync;
        this.isConnected = isConnected;
        this.bytesPending = bytesPending;
        this.bytesSending = bytesSending;
        this.maxQueuedFrames = maxQueuedFrames;
        this.maxQueuedBytes = maxQueuedBytes;
        _ = Task.Run(ProcessAsync);
    }

    public bool TrySendApplication(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.LongLength > DefaultMaxFrameBytes)
            return false;

        lock (gate)
        {
            if (stopped || !isConnected() ||
                queuedFrames >= maxQueuedFrames ||
                buffer.LongLength > maxQueuedBytes - queuedBytes)
                return false;

            applicationQueue.Enqueue(buffer);
            queuedFrames++;
            queuedBytes += buffer.LongLength;
        }

        available.Release();
        return true;
    }

    /// <summary>
    /// Adds a small control frame directly to NetCoreServer's thread-safe send buffer.
    /// Application frames remain single-writer and wait for the transport to drain, so
    /// a heartbeat can overtake the application queue without interleaving either frame.
    /// A successful return means accepted by the transport, not acknowledged by the peer.
    /// </summary>
    public bool TrySendControl(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.LongLength > DefaultMaxControlFrameBytes)
            return false;

        lock (gate)
        {
            if (stopped || !isConnected())
                return false;
        }

        return sendAsync(buffer);
    }

    public void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            applicationQueue.Clear();
            queuedFrames = 0;
            queuedBytes = 0;
        }

        cancellationTokenSource.Cancel();
    }

    public void Dispose()
    {
        // The worker owns the wait handles until cancellation is observed. They are
        // intentionally left for GC here so Dispose cannot race a pending Release
        // or WaitAsync on a socket callback thread.
        Stop();
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                await available.WaitAsync(cancellationTokenSource.Token);
                var buffer = Dequeue();
                if (buffer is null)
                    continue;

                if (isConnected() && sendAsync(buffer))
                    await WaitUntilFlushedAsync(cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private byte[]? Dequeue()
    {
        lock (gate)
        {
            var buffer = applicationQueue.Count > 0 ? applicationQueue.Dequeue() : null;

            if (buffer is not null)
            {
                queuedFrames--;
                queuedBytes -= buffer.LongLength;
            }

            return buffer;
        }
    }

    private async Task<bool> WaitUntilFlushedAsync(CancellationToken cancellationToken)
    {
        while (isConnected())
        {
            if (bytesPending() == 0 && bytesSending() == 0)
                return true;

            await Task.Delay(2, cancellationToken);
        }

        return false;
    }

}
