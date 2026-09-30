using System.Diagnostics;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Uses the built-in macOS system volume controls instead of PulseAudio.</summary>
public sealed class MacAudioFeature(
    ILogger<MacAudioFeature> logger,
    ISessionManager sessionManager,
    IDeviceManager deviceManager) : IAudioFeature, IDisposable
{
    private const string SystemOutputId = "macos-system-output";

    public Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        return SyncAsync();
    }

    private async void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (device.IsConnected && device.DeviceSettings.AudioSync)
            await SyncAsync(device);
    }

    private async Task SyncAsync(PairedDevice? target = null)
    {
        try
        {
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
            await process.WaitForExitAsync();
            var values = (await outputTask).Trim().Split('|');
            var error = await errorTask;
            if (process.ExitCode != 0 || values.Length != 2 || !int.TryParse(values[0], out var volume))
            {
                logger.Warn($"Unable to read macOS system volume: {error}");
                return;
            }
            var info = new AudioDeviceInfo
            {
                InfoType = AudioInfoType.New,
                DeviceId = SystemOutputId,
                DeviceName = "macOS System Output",
                Volume = volume / 100f,
                IsMuted = values[1].Equals("true", StringComparison.OrdinalIgnoreCase),
                IsSelected = true
            };
            if (target is not null)
                target.SendMessage(info);
            else
                foreach (var device in deviceManager.PairedDevices.Where(d => d.IsConnected && d.DeviceSettings.AudioSync))
                    device.SendMessage(info);
        }
        catch (Exception ex)
        {
            logger.Warn("Unable to synchronize macOS system volume", ex);
        }
    }

    public async Task HandleAudioActionAsync(AudioAction action)
    {
        if (action.Source != SystemOutputId) return;
        string? script = action.ActionType switch
        {
            AudioActionType.VolumeUpdate when action.Value.HasValue =>
                $"set volume output volume {(int)Math.Clamp(action.Value.Value * 100, 0, 100)}",
            AudioActionType.ToggleMute => "set volume output muted not (output muted of (get volume settings))",
            _ => null
        };
        if (script is null) return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false
        });
        if (process is not null)
        {
            await process.WaitForExitAsync();
            await SyncAsync();
        }
    }

    public void Dispose() => sessionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
}
