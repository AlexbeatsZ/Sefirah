using Sefirah.Data.Models;

namespace Sefirah.ViewModels;

public sealed partial class HeadsetHandoffViewModel : BaseViewModel
{
    private readonly IHeadsetHandoffService handoffService = Ioc.Default.GetRequiredService<IHeadsetHandoffService>();
    private readonly IDeviceManager deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
    private bool initialized;

    public ObservableCollection<HeadsetConfiguration> Headsets { get; } = [];
    public ObservableCollection<HeadsetEndpointOption> Endpoints { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    public partial HeadsetConfiguration? SelectedHeadset { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    public partial HeadsetEndpointOption? SelectedEndpoint { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Discover paired headsets to begin.";

    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        handoffService.StateChanged += OnStateChanged;
        await RefreshEndpointsAsync();
        ReloadConfigurations();
    }

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task Discover()
    {
        IsBusy = true;
        try
        {
            await RefreshEndpointsAsync();
            await handoffService.DiscoverAsync();
            ReloadConfigurations();
            StatusText = Headsets.Count == 0
                ? "No headset paired with at least two endpoints was found."
                : $"Found {Headsets.Count} headset(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task Switch()
    {
        if (SelectedHeadset is null || SelectedEndpoint is null) return;
        IsBusy = true;
        try
        {
            await handoffService.HandoffAsync(SelectedHeadset.Id, SelectedEndpoint.Id);
            ReloadConfigurations();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDiscover() => !IsBusy;

    private bool CanSwitch() => !IsBusy && SelectedHeadset is not null && SelectedEndpoint is not null;

    private async Task RefreshEndpointsAsync()
    {
        var local = await deviceManager.GetLocalDeviceAsync();
        var selectedEndpointId = SelectedEndpoint?.Id;
        Endpoints.Clear();
        Endpoints.Add(new HeadsetEndpointOption(local.DeviceId, $"{local.DeviceName} (this PC)"));
        foreach (var device in deviceManager.PairedDevices.Where(d =>
                     d.IsConnected && d.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1)))
        {
            Endpoints.Add(new HeadsetEndpointOption(device.Id, device.Name));
        }
        SelectedEndpoint = Endpoints.FirstOrDefault(e => e.Id == selectedEndpointId) ?? Endpoints.FirstOrDefault();
    }

    private void ReloadConfigurations()
    {
        var selectedHeadsetId = SelectedHeadset?.Id;
        Headsets.Clear();
        foreach (var headset in handoffService.Configurations) Headsets.Add(headset);
        SelectedHeadset = Headsets.FirstOrDefault(h => h.Id == selectedHeadsetId) ?? Headsets.FirstOrDefault();
    }

    private void OnStateChanged(object? sender, BluetoothHandoffState state)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            StatusText = state.Message ?? state.Status switch
            {
                "disconnecting" => "Disconnecting the previous endpoint…",
                "connecting" => "Connecting the target endpoint…",
                "completed" => "Headset switched successfully.",
                "failed" => "Headset switch failed.",
                _ => state.Status,
            };
        });
    }
}

public sealed partial record HeadsetEndpointOption(string Id, string DisplayName);
