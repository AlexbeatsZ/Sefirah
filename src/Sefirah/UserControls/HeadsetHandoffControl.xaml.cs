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

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HeadsetDeviceItem item })
        {
            await ViewModel.DisconnectAsync(item);
        }
    }

    private async void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HeadsetDeviceItem item }) return;

        if (item.Section is HeadsetDeviceSection.OtherConnected or HeadsetDeviceSection.SavedDisconnected)
        {
            if (ViewModel.SelectedEndpointId is not null)
            {
                await ViewModel.SwitchAsync(item, ViewModel.SelectedEndpointId);
            }
            return;
        }

        var targets = ViewModel.GetSwitchTargets(item);
        if (targets.Count == 0) return;
        var selector = new ComboBox
        {
            Header = "Connect to",
            ItemsSource = targets,
            DisplayMemberPath = nameof(HeadsetEndpointOption.DisplayName),
            SelectedIndex = 0,
            MinWidth = 280,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Switch {item.DisplayName}",
            Content = selector,
            PrimaryButtonText = "Switch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary &&
            selector.SelectedItem is HeadsetEndpointOption target)
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
