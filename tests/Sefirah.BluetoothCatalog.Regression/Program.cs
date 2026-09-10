using Sefirah.Services;
using Sefirah.Extensions;
using Sefirah.Platforms.Desktop.Mac;

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

AssertTrue(
    BluetoothHandoffFallbackPolicy.ShouldUsePerDeviceBystanderSuppression(
        supportsPerDeviceControl: true,
        headsetIsConnected: true),
    "A connected bystander with per-device control must disconnect only the headset.");

AssertFalse(
    BluetoothHandoffFallbackPolicy.ShouldDisableBystanderRadio(
        supportsPerDeviceControl: true),
    "A per-device-capable bystander must not disable the whole Bluetooth radio.");

AssertTrue(
    BluetoothHandoffFallbackPolicy.ShouldDisableBystanderRadio(
        supportsPerDeviceControl: false),
    "A radio-only Windows endpoint retains the existing suppression fallback.");

AssertTrue(
    BluetoothHandoffUiPolicy.IsConnected("phone"),
    "Any active endpoint belongs in the single connected section.");

AssertFalse(
    BluetoothHandoffUiPolicy.IsConnected(null),
    "A device without an active endpoint belongs in the disconnected section.");

AssertFalse(
    BluetoothHandoffUiPolicy.IsEndpointAvailable(
        "phone",
        new HashSet<string>(StringComparer.Ordinal) { "phone" }),
    "An endpoint with an unavailable Bluetooth controller must not accept actions.");

AssertTrue(
    BluetoothHandoffUiPolicy.IsEndpointAvailable(
        "phone",
        new HashSet<string>(StringComparer.Ordinal)),
    "An endpoint absent from the unavailable set remains actionable.");

AssertTrue(
    BluetoothHandoffUiPolicy.CanSwitchTo(
        sourceEndpointId: "server",
        targetEndpointId: "phone",
        supportedEndpointIds: new[] { "phone", "server" }),
    "The selected endpoint remains available through the generic other-device action.");

AssertTrue(
    BluetoothHandoffUiPolicy.CanSwitchTo(
        sourceEndpointId: null,
        targetEndpointId: "phone",
        supportedEndpointIds: Array.Empty<string>()),
    "Legacy configurations without an endpoint allowlist must remain switchable.");

AssertFalse(
    BluetoothHandoffUiPolicy.CanSwitchTo(
        sourceEndpointId: null,
        targetEndpointId: "phone",
        supportedEndpointIds: new[] { "server" }),
    "An explicit endpoint allowlist must still disable unsupported targets.");

AssertFalse(
    BluetoothHandoffUiPolicy.IsOtherTarget(
        candidateEndpointId: "pc",
        sourceEndpointId: "phone",
        localEndpointId: "pc"),
    "The local PC must not appear in the other-device picker.");

const string macSystemProfilerJson = """
{
  "SPBluetoothDataType": [
    {
      "controller_properties": {
        "controller_state": "attrib_on"
      },
      "device_connected": [
        {
          "QCY AilyBuds Lite": {
            "device_address": "84:AC:60:B4:EC:25",
            "device_minorType": "Headset"
          }
        }
      ],
      "device_not_connected": [
        {
          "QCY AilyBuds Lite": {
            "device_address": "84:AC:60:B4:EC:25",
            "device_minorType": "Headset"
          }
        },
        {
          "MCHOSE A7 V3 Pro": {
            "device_address": "C1:9A:78:C8:35:F4",
            "device_minorType": "Mouse"
          }
        }
      ]
    }
  ]
}
""";
var macCatalog = MacBluetoothCatalogParser.Parse(macSystemProfilerJson);
AssertTrue(macCatalog.ControllerAvailable, "The macOS system_profiler controller must be detected.");
AssertTrue(macCatalog.RadioEnabled, "The macOS attrib_on radio state must be detected.");
AssertTrue(macCatalog.Devices.Count == 2, "The macOS catalog must merge repeated addresses across groups.");
var qcy = macCatalog.Devices.Single(device => device.BluetoothAddress == "84AC60B4EC25");
AssertTrue(qcy.IsConnected, "The device_connected group must define connected state.");
AssertTrue(qcy.IsHeadset, "The macOS device category must classify the headset without name heuristics.");

var first = new CollectionItem("first", 1);
var second = new CollectionItem("second", 2);
var synchronized = new System.Collections.ObjectModel.ObservableCollection<CollectionItem> { first, second };
synchronized.SynchronizeByKey(
    [new CollectionItem("first", 1), new CollectionItem("second", 2)],
    item => item.Id);
AssertTrue(
    ReferenceEquals(first, synchronized[0]) && ReferenceEquals(second, synchronized[1]),
    "An unchanged refresh must preserve item instances and their live pointer state.");
synchronized.SynchronizeByKey(
    [new CollectionItem("second", 2), new CollectionItem("first", 1)],
    item => item.Id);
AssertTrue(
    ReferenceEquals(second, synchronized[0]) && ReferenceEquals(first, synchronized[1]),
    "A reordered refresh must move existing item instances instead of recreating them.");

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

internal sealed record CollectionItem(string Id, int Value);
