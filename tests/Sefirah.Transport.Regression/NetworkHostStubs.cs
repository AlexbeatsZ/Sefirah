using System.Runtime.CompilerServices;

public interface ILogger<T> : ILogger { }
public class ObservableObject
{
    protected bool SetProperty<T>(ref T field, T value) { field = value; return true; }
}
public interface INetworkService { }
public interface ISessionManager
{
    void Connect(string id, string address, int port);
}
public interface IAdbService { Task TryConnectTcp(string address, string model); }
public interface IDeviceManager
{
    ObservableCollection<PairedDevice> PairedDevices { get; }
    ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; }
    PairedDevice? ActiveDevice { get; set; }
    Task<TestLocalDevice> GetLocalDeviceAsync();
    Task UpdateDevice(PairedDevice device);
    Task<PairedDevice> AddDevice(DiscoveredDevice device);
}
public sealed record TestLocalDevice(string DeviceId, string DeviceName);
public static class Ioc
{
    public static TestServiceProvider Default { get; } = new();
    public sealed class TestServiceProvider
    {
        public T GetRequiredService<T>() where T : new() => new();
    }
}
public static class App
{
    public static TestWindow MainWindow { get; } = new();
}
public sealed class TestWindow
{
    public TestDispatcher DispatcherQueue { get; } = new();
    public Frame Content { get; } = new();
}
public sealed class Frame { public object XamlRoot { get; } = new(); }
public enum ContentDialogResult { None, Primary }
public sealed class TestDispatcher { }
namespace CommunityToolkit.WinUI
{
    public static class DispatcherExtensions
    {
        public static Task EnqueueAsync(this TestDispatcher dispatcher, Action action) { action(); return Task.CompletedTask; }
        public static Task<T> EnqueueAsync<T>(this TestDispatcher dispatcher, Func<T> action) => Task.FromResult(action());
        public static Task EnqueueAsync(this TestDispatcher dispatcher, Func<Task> action) => action();
        public static Task<T> AsTask<T>(this Task<T> task) => task;
    }
}
namespace Sefirah.Dialogs
{
    public sealed class ConnectionRequestDialog(string name, string key, Frame frame)
    {
        public object? XamlRoot { get; set; }
        public Task<ContentDialogResult> ShowAsync() => Task.FromResult(ContentDialogResult.None);
        public void Hide() { }
    }
}
namespace Sefirah.Services
{
    public sealed class DevicePresentationService { public Task PublishAsync(PairedDevice device) => Task.CompletedTask; }
}
