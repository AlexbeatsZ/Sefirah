using System.Net.Sockets;
using System.Text;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;

namespace Sefirah.Services.Transfer;

public partial class ReceiveFileHandler(
    List<FileMetadata> files,
    ServerInfo serverInfo,
    PairedDevice device,
    byte[] expectedCert,
    string storageLocation,
    bool silent,
    ILogger logger,
    IPlatformNotificationHandler notificationHandler) : ITcpClientProvider, IDisposable
{
    private Client? client;
    private FileStream? fileStream;
    private FileMetadata? currentFileMetadata;
    private string? currentFilePath;
    private string? completedFilePath;
    private long bytesTransferred;
    private long totalBytesTransferred = 0;
    private readonly long totalBytes = files.Sum(f => f.FileSize);
    private int currentFileIndex = 0;
    private uint notificationSequence = 1;
    private long lastNotificationUpdateTimestamp;
    private TaskCompletionSource<bool>? handshakeTcs;
    private TaskCompletionSource<bool>? transferCompletionSource;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private bool disposed;

    public Guid TransferId { get; private set; }
    public double Progress => totalBytes == 0 ? 100 : (double)totalBytesTransferred / totalBytes * 100;
    public bool IsBulkTransfer => files.Count > 1;

    /// <summary>
    /// Connects to the file transfer server and authenticates.
    /// </summary>
    /// <returns>The transfer ID for tracking this transfer.</returns>
    public async Task<Guid> ConnectAsync()
    {
        var clientContext = SslHelper.CreateSslContext(expectedCert);
        client = new Client(clientContext, device.Address, serverInfo.Port, this);
        TransferId = client.Id;

        handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!client.ConnectAsync())
            throw new IOException("Failed to connect to file transfer server");

        // Wait for TLS handshake
        if (!client.IsHandshaked)
        {
            await handshakeTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationTokenSource.Token);
        }

        return TransferId;
    }

    /// <summary>
    /// Receives files from the connected server.
    /// </summary>
    /// <returns>The received file for single file transfers, null for bulk.</returns>
    public async Task<StorageFile?> ReceiveAsync()
    {
        StorageFile? resultFile = null;
        
        try
        {
            ShowProgressNotification();

            // Process each file
            foreach (var fileMetadata in files)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                logger.Info($"Starting to receive file {currentFileIndex + 1}/{files.Count}: {fileMetadata.FileName}");

                // Wait for previous file to complete (for bulk transfers)
                if (transferCompletionSource?.Task is { IsCompleted: false })
                {
                    await transferCompletionSource.Task;
                }

                TransferFiles.ValidateName(fileMetadata.FileName);
                if (fileMetadata.FileSize < 0) throw new InvalidDataException("Invalid file size");
                fileStream = silent
                    ? new FileStream(LocalAppPaths.CreateClipboardFilePath(Path.GetExtension(fileMetadata.FileName)), FileMode.CreateNew)
                    : TransferFiles.CreateUnique(storageLocation, fileMetadata.FileName);
                var fullPath = fileStream.Name;
                currentFilePath = fullPath;
                transferCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                currentFileMetadata = fileMetadata;

                var startMessage = Encoding.UTF8.GetBytes(FileTransferService.StartMessage + "\n");
                client?.Send(startMessage);

                if (fileMetadata.FileSize == 0) CompleteCurrentFile(client!);
                // Each chunk renews the inactivity timeout through the progress counter.
                while (!transferCompletionSource.Task.IsCompleted)
                {
                    var previous = Interlocked.Read(ref bytesTransferred);
                    if (await Task.WhenAny(transferCompletionSource.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationTokenSource.Token)) == transferCompletionSource.Task) break;
                    cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    if (Interlocked.Read(ref bytesTransferred) == previous) throw new TimeoutException("File transfer stalled");
                }
                await transferCompletionSource.Task;
                completedFilePath = fullPath;
                currentFilePath = null;

                CleanupFileStream();
                currentFileIndex++;
                logger.Info($"Received file {fileMetadata.FileName}");

                // For single file transfer, capture the result
                if (!IsBulkTransfer)
                {
                    resultFile = await StorageFile.GetFileFromPathAsync(fullPath);
                }

            }

            if (!silent)
            {
                if (IsBulkTransfer)
                {
                    notificationHandler.ShowCompletedFileTransferNotification(
                        "FileTransferNotification.Completed".GetLocalizedResource(),
                        string.Format("FileTransferNotification.CompletedBulk".GetLocalizedResource(), files.Count, device.Name),
                        TransferId.ToString(),
                        folderPath: storageLocation);
                }
                else
                {
                    notificationHandler.ShowCompletedFileTransferNotification(
                        "FileTransferNotification.Completed".GetLocalizedResource(),
                        string.Format("FileTransferNotification.CompletedSingle".GetLocalizedResource(), files[0].FileName, device.Name),
                        TransferId.ToString(),
                        completedFilePath);
                }
            }

            logger.Info($"File transfer completed: {currentFileIndex}/{files.Count} files received");
        }
        catch (OperationCanceledException)
        {
            logger.Info($"File transfer from {device.Name} cancelled");
            if (!silent)
                _ = notificationHandler.RemoveNotificationsByTagAndGroup(TransferId.ToString(), Constants.Notification.FileTransferGroup);
            CleanupFailedFile();
        }
        catch (Exception ex)
        {
            logger.Warn($"File transfer from {device.Name} failed: {ex.Message}");
            if (!silent)
            {
                notificationHandler.ShowCompletedFileTransferNotification(
                    "FileTransferNotification.Failed".GetLocalizedResource(),
                    string.Format("FileTransferNotification.FailedFrom".GetLocalizedResource(), device.Name),
                    TransferId.ToString());
            }
            CleanupFailedFile();
        }

        return resultFile;
    }

    public void Cancel()
    {
        cancellationTokenSource.Cancel();
        transferCompletionSource?.TrySetCanceled(cancellationTokenSource.Token);
        handshakeTcs?.TrySetCanceled(cancellationTokenSource.Token);
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

        var fileName = currentFileMetadata?.FileName ?? files[0].FileName;

        // Title: fileName (index/total)
        var progressTitle = $"{fileName} ({currentFileIndex + 1}/{files.Count})";

        // Subtitle: Receiving/Sending message
        var notificationTitle = IsBulkTransfer
            ? string.Format("FileTransferNotification.ReceivingBulk".GetLocalizedResource(), files.Count, device.Name)
            : string.Format("FileTransferNotification.Receiving".GetLocalizedResource(), device.Name);

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


    private void CleanupFileStream()
    {
        fileStream?.Close();
        fileStream?.Dispose();
        fileStream = null;
        currentFileMetadata = null;
        bytesTransferred = 0;
    }

    private void CleanupFailedFile()
    {
        var path = currentFilePath;
        CleanupFileStream();
        currentFilePath = null;
        if (path is not null)
        {
            try { File.Delete(path); }
            catch (Exception ex) { logger.Warn("Failed to remove incomplete transfer", ex); }
        }
    }

    #region ITcpClientProvider Implementation

    public void OnConnected(Client client)
    {
        logger.Info("Connected to file transfer server");
    }

    public void OnDisconnected(Client client)
    {
        logger.Info("Disconnected from file transfer server");

        handshakeTcs?.TrySetException(new IOException("Disconnected before TLS handshake completed"));

        // If transfer is not complete
        if (currentFileMetadata is not null && fileStream is not null && bytesTransferred < currentFileMetadata.FileSize)
        {
            transferCompletionSource?.TrySetException(new IOException("Connection to server lost"));
        }
    }

    public void OnError(Client client, SocketError error)
    {
        logger.Error($"Socket error occurred during file transfer: {error}");
        handshakeTcs?.TrySetException(new IOException($"Socket error before TLS handshake completed: {error}"));
        transferCompletionSource?.TrySetException(new IOException($"Socket error: {error}"));
    }

    public void OnHandshaked(Client client)
    {
        handshakeTcs?.TrySetResult(true);
    }

    public void OnReceived(Client client, byte[] buffer, long offset, long size)
    {
        try
        {
            cancellationTokenSource.Token.ThrowIfCancellationRequested();

            if (fileStream is null || currentFileMetadata is null) return;

            if (size > currentFileMetadata.FileSize - bytesTransferred) throw new InvalidDataException("Received more file data than declared");
            fileStream.Write(buffer, (int)offset, (int)size);
            bytesTransferred += size;
            totalBytesTransferred += size;

            ShowProgressNotification();

            if (bytesTransferred >= currentFileMetadata.FileSize)
            {
                CompleteCurrentFile(client);
            }
        }
        catch (Exception ex)
        {
            transferCompletionSource?.TrySetException(ex);
        }
    }

    private void CompleteCurrentFile(Client connected)
    {
        fileStream?.Flush();
        if (connected.Send(Encoding.UTF8.GetBytes(FileTransferService.CompleteMessage + "\n")) == 0)
            throw new IOException("Could not confirm file transfer");
        transferCompletionSource?.TrySetResult(true);
    }

    #endregion

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        CleanupFileStream();
        client?.DisconnectAsync();
        client?.Dispose();
        client = null;
        handshakeTcs = null;
        transferCompletionSource = null;
        cancellationTokenSource.Dispose();
        
        GC.SuppressFinalize(this);
    }
}

