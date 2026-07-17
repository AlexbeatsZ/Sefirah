using Windows.Devices.Radios;

namespace Sefirah.Platforms.Windows.Bluetooth;

public sealed class BluetoothRadioManager(ILogger logger)
{
    private Radio? bluetoothRadio;

    public bool IsBluetoothSupported { get; private set; }

    public bool IsBluetoothRadioOn => bluetoothRadio?.State is RadioState.On;

    public RadioState RadioState => bluetoothRadio?.State ?? RadioState.Unknown;

    public event Action<RadioState>? RadioStateChanged;

    public async Task<bool> RefreshAsync()
    {
        try
        {
            if (bluetoothRadio is not null)
            {
                return true;
            }

            var radios = await Radio.GetRadiosAsync();
            bluetoothRadio = radios.FirstOrDefault(r => r.Kind is RadioKind.Bluetooth);
            if (bluetoothRadio is null)
            {
                IsBluetoothSupported = false;
                return false;
            }

            bluetoothRadio.StateChanged += OnBluetoothRadioStateChanged;
            IsBluetoothSupported = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to refresh bluetooth radio: {ex}");
            IsBluetoothSupported = false;
            return false;
        }
    }

    public async Task<bool> TryEnableAsync()
    {
        return await TrySetStateAsync(true);
    }

    public async Task<bool> TrySetStateAsync(bool enabled)
    {
        if (!await RefreshAsync() || bluetoothRadio is null)
        {
            return false;
        }

        var targetState = enabled ? RadioState.On : RadioState.Off;
        if (bluetoothRadio.State == targetState)
        {
            return true;
        }

        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access is not RadioAccessStatus.Allowed)
            {
                logger.Debug($"Bluetooth radio access not allowed: {access}");
                return false;
            }

            var setState = await bluetoothRadio.SetStateAsync(targetState);
            if (setState is not RadioAccessStatus.Allowed)
            {
                logger.Debug($"Bluetooth radio state change denied: {setState}");
                return false;
            }

            return bluetoothRadio.State == targetState;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to change bluetooth radio state: {ex}");
            return false;
        }
    }

    private void OnBluetoothRadioStateChanged(Radio sender, object args) => RadioStateChanged?.Invoke(sender.State);
}
