using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.WinUI;
using Sefirah.Data.Models;

namespace Sefirah.Services;

/// <summary>Same-user IPC for headless administration. It does not expose a network listener.</summary>
public sealed class LocalManagementService(IDeviceManager devices, ISessionManager sessions)
{
    private int started;
    public static string PipeName => $"sefirah-management-{Environment.UserName}";

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                JsonObject response;
                try
                {
                    var text = new StringBuilder();
                    var ch = new char[1];
                    while (text.Length < 8192)
                    {
                        if (await reader.ReadAsync(ch.AsMemory(), deadline.Token) == 0 || ch[0] == '\n') break;
                        text.Append(ch[0]);
                    }
                    if (text.Length >= 8192) throw new ArgumentException("Request too large");
                    using var request = JsonDocument.Parse(text.ToString());
                    var data = await App.MainWindow.DispatcherQueue.EnqueueAsync(
                        () => ExecuteAsync(request.RootElement)).WaitAsync(deadline.Token);
                    response = new JsonObject { ["ok"] = true, ["data"] = data };
                }
                catch (Exception ex) { response = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), deadline.Token);
            }
            catch (Exception) { await Task.Delay(1000); }
        }
    }

    private async Task<JsonNode> ExecuteAsync(JsonElement request)
    {
        string Required(string key) => request.GetProperty(key).GetString()
            ?? throw new ArgumentException($"Missing {key}");
        var method = Required("method");
        var network = (NetworkService)Ioc.Default.GetRequiredService<INetworkService>();
        switch (method)
        {
            case "status":
                var local = await devices.GetLocalDeviceAsync();
                // What this device declares to the first connected peer: Bluetooth control mode and state.
                var controlPeer = devices.PairedDevices.FirstOrDefault(d => d.IsConnected);
                var localControls = controlPeer is null ? null
                    : await Ioc.Default.GetRequiredService<DeviceControlService>().GetCapabilitiesAsync(controlPeer);
                return new JsonObject
                {
                    ["bluetooth"] = localControls is null ? null : new JsonObject
                    { ["control"] = localControls.BluetoothControl, ["enabled"] = localControls.BluetoothEnabled, ["declaredTo"] = controlPeer!.Name },
                    ["apiVersion"] = 1, ["version"] = typeof(LocalManagementService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    ["deviceId"] = local.DeviceId, ["deviceName"] = local.DeviceName,
                    ["port"] = NetworkService.ServerPort,
                    ["paired"] = new JsonArray(devices.PairedDevices.Select(d => (JsonNode)new JsonObject
                    { ["deviceId"] = d.Id, ["deviceName"] = d.Name, ["connected"] = d.IsConnected, ["address"] = d.Address }).ToArray()),
                    ["discovered"] = new JsonArray(devices.DiscoveredDevices.Select(d => (JsonNode)new JsonObject
                    { ["deviceId"] = d.Id, ["deviceName"] = d.Name, ["verificationCode"] = d.VerificationKey,
                        ["address"] = d.Address, ["port"] = d.Port }).ToArray()),
                    ["pending"] = new JsonArray(network.PendingManagementPairings.Select(p => (JsonNode)new JsonObject
                    { ["deviceId"] = p.DeviceId, ["verificationCode"] = p.VerificationCode }).ToArray())
                };
            case "bluetooth":
                // Local same-user pipe only: switch this device's Bluetooth through the same path remote requests use.
                var control = Ioc.Default.GetRequiredService<DeviceControlService>();
                var status = await control.SetLocalBluetoothAsync(Required("enabled") == "true");
                foreach (var peer in devices.PairedDevices.Where(d => d.IsConnected).ToArray())
                    await control.PublishAsync(peer);
                return new JsonObject { ["status"] = status };
            case "connect":
                var id = Required("deviceId");
                if (devices.FindDeviceById(id) is { } paired) sessions.Connect(paired);
                else
                {
                    var port = request.GetProperty("port").GetInt32();
                    if (port is < 1 or > 65535) throw new ArgumentException("Invalid port");
                    sessions.Connect(id, Required("address"), port);
                }
                break;
            case "pair":
                var discovered = devices.DiscoveredDevices.Single(d => d.Id == Required("deviceId"));
                if (discovered.VerificationKey != Required("verificationCode")) throw new ArgumentException("Verification code mismatch");
                sessions.Pair(discovered);
                break;
            case "approve":
            case "reject":
                if (!network.ResolveManagementPairing(Required("deviceId"), Required("verificationCode"), method == "approve"))
                    throw new ArgumentException("No matching pending request");
                break;
            case "disconnect":
                sessions.DisconnectDevice(devices.PairedDevices.Single(d => d.Id == Required("deviceId")), true);
                break;
            case "remove":
                var removed = devices.PairedDevices.Single(d => d.Id == Required("deviceId"));
                sessions.DisconnectDevice(removed, true);
                await devices.RemoveDevice(removed);
                break;
            default: throw new ArgumentException("Unknown method");
        }
        return new JsonObject { ["accepted"] = true };
    }
}
