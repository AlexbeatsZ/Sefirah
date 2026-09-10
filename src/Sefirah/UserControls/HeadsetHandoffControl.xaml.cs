using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Sefirah.Dialogs;
using Sefirah.ViewModels;

namespace Sefirah.UserControls;

public sealed partial class HeadsetHandoffControl : UserControl
{
    public HeadsetHandoffViewModel ViewModel { get; } = Ioc.Default.GetRequiredService<HeadsetHandoffViewModel>();

    public HeadsetHandoffControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void DeviceButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: HeadsetDeviceItem item } button) return;

        var menu = new MenuFlyout();
        menu.Closed += (_, _) => ResetDeviceButtonVisualState(button);
        menu.Items.Add(CreateEndpointAction(
            "BluetoothSwitchToThisDevice",
            item,
            ViewModel.LocalEndpointId));
        menu.Items.Add(CreateOtherDeviceAction(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateDisconnectAction(item));

        menu.ShowAt(button, new FlyoutShowOptions
        {
            Position = e.GetPosition(button),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void DeviceButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ResetDeviceButtonVisualState(button);
        }
    }

    private void DeviceButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ResetDeviceButtonVisualState(button);
        }
    }

    private static void ResetDeviceButtonVisualState(Button button)
    {
        VisualStateManager.GoToState(button, "Normal", false);
    }

    private MenuFlyoutItem CreateEndpointAction(
        string resourceName,
        HeadsetDeviceItem item,
        string? targetEndpointId)
    {
        var action = new MenuFlyoutItem
        {
            Text = resourceName.GetLocalizedResource(),
            IsEnabled = ViewModel.CanSwitchTo(item, targetEndpointId),
        };
        action.Click += async (_, _) =>
        {
            if (targetEndpointId is not null)
            {
                await ViewModel.SwitchAsync(item, targetEndpointId);
            }
        };
        return action;
    }

    private MenuFlyoutItem CreateOtherDeviceAction(HeadsetDeviceItem item)
    {
        var targets = ViewModel.GetOtherSwitchDevices(item);
        var action = new MenuFlyoutItem
        {
            Text = "BluetoothSwitchToOtherDevice".GetLocalizedResource(),
            IsEnabled = targets.Count > 0,
        };
        action.Click += async (_, _) => await ShowOtherDeviceDialogAsync(item, targets);
        return action;
    }

    private MenuFlyoutItem CreateDisconnectAction(HeadsetDeviceItem item)
    {
        var action = new MenuFlyoutItem
        {
            Text = "Disconnect".GetLocalizedResource(),
            IsEnabled = item.CanDisconnect,
        };
        action.Click += async (_, _) => await ViewModel.DisconnectAsync(item);
        return action;
    }

    private async Task ShowOtherDeviceDialogAsync(
        HeadsetDeviceItem item,
        IReadOnlyList<Data.Models.PairedDevice> targets)
    {
        if (targets.Count == 0) return;

        var selector = new DeviceSelectorDialog(targets.ToList(), singleSelection: true);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = string.Format("BluetoothSwitchTitle".GetLocalizedResource(), item.DisplayName),
            Content = selector,
            PrimaryButtonText = "BluetoothSwitchDevice".GetLocalizedResource(),
            CloseButtonText = "Cancel".GetLocalizedResource(),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary &&
            selector.ViewModel.SelectedDevices.FirstOrDefault() is { } target)
        {
            await ViewModel.SwitchAsync(item, target.Id);
        }
    }

    private void VisibilityCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: HeadsetVisibilityOption option } checkBox)
        {
            ViewModel.SetVisibility(option, checkBox.IsChecked is true);
        }
    }
}
