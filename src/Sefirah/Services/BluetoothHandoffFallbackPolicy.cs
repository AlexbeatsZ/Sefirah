namespace Sefirah.Services;

internal static class BluetoothHandoffFallbackPolicy
{
    public static bool ShouldCycleTargetRadio(bool perDeviceConnectionSucceeded, bool targetRadioEnabled) =>
        !perDeviceConnectionSucceeded && targetRadioEnabled;

    public static bool ShouldUsePerDeviceBystanderSuppression(
        bool supportsPerDeviceControl,
        bool headsetIsConnected) =>
        supportsPerDeviceControl && headsetIsConnected;

    public static bool ShouldDisableBystanderRadio(bool supportsPerDeviceControl) =>
        !supportsPerDeviceControl;
}
