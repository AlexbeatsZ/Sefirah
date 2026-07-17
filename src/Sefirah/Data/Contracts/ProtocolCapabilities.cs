namespace Sefirah.Data.Contracts;

public static class ProtocolCapabilities
{
    public const string BluetoothHandoffV1 = "bluetooth-handoff-v1";
    public const string ClipboardEventV1 = "clipboard-event-v1";

    public static readonly IReadOnlyList<string> Local =
    [
        BluetoothHandoffV1,
        ClipboardEventV1,
    ];
}
