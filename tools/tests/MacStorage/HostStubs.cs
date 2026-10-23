// The lifecycle test links the production mount/feature code. Only the Uno host,
// device model and logger are substituted; all SFTP, WebDAV and mount I/O is real.
global using Sefirah.Data.Contracts;
global using Microsoft.Extensions.Logging;
global using Sefirah.Tests;

namespace Microsoft.Extensions.Logging
{
    public interface ILogger<T> { }
}

namespace Sefirah.Tests
{
    public class TestLogger<T> : ILogger<T> { }
    public static class TestLogging
    {
        public static void Info<T>(this ILogger<T> logger, string message) { }
        public static void Warn<T>(this ILogger<T> logger, string message) { Console.WriteLine("HOST_WARNING " + message); }
    }
    public class TestSessionManager : ISessionManager
    {
        public event EventHandler<Sefirah.Data.Models.PairedDevice>? ConnectionStatusChanged;
        public void Disconnect(Sefirah.Data.Models.PairedDevice device)
        {
            device.IsConnected = false;
            ConnectionStatusChanged?.Invoke(this, device);
        }
    }
}

namespace Sefirah.Data.Contracts
{
    public interface IFeature { Task InitializeAsync(); }
    public interface ISessionManager { event EventHandler<Sefirah.Data.Models.PairedDevice>? ConnectionStatusChanged; }
    public interface IUserSettingsService { IGeneralSettingsService GeneralSettingsService { get; } }
    public interface IGeneralSettingsService { string RemoteStoragePath { get; } }
}

namespace Sefirah.Data.Models
{
    public class TestDeviceSettings { public bool StorageAccess { get; set; } = true; }
    public class PairedDevice
    {
        public string Id { get; set; } = "fixture-device";
        public string Name { get; set; } = "测试手机 / device";
        public string? Address { get; set; } = "127.0.0.1";
        public bool IsConnected { get; set; } = true;
        public TestDeviceSettings DeviceSettings { get; } = new();
    }
    public class SftpServerInfo
    {
        public required string Username { get; set; }
        public required string Password { get; set; }
        public int Port { get; set; }
        public List<string> Paths { get; set; } = [];
        public List<string> PathNames { get; set; } = [];
    }
}

namespace Windows.System
{
    public static class Launcher
    {
        public static int UriLaunches;
        public static Task<bool> LaunchUriAsync(Uri uri) { UriLaunches++; return Task.FromResult(true); }
    }
}

namespace Windows.ApplicationModel.DataTransfer
{
    public enum DataPackageOperation { Copy }
    public class DataPackage
    {
        public DataPackageOperation RequestedOperation { get; set; }
        public void SetText(string text) { }
    }
    public static class Clipboard { public static void SetContent(DataPackage value) { } }
}
