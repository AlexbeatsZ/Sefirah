using System.IO.Pipes;
using System.Text;
using CommunityToolkit.WinUI;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Windows.Control;

public sealed class ControlApiWorker(
    IHeadsetHandoffService handoffService,
    IDeviceManager deviceManager,
    ISessionManager sessionManager,
    ISftpFeature sftpFeature,
    ILogger<ControlApiWorker> logger) : BackgroundService
{
    public const string PipeName = "Sefirah.Control.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sefirah control API request failed");
            }
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line)) return;

        ControlResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ControlRequest>(line, JsonOptions)
                ?? throw new InvalidOperationException("Request JSON is empty");
            response = new ControlResponse(true, await DispatchAsync(request, cancellationToken), null);
        }
        catch (Exception ex)
        {
            response = new ControlResponse(false, null, new ControlError("command_failed", ex.Message));
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private async Task<object?> DispatchAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "status" or "bluetooth.endpoints" => await GetStatusAsync(),
            "pairing.list" => await GetPairingCandidatesAsync(),
            "pairing.request" => await RequestPairingAsync(request.Arguments),
            "pairing.forget" => await ForgetPairingAsync(request.Arguments),
            "storage.unregister" => await UnregisterStorageAsync(request.Arguments),
            "bluetooth.catalog" => await GetCatalogAsync(request.Arguments, cancellationToken),
            "bluetooth.discover" => await handoffService.DiscoverAsync(cancellationToken),
            "bluetooth.config" => handoffService.Configurations,
            "bluetooth.view" => await GetBluetoothViewAsync(request.Arguments),
            "bluetooth.handoff" => await HandoffAsync(request.Arguments, cancellationToken),
            "bluetooth.disconnect" => await DisconnectAsync(request.Arguments, cancellationToken),
            "bluetooth.visibility" => SetVisibility(request.Arguments),
            "bluetooth.command" => await ExecuteBluetoothCommandAsync(request.Arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown command: {request.Command}"),
        };
    }

    private async Task<object> GetPairingCandidatesAsync()
    {
        return await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            deviceManager.DiscoveredDevices.Select(device => new
            {
                id = device.Id,
                name = device.Name,
                model = device.Model,
                address = device.Address,
                port = device.Port,
                verificationCode = device.VerificationKey,
                isPairing = device.IsPairing,
            }).ToArray());
    }

    private async Task<object> RequestPairingAsync(JsonElement arguments)
    {
        var deviceId = GetRequiredString(arguments, "deviceId");
        var verificationCode = GetRequiredString(arguments, "verificationCode");
        var device = await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            deviceManager.DiscoveredDevices.FirstOrDefault(candidate =>
                candidate.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)));

        if (device is null)
            throw new InvalidOperationException($"Pairing candidate was not found: {deviceId}");
        if (!device.VerificationKey.Equals(verificationCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verification code does not match the current device certificate.");

        sessionManager.Pair(device);
        return new
        {
            id = device.Id,
            name = device.Name,
            verificationCode = device.VerificationKey,
            requested = true,
        };
    }

    private async Task<object> ForgetPairingAsync(JsonElement arguments)
    {
        var deviceId = GetRequiredString(arguments, "deviceId");
        var expectedName = GetRequiredString(arguments, "expectedName");
        var device = deviceManager.FindDeviceById(deviceId)
            ?? throw new InvalidOperationException($"Paired device was not found: {deviceId}");

        if (!device.Name.Equals(expectedName, StringComparison.Ordinal))
            throw new InvalidOperationException("Expected device name does not match the paired device.");

        if (device.IsConnected)
            sessionManager.DisconnectDevice(device, true);

        await sftpFeature.RemoveAsync(device.Id);
        await deviceManager.RemoveDevice(device);
        return new { id = device.Id, name = device.Name, forgotten = true };
    }

    private async Task<object> UnregisterStorageAsync(JsonElement arguments)
    {
        var deviceId = GetRequiredString(arguments, "deviceId");
        var expectedName = GetRequiredString(arguments, "expectedName");
        var device = deviceManager.FindDeviceById(deviceId)
            ?? throw new InvalidOperationException($"Paired device was not found: {deviceId}");

        if (!device.Name.Equals(expectedName, StringComparison.Ordinal))
            throw new InvalidOperationException("Expected device name does not match the paired device.");

        await sftpFeature.RemoveAsync(device.Id);
        return new { id = device.Id, name = device.Name, unregistered = true };
    }

    private async Task<object> GetStatusAsync()
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        return new
        {
            local = new { id = local.DeviceId, name = local.DeviceName, connected = true },
            remotes = deviceManager.PairedDevices.Select(device => new
            {
                id = device.Id,
                name = device.Name,
                connected = device.IsConnected,
                capabilities = device.Capabilities.Order().ToArray(),
            }).ToArray(),
        };
    }

    private async Task<object> GetCatalogAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(GetRequiredString(arguments, "endpoint"));
        return new
        {
            endpoint = new { id = endpoint.Id, name = endpoint.Name },
            catalog = await handoffService.GetCatalogAsync(endpoint.Id, cancellationToken),
        };
    }

    private async Task<object> HandoffAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var headsetId = GetRequiredString(arguments, "headsetId");
        var endpoint = await ResolveEndpointAsync(GetRequiredString(arguments, "endpoint"));
        return await handoffService.HandoffAsync(headsetId, endpoint.Id, cancellationToken);
    }

    private async Task<object> DisconnectAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var headsetId = GetRequiredString(arguments, "headsetId");
        var endpoint = await ResolveEndpointAsync(GetRequiredString(arguments, "endpoint"));
        return await handoffService.DisconnectAsync(headsetId, endpoint.Id, cancellationToken);
    }

    private object SetVisibility(JsonElement arguments)
    {
        var headsetId = GetRequiredString(arguments, "headsetId");
        if (!arguments.TryGetProperty("isVisible", out var visibility))
        {
            throw new InvalidOperationException("Missing argument: isVisible");
        }

        var configurations = handoffService.Configurations.ToList();
        var headset = configurations.FirstOrDefault(item => item.Id == headsetId)
            ?? throw new InvalidOperationException($"Bluetooth headset was not found: {headsetId}");
        headset.IsVisible = visibility.GetBoolean();
        handoffService.SaveConfigurations(configurations);
        return headset;
    }

    private async Task<object> GetBluetoothViewAsync(JsonElement arguments)
    {
        var endpoint = await ResolveEndpointAsync(GetRequiredString(arguments, "endpoint"));
        var filter = arguments.TryGetProperty("filter", out var filterValue)
            ? filterValue.GetString()
            : "all";
        if (filter is not ("all" or "headsets"))
        {
            throw new InvalidOperationException("Bluetooth filter must be all or headsets.");
        }

        var visible = handoffService.Configurations
            .Where(item => item.IsVisible && (filter == "all" || item.IsHeadset))
            .ToList();
        return new
        {
            filter,
            selectedEndpoint = new { id = endpoint.Id, name = endpoint.Name },
            selectedConnected = visible.Where(item => item.ActiveEndpointId == endpoint.Id).ToArray(),
            otherConnected = visible.Where(item =>
                item.ActiveEndpointId is not null && item.ActiveEndpointId != endpoint.Id).ToArray(),
            savedDisconnected = visible.Where(item =>
                item.ActiveEndpointId is null && item.EndpointDeviceKeys.ContainsKey(endpoint.Id)).ToArray(),
            visibility = handoffService.Configurations.Select(item => new
            {
                headsetId = item.Id,
                item.DisplayName,
                item.IsVisible,
                item.IsHeadset,
            }).ToArray(),
        };
    }

    private async Task<object> ExecuteBluetoothCommandAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(GetRequiredString(arguments, "endpoint"));
        var action = GetRequiredString(arguments, "action");
        var command = new BluetoothHandoffCommand
        {
            OperationId = Guid.NewGuid().ToString(),
            Action = action,
            DeviceKey = arguments.TryGetProperty("deviceKey", out var key) ? key.GetString() : null,
            Enabled = arguments.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : null,
        };
        return await handoffService.ExecuteCommandAsync(endpoint.Id, command, cancellationToken);
    }

    private async Task<(string Id, string Name)> ResolveEndpointAsync(string value)
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        if (value.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pc", StringComparison.OrdinalIgnoreCase) ||
            value.Equals(local.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(local.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return (local.DeviceId, local.DeviceName);
        }

        var connected = deviceManager.PairedDevices.Where(device => device.IsConnected).ToList();
        if (value.Equals("phone", StringComparison.OrdinalIgnoreCase) && connected.Count == 1)
        {
            return (connected[0].Id, connected[0].Name);
        }

        var remote = deviceManager.PairedDevices.FirstOrDefault(device =>
            device.Id.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            device.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        return remote is null
            ? throw new InvalidOperationException($"Bluetooth endpoint was not found: {value}")
            : (remote.Id, remote.Name);
    }

    private static string GetRequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"Missing argument: {name}");

    private sealed record ControlRequest(string Command, JsonElement Arguments);
    private sealed record ControlResponse(bool Ok, object? Result, ControlError? Error);
    private sealed record ControlError(string Code, string Message);
}
