using System.Diagnostics;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="IMediaFeature"/> using AppleScript for system volume
/// and media player control.
/// </summary>
public sealed class MacMediaFeature(ILogger<MacMediaFeature> logger) : IMediaFeature
{
    public Task InitializeAsync()
    {
        logger.Info("MacMediaFeature initialized");
        return Task.CompletedTask;
    }

    public Task HandleMediaActionAsync(MediaAction mediaAction)
    {
        try
        {
            switch (mediaAction.ActionType)
            {
                case MediaActionType.VolumeUpdate when mediaAction.Value.HasValue:
                    var volumePercent = (int)Math.Clamp(mediaAction.Value.Value * 100, 0, 100);
                    RunAppleScript($"set volume output volume {volumePercent}");
                    break;

                case MediaActionType.ToggleMute:
                    RunAppleScript("set volume output muted not (output muted of (get volume settings))");
                    break;

                case MediaActionType.Play:
                case MediaActionType.Pause:
                case MediaActionType.Stop:
                    RunAppleScript(
                        "tell application \"System Events\"\n" +
                        "    key code 16 using {control down, command down}\n" +
                        "end tell");
                    break;

                case MediaActionType.Next:
                    RunAppleScript(
                        "tell application \"System Events\"\n" +
                        "    key code 17 using {control down, command down}\n" +
                        "end tell");
                    break;

                case MediaActionType.Previous:
                    RunAppleScript(
                        "tell application \"System Events\"\n" +
                        "    key code 18 using {control down, command down}\n" +
                        "end tell");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to handle media action {mediaAction.ActionType}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static void RunAppleScript(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
    }
}
