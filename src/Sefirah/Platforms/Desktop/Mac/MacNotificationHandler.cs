using System.Diagnostics;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="IPlatformNotificationHandler"/> using AppleScript
/// to display notifications in the macOS Notification Center.
/// </summary>
public sealed class MacNotificationHandler(ILogger<MacNotificationHandler> logger) : IPlatformNotificationHandler
{
    public Task RegisterForNotifications()
    {
        logger.Info("Registered for macOS notifications");
        return Task.CompletedTask;
    }

    public Task ShowRemoteNotification(NotificationInfo message, string deviceId)
    {
        var title = EscapeAppleScriptString(message.Title ?? message.AppName ?? "Sefirah");
        var body = EscapeAppleScriptString(message.Text ?? "");
        var subtitle = EscapeAppleScriptString(message.AppName ?? "");

        DisplayNotification(body, title, subtitle);
        return Task.CompletedTask;
    }

    public Task ShowCallNotification(string callId, string transportDeviceId, string title, string subtitle, Uri? icon = null)
    {
        DisplayNotification(EscapeAppleScriptString(subtitle), EscapeAppleScriptString(title), "Phone Call");
        return Task.CompletedTask;
    }

    public Task ShowCallNotification(string title, string text, string tag, Data.Enums.CallState callState, Uri? icon = null)
    {
        DisplayNotification(EscapeAppleScriptString(text), EscapeAppleScriptString(title), "Phone Call");
        return Task.CompletedTask;
    }

    public Task ShowBatteryNotification(string title, string text, string tag)
    {
        DisplayNotification(EscapeAppleScriptString(text), EscapeAppleScriptString(title), "Battery");
        return Task.CompletedTask;
    }

    public void ShowClipboardNotification(string title, string text, string? actionLabel = null, string? actionData = null)
    {
        DisplayNotification(EscapeAppleScriptString(text), EscapeAppleScriptString(title), "Clipboard");
    }

    public void ShowCompletedFileTransferNotification(string title, string subtitle, string transferId, string? filePath = null, string? folderPath = null)
    {
        DisplayNotification(EscapeAppleScriptString(subtitle), EscapeAppleScriptString(title), "File Transfer");
    }

    public void ShowFileTransferNotification(string notificationTitle, string progressTitle, string status, string transferId, uint notificationSequence, double progress)
    {
        // Suppress intermediate progress notifications on macOS to prevent banner storms
    }

    public Task RemoveNotificationByTag(string? notificationKey) => Task.CompletedTask;

    public Task RemoveNotificationsByGroup(string? groupKey) => Task.CompletedTask;

    public Task RemoveNotificationsByTagAndGroup(string? tag, string? groupKey) => Task.CompletedTask;

    public Task ClearAllNotifications() => Task.CompletedTask;

    private void DisplayNotification(string text, string title, string? subtitle = null)
    {
        try
        {
            var subtitleClause = string.IsNullOrWhiteSpace(subtitle) ? "" : $" subtitle \"{subtitle}\"";
            var script = $"display notification \"{text}\" with title \"{title}\"{subtitleClause}";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to display notification: {ex.Message}");
        }
    }

    private static string EscapeAppleScriptString(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
