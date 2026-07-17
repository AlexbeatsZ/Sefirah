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
}
