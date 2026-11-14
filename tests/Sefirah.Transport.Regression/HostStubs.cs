public sealed class ApplicationData
{
    public static ApplicationData Current { get; } = new();
    public LocalFolderInfo LocalFolder { get; } = new();
    public sealed class LocalFolderInfo
    {
        public string Path { get; set; } = string.Empty;
    }
}

public sealed class StorageFile(string path)
{
    public string Path { get; } = path;
    public Task<Stream> OpenStreamForReadAsync() => Task.FromResult<Stream>(File.OpenRead(Path));
    public static Task<StorageFile> GetFileFromPathAsync(string path) => Task.FromResult(new StorageFile(path));
}

public interface IPlatformNotificationHandler
{
    void ShowCompletedFileTransferNotification(string title, string message, string id, string? path = null, string? folderPath = null);
    void ShowFileTransferNotification(string title, string progressTitle, string status, string id, uint sequence, double progress);
    Task RemoveNotificationsByTagAndGroup(string tag, string group);
}

public sealed class TestHost : ILogger<NetworkService>, IPlatformNotificationHandler
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Failures { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> Trace { get; } = new();
    public void Debug(string message) => Trace.Enqueue(message);
    public void Info(string message) => Trace.Enqueue(message);
    public void Error(string message) => Failures.Enqueue(message);
    public void Warn(string message, Exception? exception = null) => Failures.Enqueue(message);
    public void ShowCompletedFileTransferNotification(string title, string message, string id, string? path = null, string? folderPath = null) { }
    public void ShowFileTransferNotification(string title, string progressTitle, string status, string id, uint sequence, double progress) { }
    public Task RemoveNotificationsByTagAndGroup(string tag, string group) => Task.CompletedTask;
}

public static class Constants
{
    public static class Notification { public const string FileTransferGroup = "test-transfer"; }
}

namespace Sefirah.Data.Models
{
    public sealed class PairedDevice : BaseRemoteDevice
    {
        public PairedDevice() { Name = "Regression peer"; Address = "127.0.0.1"; }
        public string Address { get; set; }
        public int Port { get; set; }
        public ConnectionStatus ConnectionStatus { get; set; } = new Disconnected();
        public bool IsConnected => ConnectionStatus is Connected;
        public bool IsConnecting => ConnectionStatus is Connecting;
        public bool IsConnectedOrConnecting => IsConnected || IsConnecting;
        public bool IsForcedDisconnect => ConnectionStatus is Disconnected disconnected && disconnected.ForcedDisconnect;
        public bool IsComputer => Guid.TryParse(Id, out _);
        public bool IsMobile => !IsComputer;
        public TestDeviceSettings DeviceSettings { get; } = new();
        public List<string> GetEnabledAddresses() => [Address];
        public void TryAddAddress(string address) { }
    }
    public sealed class TestDeviceSettings { public bool AdbAutoConnect => false; }
    public sealed class DiscoveredDevice : BaseRemoteDevice
    {
        public string Address { get; set; } = string.Empty;
        public int Port { get; set; }
        public string VerificationKey { get; set; } = string.Empty;
        public bool IsPairing { get; set; }
    }

}
namespace Sefirah.Utils
{
    public static class TestResources
    {
        public static string GetLocalizedResource(this string value) => "{0} {1}";
    }
    public static class LocalAppPaths
    {
        public static string CreateClipboardFilePath(string extension) =>
            System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, Guid.NewGuid() + extension);
    }
}
namespace Sefirah.Services.Transfer
{
    public static class FileTransferService
    {
        public const string StartMessage = "start";
        public const string CompleteMessage = "complete";
        public const int ChunkSize = 524288;
        public static IEnumerable<int> PortRange => Enumerable.Range(5152, 18);
    }
}
