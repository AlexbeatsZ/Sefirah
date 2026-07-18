using System.Collections.Concurrent;
using Sefirah.Data.Models;

namespace Sefirah.Services;

public sealed class HeadsetHandoffService(
    ILocalBluetoothController localController,
    IDeviceManager deviceManager,
    IUserSettingsService userSettingsService,
    ILogger<HeadsetHandoffService> logger) : IHeadsetHandoffService
{
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BluetoothDeviceCatalog>> catalogWaiters = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BluetoothHandoffResult>> resultWaiters = [];

    public IReadOnlyList<HeadsetConfiguration> Configurations =>
        userSettingsService.GeneralSettingsService.Headsets;

    public BluetoothHandoffState? CurrentState { get; private set; }

    public event EventHandler<BluetoothHandoffState>? StateChanged;
    public event EventHandler? ConfigurationsChanged;

    public void SaveConfigurations(IEnumerable<HeadsetConfiguration> configurations)
    {
        userSettingsService.GeneralSettingsService.Headsets = configurations.ToList();
        ConfigurationsChanged?.Invoke(this, EventArgs.Empty);
        _ = SendConfigurationAsync();
    }

    public async Task SendConfigurationAsync(PairedDevice? targetDevice = null)
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        var message = new BluetoothHandoffConfiguration
        {
            Headsets = Configurations.Select(h => new BluetoothHeadsetDescriptor
            {
                Id = h.Id,
                DisplayName = h.DisplayName,
                IsVisible = h.IsVisible,
                EndpointIds = h.EndpointDeviceKeys.Keys.ToList(),
                ActiveEndpointId = h.ActiveEndpointId,
            }).ToList(),
            Endpoints =
            [
                new BluetoothEndpointDescriptor { Id = local.DeviceId, DisplayName = $"{local.DeviceName} (PC)" },
                .. deviceManager.PairedDevices.Select(d => new BluetoothEndpointDescriptor
                {
                    Id = d.Id,
                    DisplayName = d.Name,
                }),
            ],
        };
        IEnumerable<PairedDevice> targets = targetDevice is null ? deviceManager.PairedDevices : [targetDevice];
        foreach (var device in targets.Where(d =>
                     d.IsConnected && d.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1)))
        {
            device.SendMessage(message);
        }
    }

    public async Task<BluetoothDeviceCatalog> GetCatalogAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        var localEndpointId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
        var requestId = Guid.NewGuid().ToString();
        if (endpointId == localEndpointId)
        {
            return await localController.GetCatalogAsync(requestId);
        }

        var endpoint = deviceManager.PairedDevices.FirstOrDefault(d => d.Id == endpointId);
        if (endpoint is null || !endpoint.IsConnected)
        {
            return CatalogError(requestId, "endpoint_disconnected");
        }
        if (!endpoint.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
        {
            return CatalogError(requestId, "endpoint_unsupported");
        }

        var waiter = new TaskCompletionSource<BluetoothDeviceCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        catalogWaiters[requestId] = waiter;
        try
        {
            endpoint.SendMessage(new BluetoothDeviceCatalogRequest { RequestId = requestId });
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            return CatalogError(requestId, "endpoint_timeout");
        }
        finally
        {
            catalogWaiters.TryRemove(requestId, out _);
        }
    }

    public async Task<BluetoothDiscoveryReport> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        var endpoints = new List<BluetoothEndpointDescriptor>
        {
            new() { Id = local.DeviceId, DisplayName = $"{local.DeviceName} (this PC)" },
        };
        endpoints.AddRange(deviceManager.PairedDevices
            .Where(device => device.IsConnected && device.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
            .Select(device => new BluetoothEndpointDescriptor { Id = device.Id, DisplayName = device.Name }));

        var endpointCatalogs = new List<BluetoothEndpointCatalog>();
        foreach (var endpoint in endpoints)
        {
            endpointCatalogs.Add(new BluetoothEndpointCatalog
            {
                EndpointId = endpoint.Id,
                DisplayName = endpoint.DisplayName,
                Catalog = await GetCatalogAsync(endpoint.Id, cancellationToken),
            });
        }

        var grouped = endpointCatalogs
            .SelectMany(item => item.Catalog.Devices.Select(device => (Endpoint: item, Device: device)))
            .GroupBy(item => item.Device.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Endpoint.EndpointId).Distinct().Count() >= 2)
            .ToList();

        var configurations = Configurations.ToList();
        foreach (var configuration in configurations)
        {
            configuration.ActiveEndpointId = null;
        }
        foreach (var group in grouped)
        {
            var configuration = configurations.FirstOrDefault(headset =>
                headset.DisplayName.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (configuration is null)
            {
                configuration = new HeadsetConfiguration { DisplayName = group.Key };
                configurations.Add(configuration);
            }

            foreach (var item in group)
            {
                configuration.EndpointDeviceKeys[item.Endpoint.EndpointId] = item.Device.DeviceKey;
                if (item.Device.IsConnected) configuration.ActiveEndpointId = item.Endpoint.EndpointId;
            }
        }

        foreach (var configuration in configurations)
        {
            var connectedEndpoint = endpointCatalogs.FirstOrDefault(endpoint =>
                configuration.EndpointDeviceKeys.TryGetValue(endpoint.EndpointId, out var deviceKey) &&
                endpoint.Catalog.Devices.Any(device => device.DeviceKey == deviceKey && device.IsConnected));
            configuration.ActiveEndpointId = connectedEndpoint?.EndpointId;
        }

        SaveConfigurations(configurations);
        return new BluetoothDiscoveryReport
        {
            Endpoints = endpointCatalogs,
            Headsets = configurations,
        };
    }

    public Task<BluetoothHandoffState> HandoffAsync(
        string headsetId,
        string targetEndpointId,
        CancellationToken cancellationToken = default) =>
        HandoffCoreAsync(Guid.NewGuid().ToString(), headsetId, targetEndpointId, cancellationToken);

    public Task<BluetoothHandoffState> DisconnectAsync(
        string headsetId,
        string endpointId,
        CancellationToken cancellationToken = default) =>
        DisconnectCoreAsync(Guid.NewGuid().ToString(), headsetId, endpointId, cancellationToken);

    public Task<BluetoothHandoffResult> ExecuteCommandAsync(
        string endpointId,
        BluetoothHandoffCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteEndpointCommandAsync(endpointId, command, cancellationToken);

    public void HandleCatalog(PairedDevice sourceDevice, BluetoothDeviceCatalog catalog)
    {
        if (catalogWaiters.TryGetValue(catalog.RequestId, out var waiter))
        {
            waiter.TrySetResult(catalog);
        }
    }

    public void HandleResult(PairedDevice sourceDevice, BluetoothHandoffResult result)
    {
        var key = ResultKey(sourceDevice.Id, result.OperationId, result.Action);
        if (resultWaiters.TryGetValue(key, out var waiter))
        {
            waiter.TrySetResult(result);
        }
    }

    public async void HandleRequest(PairedDevice sourceDevice, BluetoothHandoffRequest request)
    {
        try
        {
            await HandoffCoreAsync(
                request.OperationId,
                request.HeadsetId,
                request.TargetEndpointId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error("Bluetooth handoff request failed", ex);
        }
    }

    public async void HandleDisconnectRequest(PairedDevice sourceDevice, BluetoothDisconnectRequest request)
    {
        try
        {
            await DisconnectCoreAsync(
                request.OperationId,
                request.HeadsetId,
                request.EndpointId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error("Bluetooth disconnect request failed", ex);
        }
    }

    public async void HandleRefreshRequest(PairedDevice sourceDevice, BluetoothHandoffRefreshRequest request)
    {
        try
        {
            await DiscoverAsync();
            await SendConfigurationAsync(sourceDevice);
        }
        catch (Exception ex)
        {
            logger.Error("Bluetooth refresh request failed", ex);
        }
    }

    public async void HandleVisibilityRequest(PairedDevice sourceDevice, BluetoothHeadsetVisibilityRequest request)
    {
        var configurations = Configurations.ToList();
        var headset = configurations.FirstOrDefault(item => item.Id == request.HeadsetId);
        if (headset is null) return;
        headset.IsVisible = request.IsVisible;
        SaveConfigurations(configurations);
        await SendConfigurationAsync();
    }

    private async Task<BluetoothHandoffState> DisconnectCoreAsync(
        string operationId,
        string headsetId,
        string endpointId,
        CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var headset = Configurations.FirstOrDefault(item => item.Id == headsetId);
            if (headset is null || !headset.EndpointDeviceKeys.ContainsKey(endpointId))
            {
                return Publish(operationId, headsetId, "failed", endpointId, endpointId, headset?.ActiveEndpointId,
                    "Bluetooth device is not saved on this endpoint");
            }

            Publish(operationId, headsetId, "disconnecting", endpointId, endpointId, headset.ActiveEndpointId);
            if (!await TryPerDeviceActionAsync(operationId, headset, endpointId, "disconnect", cancellationToken))
            {
                return Publish(operationId, headsetId, "failed", endpointId, endpointId, headset.ActiveEndpointId,
                    "This endpoint does not support disconnecting one Bluetooth device");
            }

            if (headset.ActiveEndpointId == endpointId)
            {
                headset.ActiveEndpointId = null;
                SaveConfigurations(Configurations);
            }
            return Publish(operationId, headsetId, "completed", endpointId, endpointId, null,
                "Bluetooth device disconnected");
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth disconnect {operationId} failed: {ex}");
            return Publish(operationId, headsetId, "failed", endpointId, endpointId, null, ex.Message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<BluetoothHandoffState> HandoffCoreAsync(
        string operationId,
        string headsetId,
        string targetEndpointId,
        CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        var sourceRadioDisabled = false;
        HeadsetConfiguration? headset = null;
        string? sourceEndpointId = null;
        try
        {
            headset = Configurations.FirstOrDefault(h => h.Id == headsetId);
            sourceEndpointId = headset?.ActiveEndpointId;
            if (headset is null || !headset.EndpointDeviceKeys.ContainsKey(targetEndpointId))
            {
                return Publish(operationId, headsetId, "failed", sourceEndpointId, targetEndpointId, null,
                    "Target endpoint is not configured for this headset");
            }

            Publish(operationId, headsetId, "disconnecting", sourceEndpointId, targetEndpointId, sourceEndpointId);
            if (!string.IsNullOrEmpty(sourceEndpointId) && sourceEndpointId != targetEndpointId)
            {
                var disconnectedPerDevice = await TryPerDeviceActionAsync(
                    operationId, headset, sourceEndpointId, "disconnect", cancellationToken);
                if (!disconnectedPerDevice)
                {
                    var result = await ExecuteEndpointCommandAsync(
                        sourceEndpointId,
                        new BluetoothHandoffCommand
                        {
                            OperationId = operationId,
                            Action = "setRadio",
                            Enabled = false,
                        },
                        cancellationToken);
                    if (!result.Success) throw new InvalidOperationException("Failed to disable Bluetooth on source endpoint");
                    sourceRadioDisabled = true;
                }
            }

            Publish(operationId, headsetId, "connecting", sourceEndpointId, targetEndpointId, null);
            var connectedPerDevice = await TryPerDeviceActionAsync(
                operationId, headset, targetEndpointId, "connect", cancellationToken);
            if (!connectedPerDevice)
            {
                var targetCatalog = await GetCatalogAsync(targetEndpointId, cancellationToken);
                if (!targetCatalog.SupportsPerDeviceControl && targetCatalog.RadioEnabled)
                {
                    var disable = await ExecuteEndpointCommandAsync(
                        targetEndpointId,
                        new BluetoothHandoffCommand
                        {
                            OperationId = operationId,
                            Action = "setRadio",
                            Enabled = false,
                        },
                        cancellationToken);
                    if (!disable.Success) throw new InvalidOperationException("Failed to cycle Bluetooth on target endpoint");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }

                var result = await ExecuteEndpointCommandAsync(
                    targetEndpointId,
                    new BluetoothHandoffCommand
                    {
                        OperationId = operationId,
                        Action = "setRadio",
                        Enabled = true,
                    },
                    cancellationToken);
                if (!result.Success) throw new InvalidOperationException("Failed to enable Bluetooth on target endpoint");
            }

            if (!await WaitForConnectionAsync(headset, targetEndpointId, TimeSpan.FromSeconds(20), cancellationToken))
            {
                throw new TimeoutException("Headset did not connect to the target endpoint");
            }

            string? completionMessage = null;
            if (sourceRadioDisabled && sourceEndpointId is not null)
            {
                var restore = await ExecuteEndpointCommandAsync(
                    sourceEndpointId,
                    new BluetoothHandoffCommand
                    {
                        OperationId = operationId,
                        Action = "setRadio",
                        Enabled = true,
                    },
                    cancellationToken);
                sourceRadioDisabled = !restore.Success;

                if (restore.Success)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    if (!await IsConnectedAsync(headset, targetEndpointId, cancellationToken))
                    {
                        await ExecuteEndpointCommandAsync(
                            sourceEndpointId,
                            new BluetoothHandoffCommand
                            {
                                OperationId = operationId,
                                Action = "setRadio",
                                Enabled = false,
                            },
                            cancellationToken);
                        sourceRadioDisabled = true;
                        if (!await WaitForConnectionAsync(headset, targetEndpointId, TimeSpan.FromSeconds(10), cancellationToken))
                        {
                            throw new InvalidOperationException("Source endpoint reclaimed the headset");
                        }
                        completionMessage = "Source Bluetooth remains off because this headset reconnects to it automatically";
                    }
                }
            }

            headset.ActiveEndpointId = targetEndpointId;
            SaveConfigurations(Configurations);
            return Publish(operationId, headsetId, "completed", sourceEndpointId, targetEndpointId, targetEndpointId,
                completionMessage);
        }
        catch (Exception ex)
        {
            logger.Warn($"Bluetooth handoff {operationId} failed: {ex}");
            if (sourceRadioDisabled && sourceEndpointId is not null)
            {
                await ExecuteEndpointCommandAsync(
                    sourceEndpointId,
                    new BluetoothHandoffCommand
                    {
                        OperationId = operationId,
                        Action = "setRadio",
                        Enabled = true,
                    },
                    CancellationToken.None);
            }
            return Publish(operationId, headsetId, "failed", sourceEndpointId, targetEndpointId,
                headset?.ActiveEndpointId, ex.Message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<bool> TryPerDeviceActionAsync(
        string operationId,
        HeadsetConfiguration headset,
        string endpointId,
        string action,
        CancellationToken cancellationToken)
    {
        if (!headset.EndpointDeviceKeys.TryGetValue(endpointId, out var deviceKey)) return false;
        var catalog = await GetCatalogAsync(endpointId, cancellationToken);
        if (!catalog.ControllerAvailable || !catalog.SupportsPerDeviceControl) return false;

        var result = await ExecuteEndpointCommandAsync(
            endpointId,
            new BluetoothHandoffCommand
            {
                OperationId = operationId,
                Action = action,
                DeviceKey = deviceKey,
            },
            cancellationToken);
        return result.Success;
    }

    private async Task<BluetoothHandoffResult> ExecuteEndpointCommandAsync(
        string endpointId,
        BluetoothHandoffCommand command,
        CancellationToken cancellationToken)
    {
        var localEndpointId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
        if (endpointId == localEndpointId)
        {
            return await localController.ExecuteAsync(command);
        }

        var endpoint = deviceManager.PairedDevices.FirstOrDefault(d => d.Id == endpointId);
        if (endpoint is null || !endpoint.IsConnected ||
            !endpoint.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
        {
            return FailedResult(command, "endpoint_unavailable");
        }

        var key = ResultKey(endpointId, command.OperationId, command.Action);
        var waiter = new TaskCompletionSource<BluetoothHandoffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        resultWaiters[key] = waiter;
        try
        {
            endpoint.SendMessage(command);
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        }
        catch (TimeoutException)
        {
            return FailedResult(command, "endpoint_timeout");
        }
        finally
        {
            resultWaiters.TryRemove(key, out _);
        }
    }

    private async Task<bool> WaitForConnectionAsync(
        HeadsetConfiguration headset,
        string endpointId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsConnectedAsync(headset, endpointId, cancellationToken)) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return false;
    }

    private async Task<bool> IsConnectedAsync(
        HeadsetConfiguration headset,
        string endpointId,
        CancellationToken cancellationToken)
    {
        if (!headset.EndpointDeviceKeys.TryGetValue(endpointId, out var deviceKey)) return false;
        var catalog = await GetCatalogAsync(endpointId, cancellationToken);
        return catalog.Devices.Any(d => d.DeviceKey == deviceKey && d.IsConnected);
    }

    private BluetoothHandoffState Publish(
        string operationId,
        string headsetId,
        string status,
        string? sourceEndpointId,
        string targetEndpointId,
        string? activeEndpointId,
        string? message = null)
    {
        var state = new BluetoothHandoffState
        {
            OperationId = operationId,
            HeadsetId = headsetId,
            Status = status,
            SourceEndpointId = sourceEndpointId,
            TargetEndpointId = targetEndpointId,
            ActiveEndpointId = activeEndpointId,
            Message = message,
        };
        CurrentState = state;
        StateChanged?.Invoke(this, state);
        foreach (var device in deviceManager.PairedDevices.Where(d =>
                     d.IsConnected && d.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1)))
        {
            device.SendMessage(state);
        }
        return state;
    }

    private static string ResultKey(string endpointId, string operationId, string action) =>
        $"{endpointId}:{operationId}:{action}";

    private static BluetoothDeviceCatalog CatalogError(string requestId, string errorCode) => new()
    {
        RequestId = requestId,
        ControllerAvailable = false,
        RadioEnabled = false,
        SupportsPerDeviceControl = false,
        ErrorCode = errorCode,
    };

    private static BluetoothHandoffResult FailedResult(BluetoothHandoffCommand command, string errorCode) => new()
    {
        OperationId = command.OperationId,
        Action = command.Action,
        Success = false,
        ErrorCode = errorCode,
    };
}
