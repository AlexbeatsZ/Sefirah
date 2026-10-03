using System.Diagnostics;
using System.Runtime.InteropServices;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Uses the built-in macOS system volume controls instead of PulseAudio.</summary>
public sealed class MacAudioFeature(
    ILogger<MacAudioFeature> logger,
    ISessionManager sessionManager,
    IDeviceManager deviceManager) : IAudioFeature, IDisposable
{
    private const string SystemOutputId = "macos-system-output";
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private NativeCallback? audioCallback;
    private Timer? changeTimer;
    private bool monitoring;
    private int disposed;
    private (int Volume, bool Muted)? lastBroadcastState;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCallback();

    [DllImport("libSefirahMacAudio.dylib", EntryPoint = "sefirah_macos_monitor_audio")]
    private static extern int MonitorAudio(NativeCallback callback);

    [DllImport("libSefirahMacAudio.dylib", EntryPoint = "sefirah_macos_stop_audio_monitor")]
    private static extern void StopAudioMonitor();

    public Task InitializeAsync()
    {
        changeTimer = new Timer(state => { _ = SyncAsync(update: true); }, null, Timeout.Infinite, Timeout.Infinite);
        audioCallback = OnSystemAudioChanged;
        try
        {
            var status = MonitorAudio(audioCallback);
            monitoring = status == 0;
            if (monitoring) logger.Info("macOS system volume change monitoring initialized");
            else logger.Warn($"Unable to monitor macOS system volume: {status}");
        }
        catch (Exception ex)
        {
            logger.Warn("Unable to initialize macOS system volume monitoring", ex);
        }
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        return SyncAsync();
    }

    private void OnSystemAudioChanged()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { changeTimer?.Change(100, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private async void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (device.IsConnected && device.DeviceSettings.AudioSync)
            await SyncAsync(device);
    }

    private async Task SyncAsync(PairedDevice? target = null, bool update = false)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var entered = false;
        try
        {
            await syncLock.WaitAsync(lifetime.Token);
            entered = true;
            // Avoid spawning an AppleScript process for changes with no interested peer.
            var peers = target is null
                ? deviceManager.PairedDevices.Where(d => d.IsConnected && d.DeviceSettings.AudioSync).ToArray()
                : [target];
            if (peers.Length == 0) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", "set v to get volume settings", "-e", "return (output volume of v as text) & \"|\" & (output muted of v as text)" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null) return;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw;
            }
            var values = (await outputTask).Trim().Split('|');
            var error = await errorTask;
            if (process.ExitCode != 0 || values.Length != 2 || !int.TryParse(values[0], out var volume))
            {
                logger.Warn($"Unable to read macOS system volume: {error}");
                return;
            }
            var muted = values[1].Equals("true", StringComparison.OrdinalIgnoreCase);
            if (update && target is null && lastBroadcastState == (volume, muted)) return;
            var info = new AudioDeviceInfo
            {
                InfoType = update ? AudioInfoType.Active : AudioInfoType.New,
                DeviceId = SystemOutputId,
                DeviceName = "macOS System Output",
                Volume = volume / 100f,
                IsMuted = muted,
                IsSelected = true
            };
            foreach (var device in peers) device.SendMessage(info);
            if (target is null) lastBroadcastState = (volume, muted);
            logger.Debug($"macOS volume synchronized: {volume}%, muted={info.IsMuted}, peers={peers.Length}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warn("Unable to synchronize macOS system volume", ex);
        }
        finally
        {
            if (entered) syncLock.Release();
        }
    }

    public async Task HandleAudioActionAsync(AudioAction action)
    {
        if (action.Source != SystemOutputId || Volatile.Read(ref disposed) != 0) return;
        string? script = action.ActionType switch
        {
            AudioActionType.VolumeUpdate when action.Value.HasValue =>
                $"set volume output volume {(int)Math.Clamp(action.Value.Value * 100, 0, 100)}",
            AudioActionType.ToggleMute => "set volume output muted not (output muted of (get volume settings))",
            _ => null
        };
        if (script is null) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false
            });
            if (process is null) return;
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw;
            }
            if (process.ExitCode == 0) await SyncAsync(update: true);
            else logger.Warn($"macOS audio action failed: {process.ExitCode}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warn("Unable to change macOS system volume", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        sessionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (monitoring) StopAudioMonitor();
        changeTimer?.Dispose();
        lifetime.Cancel();
        GC.KeepAlive(audioCallback);
    }
}
