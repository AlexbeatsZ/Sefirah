using Sefirah.Data.Models;
using Sefirah.Services;

namespace Sefirah.ViewModels;

public sealed partial class HeadsetHandoffViewModel : BaseViewModel
{
    private readonly IHeadsetHandoffService handoffService = Ioc.Default.GetRequiredService<IHeadsetHandoffService>();
    private readonly IDeviceManager deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
    private bool initialized;
    private DispatcherTimer? refreshTimer;
    private List<HeadsetConfiguration> latestConfigurations = [];

    public ObservableCollection<HeadsetEndpointOption> Endpoints { get; } = [];
    public ObservableCollection<HeadsetDeviceItem> ConnectedDevices { get; } = [];
    public ObservableCollection<HeadsetDeviceItem> DisconnectedDevices { get; } = [];
    public ObservableCollection<HeadsetVisibilityOption> VisibilityOptions { get; } = [];
    public IReadOnlyList<string> FilterOptions { get; } =
    [
        "BluetoothFilterAll".GetLocalizedResource(),
        "BluetoothFilterHeadsets".GetLocalizedResource(),
    ];

    public string? LocalEndpointId { get; private set; }
    public string? SelectedEndpointId { get; private set; }

    [ObservableProperty]
    public partial string SelectedEndpointName { get; set; } = "BluetoothSelectedDevice".GetLocalizedResource();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "BluetoothStatusRefreshing".GetLocalizedResource();

    [ObservableProperty]
    public partial int SelectedFilterIndex { get; set; }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        handoffService.StateChanged += OnStateChanged;
        handoffService.ConfigurationsChanged += OnConfigurationsChanged;
        deviceManager.ActiveDeviceChanged += OnActiveDeviceChanged;
        await RefreshAsync();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
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
            UpdateCountStatus();
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
        if (IsBusy || item.SourceEndpointId is null) return;
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
        BluetoothHandoffUiPolicy.CanSwitchTo(item.SourceEndpointId, endpointId, item.EndpointIds);

    public IReadOnlyList<HeadsetEndpointOption> GetOtherSwitchTargets(HeadsetDeviceItem item) =>
        Endpoints.Where(endpoint =>
                BluetoothHandoffUiPolicy.SupportsEndpoint(endpoint.Id, item.EndpointIds) &&
                BluetoothHandoffUiPolicy.IsOtherTarget(
                    endpoint.Id,
                    item.SourceEndpointId,
                    LocalEndpointId))
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
        latestConfigurations = configurations.ToList();
        ConnectedDevices.Clear();
        DisconnectedDevices.Clear();
        VisibilityOptions.Clear();

        var endpointsById = Endpoints.ToDictionary(item => item.Id, item => item.DisplayName);
        foreach (var device in deviceManager.PairedDevices)
        {
            endpointsById.TryAdd(device.Id, device.Name);
        }
        foreach (var headset in latestConfigurations.OrderBy(item => item.DisplayName))
        {
            VisibilityOptions.Add(new HeadsetVisibilityOption(headset.Id, headset.DisplayName, headset.IsVisible));
            if (!headset.IsVisible || !MatchesFilter(headset)) continue;

            if (BluetoothHandoffUiPolicy.IsConnected(headset.ActiveEndpointId))
            {
                var endpointName = endpointsById.GetValueOrDefault(
                    headset.ActiveEndpointId!,
                    headset.ActiveEndpointId!);
                ConnectedDevices.Add(CreateItem(
                    headset,
                    headset.ActiveEndpointId,
                    endpointName,
                    HeadsetDeviceSection.Connected));
            }
            else
            {
                DisconnectedDevices.Add(CreateItem(
                    headset,
                    null,
                    null,
                    HeadsetDeviceSection.Disconnected));
            }
        }
    }

    private bool MatchesFilter(HeadsetConfiguration configuration) =>
        SelectedFilterIndex == 0 || configuration.IsHeadset;

    partial void OnSelectedFilterIndexChanged(int value)
    {
        if (value is < 0 or > 1)
        {
            SelectedFilterIndex = 0;
            return;
        }
        ReloadSections(latestConfigurations);
        UpdateCountStatus();
    }

    private void UpdateCountStatus()
    {
        var visibleCount = latestConfigurations.Count(item => item.IsVisible && MatchesFilter(item));
        StatusText = visibleCount == 0
            ? "BluetoothStatusNone".GetLocalizedResource()
            : string.Format("BluetoothStatusUpdated".GetLocalizedResource(), visibleCount);
    }

    private static HeadsetDeviceItem CreateItem(
        HeadsetConfiguration headset,
        string? endpointId,
        string? endpointName,
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
    Connected,
    Disconnected,
}

public sealed partial record HeadsetEndpointOption(string Id, string DisplayName);

public sealed record HeadsetDeviceItem(
    string HeadsetId,
    string DisplayName,
    string? SourceEndpointId,
    string? SourceEndpointName,
    IReadOnlyList<string> EndpointIds,
    HeadsetDeviceSection Section)
{
    public bool CanDisconnect => SourceEndpointId is not null;

    public string DisplayText => SourceEndpointName is null
        ? DisplayName
        : string.Format(
            "BluetoothConnectedDeviceName".GetLocalizedResource(),
            DisplayName,
            SourceEndpointName);
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
