using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sefirah.Data.Models;
using Sefirah.Utils;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>Native notification identity, removal and actions for the packaged macOS app.</summary>
public sealed class MacNotificationHandler(ILogger<MacNotificationHandler> logger, IDeviceManager deviceManager) : IPlatformNotificationHandler
{
    private const string Library = "libSefirahMacNotifications.dylib";
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(int Status, string Error)>> pending = new();
    private readonly object registrationLock = new();
    private Task<bool>? registration;
    private Task<bool>? authorization;
    private CompletionCallback? completionCallback;
    private ActionCallback? actionCallback;
    private long nextRequest;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CompletionCallback(long request, int status, [MarshalAs(UnmanagedType.LPUTF8Str)] string error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport(Library, EntryPoint = "sefirah_macos_initialize_notifications")]
    private static extern int InitializeNative(CompletionCallback completion, ActionCallback action);
    [DllImport(Library, EntryPoint = "sefirah_macos_authorize_notifications")]
    private static extern void AuthorizeNative(long request);
    [DllImport(Library, EntryPoint = "sefirah_macos_show_notification")]
    private static extern void ShowNative([MarshalAs(UnmanagedType.LPUTF8Str)] string json, long request);
    [DllImport(Library, EntryPoint = "sefirah_macos_remove_notifications")]
    private static extern void RemoveNative([MarshalAs(UnmanagedType.LPUTF8Str)] string json, long request);

    public async Task RegisterForNotifications() => await EnsureInitializedAsync();
    private Task<bool> EnsureInitializedAsync()
    {
        lock (registrationLock) return registration ??= InitializeAsync();
    }
    private Task<bool> InitializeAsync()
    {
        try
        {
            completionCallback = OnCompleted;
            actionCallback = OnAction;
            if (InitializeNative(completionCallback, actionCallback) != 0)
            {
                logger.Warn("Native notifications require the packaged Sefirah application identity");
                return Task.FromResult(false);
            }
            logger.Info("Registered for native macOS notification actions");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.Warn("Unable to initialize native macOS notifications", ex);
            return Task.FromResult(false);
        }
    }
    private void OnCompleted(long request, int status, string error)
    {
        if (pending.TryRemove(request, out var source)) source.TrySetResult((status, error));
    }
    private async Task<bool> RequestAsync(Action<long> start)
    {
        var request = Interlocked.Increment(ref nextRequest);
        var source = new TaskCompletionSource<(int Status, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request] = source;
        try
        {
            start(request);
            var result = await source.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (result.Status != 0) logger.Warn($"macOS notification operation failed: {result.Error}");
            return result.Status == 0;
        }
        catch (Exception ex)
        {
            logger.Warn("macOS notification operation failed", ex);
            return false;
        }
        finally { pending.TryRemove(request, out _); }
    }
    private sealed class Button(string identifier, string title)
    {
        public string id { get; } = identifier;
        public string label { get; } = title;
    }
    private static Dictionary<string, string> Context(string type, string tag, string group) =>
        new() { ["type"] = type, ["tag"] = tag, ["group"] = group };

