using Sefirah.Data.Models;

namespace Sefirah.ViewModels;

public sealed partial class HeadsetHandoffViewModel : BaseViewModel
{
    private readonly IHeadsetHandoffService handoffService = Ioc.Default.GetRequiredService<IHeadsetHandoffService>();
    private readonly IDeviceManager deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
    private bool initialized;

    public ObservableCollection<HeadsetEndpointOption> Endpoints { get; } = [];
    public ObservableCollection<HeadsetDeviceItem> SelectedConnectedDevices { get; } = [];
    public ObservableCollection<HeadsetDeviceItem> OtherConnectedDevices { get; } = [];
    public ObservableCollection<HeadsetDeviceItem> SavedDisconnectedDevices { get; } = [];
    public ObservableCollection<HeadsetVisibilityOption> VisibilityOptions { get; } = [];

    public string? LocalEndpointId { get; private set; }
    public string? SelectedEndpointId { get; private set; }

    [ObservableProperty]
    public partial string SelectedEndpointName { get; set; } = "BluetoothSelectedDevice".GetLocalizedResource();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "BluetoothStatusRefreshing".GetLocalizedResource();

    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        handoffService.StateChanged += OnStateChanged;
        handoffService.ConfigurationsChanged += OnConfigurationsChanged;
        deviceManager.ActiveDeviceChanged += OnActiveDeviceChanged;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await RefreshEndpointsAsync();
            var report = await handoffService.DiscoverAsync();
            ReloadSections(report.Headsets);
            StatusText = report.Headsets.Count == 0
                ? "BluetoothStatusNone".GetLocalizedResource()
                : string.Format("BluetoothStatusUpdated".GetLocalizedResource(), report.Headsets.Count);
        }
        catch (Exception ex)
        {
            StatusText = string.Format("BluetoothStatusRefreshFailed".GetLocalizedResource(), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync(HeadsetDeviceItem item)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await handoffService.DisconnectAsync(item.HeadsetId, item.SourceEndpointId);
            await RefreshEndpointsAsync();
            ReloadSections(handoffService.Configurations);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SwitchAsync(HeadsetDeviceItem item, string targetEndpointId)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await handoffService.HandoffAsync(item.HeadsetId, targetEndpointId);
            await RefreshEndpointsAsync();
            ReloadSections(handoffService.Configurations);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SetVisibility(HeadsetVisibilityOption option, bool isVisible)
    {
        var configurations = handoffService.Configurations.ToList();
        var headset = configurations.FirstOrDefault(item => item.Id == option.HeadsetId);
        if (headset is null) return;
        headset.IsVisible = isVisible;
        handoffService.SaveConfigurations(configurations);
        ReloadSections(configurations);
    }

    public bool CanSwitchTo(HeadsetDeviceItem item, string? endpointId) =>
        endpointId is not null &&
        (item.Section == HeadsetDeviceSection.SavedDisconnected || endpointId != item.SourceEndpointId) &&
        item.EndpointIds.Contains(endpointId);

    public IReadOnlyList<HeadsetEndpointOption> GetOtherSwitchTargets(HeadsetDeviceItem item) =>
        Endpoints.Where(endpoint =>
                item.EndpointIds.Contains(endpoint.Id) &&
                endpoint.Id != item.SourceEndpointId &&
                endpoint.Id != LocalEndpointId &&
                endpoint.Id != SelectedEndpointId)
            .ToList();

    public IReadOnlyList<PairedDevice> GetOtherSwitchDevices(HeadsetDeviceItem item)
    {
        var targetIds = GetOtherSwitchTargets(item).Select(endpoint => endpoint.Id).ToHashSet();
        return deviceManager.PairedDevices.Where(device => targetIds.Contains(device.Id)).ToList();
    }

    private bool CanRefresh() => !IsBusy;

    private async Task RefreshEndpointsAsync()
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        LocalEndpointId = local.DeviceId;
        Endpoints.Clear();
        Endpoints.Add(new HeadsetEndpointOption(
            local.DeviceId,
            string.Format("BluetoothThisPc".GetLocalizedResource(), local.DeviceName)));
        foreach (var device in deviceManager.PairedDevices.Where(item =>
                     item.IsConnected && item.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1)))
        {
            Endpoints.Add(new HeadsetEndpointOption(device.Id, device.Name));
        }

        var activeDevice = deviceManager.ActiveDevice;
        var selected = activeDevice is not null && activeDevice.IsConnected &&
                       activeDevice.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1)
            ? Endpoints.FirstOrDefault(endpoint => endpoint.Id == activeDevice.Id)
            : Endpoints.FirstOrDefault();
        SelectedEndpointId = selected?.Id;
        SelectedEndpointName = selected?.DisplayName ?? "BluetoothNoSelectedDevice".GetLocalizedResource();
    }

    private void ReloadSections(IEnumerable<HeadsetConfiguration> configurations)
    {
        SelectedConnectedDevices.Clear();
        OtherConnectedDevices.Clear();
        SavedDisconnectedDevices.Clear();
        VisibilityOptions.Clear();

        var endpointsById = Endpoints.ToDictionary(item => item.Id, item => item.DisplayName);
        foreach (var headset in configurations.OrderBy(item => item.DisplayName))
        {
            VisibilityOptions.Add(new HeadsetVisibilityOption(headset.Id, headset.DisplayName, headset.IsVisible));
            if (!headset.IsVisible || SelectedEndpointId is null) continue;

            if (headset.ActiveEndpointId == SelectedEndpointId)
            {
                SelectedConnectedDevices.Add(CreateItem(headset, SelectedEndpointId, SelectedEndpointName,
                    HeadsetDeviceSection.SelectedConnected));
            }
            else if (!string.IsNullOrEmpty(headset.ActiveEndpointId))
            {
                var endpointName = endpointsById.GetValueOrDefault(headset.ActiveEndpointId, "Another device");
                OtherConnectedDevices.Add(CreateItem(headset, headset.ActiveEndpointId, endpointName,
                    HeadsetDeviceSection.OtherConnected));
            }
            else if (headset.EndpointDeviceKeys.ContainsKey(SelectedEndpointId))
            {
                SavedDisconnectedDevices.Add(CreateItem(headset, SelectedEndpointId, SelectedEndpointName,
                    HeadsetDeviceSection.SavedDisconnected));
            }
        }
    }

    private static HeadsetDeviceItem CreateItem(
        HeadsetConfiguration headset,
        string endpointId,
        string endpointName,
        HeadsetDeviceSection section) => new(
        headset.Id,
        headset.DisplayName,
        endpointId,
        endpointName,
        headset.EndpointDeviceKeys.Keys.ToList(),
        section);

    private async void OnActiveDeviceChanged(object? sender, PairedDevice? device)
    {
        await RefreshAsync();
    }

    private void OnStateChanged(object? sender, BluetoothHandoffState state)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            StatusText = state.Status switch
            {
                "disconnecting" => "BluetoothStatusDisconnecting".GetLocalizedResource(),
                "connecting" => "BluetoothStatusConnecting".GetLocalizedResource(),
                "completed" => "BluetoothStatusCompleted".GetLocalizedResource(),
                "failed" when !string.IsNullOrWhiteSpace(state.Message) =>
                    string.Format("BluetoothStatusFailedDetail".GetLocalizedResource(), state.Message),
                "failed" => "BluetoothStatusFailed".GetLocalizedResource(),
                _ => state.Status,
            };
        });
    }

    private void OnConfigurationsChanged(object? sender, EventArgs eventArgs)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() => ReloadSections(handoffService.Configurations));
    }
}

public enum HeadsetDeviceSection
{
    SelectedConnected,
    OtherConnected,
    SavedDisconnected,
}

public sealed partial record HeadsetEndpointOption(string Id, string DisplayName);

public sealed record HeadsetDeviceItem(
    string HeadsetId,
    string DisplayName,
    string SourceEndpointId,
    string SourceEndpointName,
    IReadOnlyList<string> EndpointIds,
    HeadsetDeviceSection Section)
{
    public bool CanDisconnect => Section != HeadsetDeviceSection.SavedDisconnected;

    public string ConnectionText => Section switch
    {
        HeadsetDeviceSection.SelectedConnected => $"Connected to {SourceEndpointName}",
        HeadsetDeviceSection.OtherConnected => $"Connected to {SourceEndpointName}",
        _ => $"Saved on {SourceEndpointName}",
    };
}

public sealed partial class HeadsetVisibilityOption(
    string headsetId,
    string displayName,
    bool isVisible) : ObservableObject
{
    public string HeadsetId { get; } = headsetId;
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = isVisible;
}
