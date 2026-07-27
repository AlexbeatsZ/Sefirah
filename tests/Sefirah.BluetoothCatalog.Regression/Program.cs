using Sefirah.Services;

AssertEqual(
    "84AC60B4EC25",
    BluetoothDeviceIdentity.NormalizeAddress("84:ac:60:b4:ec:25"),
    "Colon-separated Bluetooth addresses must normalize.");

AssertEqual(
    "84AC60B4EC25",
    BluetoothDeviceIdentity.ExtractAddress(
        "Bluetooth#Bluetooth10:f6:0a:b3:d9:b7-84:ac:60:b4:ec:25"),
    "Legacy Windows device keys must resolve to the remote address, not the adapter address.");

AssertEqual(
    "address:84AC60B4EC25",
    BluetoothDeviceIdentity.CatalogKey(null, "84:AC:60:B4:EC:25", "QCY"),
    "Legacy Android catalogs must group with address-aware peers during rolling upgrades.");

AssertEqual(
    "name:Keyboard",
    BluetoothDeviceIdentity.CatalogKey(null, "opaque-device-key", " Keyboard "),
    "Devices without a usable address must fall back to a stable trimmed name.");

AssertTrue(
    BluetoothHandoffFallbackPolicy.ShouldCycleTargetRadio(
        perDeviceConnectionSucceeded: false,
        targetRadioEnabled: true),
    "A failed per-device connection must cycle an already-enabled target radio.");

AssertFalse(
    BluetoothHandoffFallbackPolicy.ShouldCycleTargetRadio(
        perDeviceConnectionSucceeded: true,
        targetRadioEnabled: true),
    "A successful per-device connection must not cycle the target radio.");

AssertFalse(
    BluetoothHandoffFallbackPolicy.ShouldCycleTargetRadio(
        perDeviceConnectionSucceeded: false,
        targetRadioEnabled: false),
    "An already-disabled target radio only needs the later enable step.");

Console.WriteLine("Bluetooth catalog identity regression: PASS");

static void AssertEqual(string expected, string? actual, string message)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool actual, string message)
{
    if (!actual) throw new InvalidOperationException(message);
}

static void AssertFalse(bool actual, string message)
{
    if (actual) throw new InvalidOperationException(message);
}