    public Task ShowRemoteNotification(NotificationInfo message, string deviceId)
    {
        var context = Context("remote", string.IsNullOrEmpty(message.Tag) ? message.NotificationKey : message.Tag, message.GroupKey ?? "");
        context["device"] = deviceId;
        context["key"] = message.NotificationKey;
        context["replyKey"] = message.ReplyResultKey ?? "";
        var buttons = new List<Button>();
        if (!string.IsNullOrEmpty(message.ReplyResultKey)) buttons.Add(new("reply", "SendButton".GetLocalizedResource()));
        buttons.AddRange(message.Actions.Where(a => !string.IsNullOrEmpty(a.Label))
            .Select(a => new Button($"action:{a.ActionIndex}", a.Label!)).Take(4 - buttons.Count));
        return ShowAsync(message.Title ?? message.AppName ?? "Sefirah", message.Text ?? "", message.AppName ?? "", context, buttons);
    }
    public Task ShowCallNotification(string callId, string transportDeviceId, string title, string subtitle, Uri? icon = null) =>
        ShowAsync(title, subtitle, "", Context("call", callId, Constants.Notification.IncomingPhoneCallGroup));
    public Task ShowCallNotification(string title, string text, string tag, Data.Enums.CallState callState, Uri? icon = null) =>
        ShowAsync(title, text, "", Context("call", tag, Constants.Notification.IncomingPhoneCallGroup));
    public Task ShowBatteryNotification(string title, string text, string tag) =>
        ShowAsync(title, text, "", Context("battery", tag, Constants.Notification.BatteryGroup));
    public void ShowClipboardNotification(string title, string text, string? actionLabel = null, string? actionData = null)
    {
        var context = Context("clipboard", "clipboard", "clipboard");
        context["url"] = actionData ?? "";
        _ = ShowAsync(title, text, "", context, string.IsNullOrEmpty(actionLabel) ? [] : [new Button("url", actionLabel)]);
    }
    public void ShowCompletedFileTransferNotification(string title, string subtitle, string transferId, string? filePath = null, string? folderPath = null)
    {
        var context = Context("file", transferId, Constants.Notification.FileTransferGroup);
        context["file"] = filePath ?? "";
        context["folder"] = folderPath ?? "";
        _ = ShowAsync(title, subtitle, "", context);
    }
    public void ShowFileTransferNotification(string notificationTitle, string progressTitle, string status, string transferId, uint notificationSequence, double progress)
    {
        // Keep intermediate progress in the app instead of issuing a banner for every update.
    }
    public Task RemoveNotificationByTag(string? tag) => string.IsNullOrEmpty(tag)
        ? Task.CompletedTask : RemoveAsync(new() { ["tag"] = tag });
    public Task RemoveNotificationsByGroup(string? group) => string.IsNullOrEmpty(group)
        ? Task.CompletedTask : RemoveAsync(new() { ["group"] = group });
    public Task RemoveNotificationsByTagAndGroup(string? tag, string? group) => string.IsNullOrEmpty(tag)
        ? Task.CompletedTask : RemoveAsync(new() { ["tag"] = tag, ["group"] = group ?? "" });
    public Task ClearAllNotifications() => RemoveAsync(new());
    private async Task RemoveAsync(Dictionary<string, string> query)
    {
        if (await EnsureInitializedAsync()) await RequestAsync(id => RemoveNative(JsonSerializer.Serialize(query), id));
    }
    private async Task ShowAsync(string title, string body, string subtitle, Dictionary<string, string> context, List<Button>? buttons = null)
    {
        if (!await EnsureInitializedAsync()) return;
        Task<bool> authorize;
        lock (registrationLock) authorize = authorization ??= RequestAsync(AuthorizeNative);
        await authorize;
        // Do not cache a denied permission as an unavailable center: the user can
        // subsequently enable notifications in System Settings without restarting.
        var buttonJson = JsonSerializer.Serialize(buttons ?? []);
        var json = JsonSerializer.Serialize(new
        {
            id = JsonSerializer.Serialize(new[] { context.GetValueOrDefault("device", ""), context["group"], context["tag"] }),
            title, body, subtitle, group = context["group"], context,
            buttons = buttons ?? [], category = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(buttonJson)))
        });
        await RequestAsync(id => ShowNative(json, id));
    }
    private void OnAction(string json)
    {
        try
        {
            var context = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (context is not null) App.MainWindow.DispatcherQueue.TryEnqueue(() => HandleAction(context));
        }
        catch (Exception ex) { logger.Warn("Unable to receive a macOS notification action", ex); }
    }
    private void HandleAction(Dictionary<string, string> context)
    {
        try
        {
            var action = context.GetValueOrDefault("action", "open");
            switch (context["type"])
            {
                case "remote" when action != "open":
                    var device = deviceManager.FindDeviceById(context["device"]);
                    if (device is null || !device.IsConnected) return;
                    if (action == "reply" && !string.IsNullOrEmpty(context["replyKey"]))
                        NotificationActionUtils.ProcessReplyAction(device, context["key"], context["replyKey"], context.GetValueOrDefault("reply", ""));
                    else if (action.StartsWith("action:", StringComparison.Ordinal) && int.TryParse(action.AsSpan(7), out var index))
                        NotificationActionUtils.ProcessClickAction(device, context["key"], index);
                    break;
                case "clipboard" when Uri.TryCreate(context["url"], UriKind.Absolute, out var uri) && Sefirah.Features.ClipboardFeature.IsValidWebUrl(uri):
                    Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false });
                    break;
                case "file":
                    var path = File.Exists(context["file"]) ? context["file"] : context["folder"];
                    if (File.Exists(path) || Directory.Exists(path))
                        Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { path }, UseShellExecute = false });
                    break;
                default:
                    App.ShowMainWindow();
                    break;
            }
        }
        catch (Exception ex) { logger.Warn("Unable to handle a macOS notification action", ex); }
    }
}
