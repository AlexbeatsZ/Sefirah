using Sefirah.Data.Models;

namespace Sefirah.ViewModels.Dialogs;

public partial class DeviceSelectorViewModel : ObservableObject
{
    public ObservableCollection<DeviceSelectorOption> Devices { get; } = [];

    public bool IsSingleSelection { get; }

    public List<PairedDevice> SelectedDevices => Devices
        .Where(option => option.IsSelected)
        .Select(option => option.Device)
        .ToList();

    public DeviceSelectorViewModel(List<PairedDevice> devices, bool isSingleSelection = false)
    {
        IsSingleSelection = isSingleSelection;
        foreach (var device in devices)
        {
            Devices.Add(new DeviceSelectorOption(device));
        }
    }

    public void SetDeviceSelected(DeviceSelectorOption option, bool isSelected)
    {
        if (isSelected && IsSingleSelection)
        {
            foreach (var other in Devices.Where(item => item != option))
            {
                other.IsSelected = false;
            }
        }

        option.IsSelected = isSelected;
    }
}

public sealed partial class DeviceSelectorOption(PairedDevice device) : ObservableObject
{
    public PairedDevice Device { get; } = device;
    public string DisplayName => device.Name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

