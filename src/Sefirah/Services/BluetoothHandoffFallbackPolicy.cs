namespace Sefirah.Services;

internal static class BluetoothHandoffFallbackPolicy
{
    public static bool ShouldCycleTargetRadio(bool perDeviceConnectionSucceeded, bool targetRadioEnabled) =>
        !perDeviceConnectionSucceeded && targetRadioEnabled;
}
