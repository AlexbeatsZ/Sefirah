using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;

namespace Sefirah.Services.Transfer;

public partial class SendFileHandler(
    StorageFile[] storageFiles,
    IReadOnlyList<FileMetadata> files,
    PairedDevice device,
    byte[] expectedClientCert,
    Action<ServerInfo> sendTransferMessage,
    ILogger logger,
    IPlatformNotificationHandler notificationHandler,
    bool silent = false) : ITcpServerProvider, IDisposable
{
    private Server? server;
    private ServerInfo? serverInfo;
    private ServerSession? session;
    private long bytesTransferred;
    private long totalBytesTransferred;
    private readonly long totalBytes = files.Sum(f => f.FileSize);
    private int currentFileIndex;
    private uint notificationSequence = 1;
    private long lastNotificationUpdateTimestamp;
    private TaskCompletionSource<ServerSession>? connectionSource;
    private readonly Channel<string> controls = Channel.CreateBounded<string>(32);
    private readonly TransferControlBuffer controlBuffer = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private bool disposed;

    public Guid TransferId { get; private set; }
    public double Progress => totalBytes == 0 ? 100 : (double)totalBytesTransferred / totalBytes * 100;
    public bool IsBulkTransfer => files.Count > 1;

    /// <summary>
    /// Starts the server and waits for the remote device to connect.
    /// </summary>
    /// <returns>The transfer ID for tracking this transfer.</returns>
    public async Task<Guid> WaitForConnectionAsync()
    {
        connectionSource = new TaskCompletionSource<ServerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Initialize server
        serverInfo = InitializeServer();

        // Let caller create and send the appropriate message type
        sendTransferMessage(serverInfo);

        // Wait for client to connect
        session = await connectionSource.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationTokenSource.Token);
        session.Socket.SendTimeout = 30_000;
        TransferId = session.Id;

        return TransferId;
    }

    /// <summary>
    /// Sends files to the connected client.
    /// </summary>
    public async Task SendAsync()
    {
        try
        {
            // Show initial notification
            ShowProgressNotification();

            // Send each file
            for (int i = 0; i < storageFiles.Length; i++)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                logger.Debug($"Sending file: {files[i].FileName}");
                
                // Wait for client to send "start" message indicating readiness to receive
                await ExpectControlAsync(FileTransferService.StartMessage);
                
                await SendFileData(files[i], await storageFiles[i].OpenStreamForReadAsync());
                await ExpectControlAsync(FileTransferService.CompleteMessage);

                currentFileIndex++;
            }

            // Show completion notification
            if (!silent)
            {
                if (IsBulkTransfer)
                {
                    notificationHandler.ShowCompletedFileTransferNotification(
                        "FileTransferNotification.Completed".GetLocalizedResource(),
                        string.Format("FileTransferNotification.SentBulk".GetLocalizedResource(), files.Count, device.Name),
                        TransferId.ToString());
                }
                else
                {
                    notificationHandler.ShowCompletedFileTransferNotification(
                        "FileTransferNotification.Completed".GetLocalizedResource(),
                        string.Format("FileTransferNotification.SentSingle".GetLocalizedResource(), files[0].FileName, device.Name),
                        TransferId.ToString());
                }
            }

            logger.Debug("All files transferred successfully");
        }
        catch (OperationCanceledException)
        {
            logger.Info($"File transfer to {device.Name} cancelled");
            if (!silent)
                _ = notificationHandler.RemoveNotificationsByTagAndGroup(TransferId.ToString(), Constants.Notification.FileTransferGroup);
        }
        catch (Exception ex)
        {
            logger.Warn($"File transfer to {device.Name} failed: {ex.Message}");
            if (!silent)
            {
                notificationHandler.ShowCompletedFileTransferNotification(
                    "FileTransferNotification.Failed".GetLocalizedResource(),
                    string.Format("FileTransferNotification.FailedTo".GetLocalizedResource(), device.Name),
                    TransferId.ToString());
            }
        }
    }

    public void Cancel()
    {
        cancellationTokenSource.Cancel();
        connectionSource?.TrySetCanceled(cancellationTokenSource.Token);
        controls.Writer.TryComplete(new OperationCanceledException());
    }

    private async Task SendFileData(FileMetadata metadata, Stream stream)
    {
        using (stream)
        {
            var buffer = new byte[FileTransferService.ChunkSize];

            while (bytesTransferred < metadata.FileSize)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Remote disconnect is a failure, not a cancellation.
                if (session is null || !session.IsConnected)
                    throw new IOException("Remote device disconnected during transfer");

                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, metadata.FileSize - bytesTransferred)), cancellationTokenSource.Token);
                if (bytesRead == 0) throw new EndOfStreamException("Source file changed during transfer");

                if (session.Send(buffer, 0, bytesRead) != bytesRead) throw new IOException("Incomplete socket write");
                bytesTransferred += bytesRead;
                totalBytesTransferred += bytesRead;

                ShowProgressNotification();
            }
        }

        bytesTransferred = 0;
        logger.Info($"Completed file transfer for {metadata.FileName}");
    }

    private void ShowProgressNotification()
    {
        if (silent) return;
        var now = Environment.TickCount64;
        if (lastNotificationUpdateTimestamp != 0 && now - lastNotificationUpdateTimestamp < 500)
            return;

        if (lastNotificationUpdateTimestamp != 0)
            notificationSequence++;

        lastNotificationUpdateTimestamp = now;

        var fileName = files[Math.Min(currentFileIndex, files.Count - 1)].FileName;

        // Title: fileName (index/total)
        var progressTitle = $"{fileName} ({currentFileIndex + 1}/{files.Count})";

        // Notification title: Receiving/Sending message
        var notificationTitle = IsBulkTransfer
            ? string.Format("FileTransferNotification.SendingBulk".GetLocalizedResource(), files.Count, device.Name)
            : string.Format("FileTransferNotification.Sending".GetLocalizedResource(), device.Name);

        // Status: "{transferred} / {total}"
        var transferredFormatted = TransferFiles.FormatBytes(totalBytesTransferred);
        var totalFormatted = TransferFiles.FormatBytes(totalBytes);
        var status = $"{transferredFormatted} / {totalFormatted}";

        notificationHandler.ShowFileTransferNotification(
            notificationTitle,
            progressTitle,
            status,
            TransferId.ToString(),
            notificationSequence,
            Progress);
    }

    private async Task ExpectControlAsync(string expected)
    {
        var message = await controls.Reader.ReadAsync(cancellationTokenSource.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationTokenSource.Token);
        if (message != expected) throw new InvalidDataException($"Expected {expected}, received {message}");
    }

    private ServerInfo InitializeServer()
    {
        var sslContext = SslHelper.CreateSslContext(expectedClientCert);
        foreach (int port in FileTransferService.PortRange)
        {
            try
            {
                server = new Server(sslContext, IPAddress.IPv6Any, port, this)
                {
                    OptionDualMode = true,
                };
                if (!server.Start()) throw new IOException("Transfer port unavailable");

                serverInfo = new ServerInfo(port);
                logger.Info($"File transfer server initialized at {serverInfo.Port} (cert-only auth)");
                return serverInfo;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                server = null;
                logger.Debug($"Failed to start server on port {port}: {ex.Message}");
            }
        }

        throw new IOException("Failed to start file transfer server: all ports in range are unavailable");
    }

    #region ITcpServerProvider Implementation

    public void OnConnected(ServerSession session) { }

    public void OnHandshaked(ServerSession connected)
    {
        if (Interlocked.CompareExchange(ref session, connected, null) is null)
            connectionSource?.TrySetResult(connected);
        else connected.Disconnect();
    }

    public void OnDisconnected(ServerSession disconnected)
    {
        if (session != disconnected) return;
        controls.Writer.TryComplete(new IOException("Remote device disconnected during transfer"));
    }

    public void OnReceived(ServerSession connected, byte[] buffer, long offset, long size)
    {
        if (session != connected) return;
        try
        {
            foreach (var message in controlBuffer.Append(buffer.AsSpan((int)offset, (int)size)))
                if (!controls.Writer.TryWrite(message)) throw new IOException("Too many transfer controls");
        }
        catch (Exception ex) { controls.Writer.TryComplete(ex); }
    }

    public void OnError(SocketError error)
    {
        logger.Error($"Server socket error: {error}");
    }

    #endregion

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        server?.Stop();
        server?.Dispose();
        server = null;
        session = null;
        serverInfo = null;
        connectionSource = null;
        controls.Writer.TryComplete();
        cancellationTokenSource.Dispose();
        
        GC.SuppressFinalize(this);
    }
}

