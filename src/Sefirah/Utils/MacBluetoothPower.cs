#if !WINDOWS
using System.Runtime.InteropServices;

namespace Sefirah.Utils;

/// <summary>
/// macOS Bluetooth controller power through IOBluetooth's preference functions (the same calls blueutil uses).
/// There is no public API for this. The bundle must declare NSBluetoothAlwaysUsageDescription, otherwise TCC
/// terminates the process on the first call; the user is asked once to allow Bluetooth for Sefirah.
/// </summary>
internal static class MacBluetoothPower
{
    private const string Framework = "/System/Library/Frameworks/IOBluetooth.framework/IOBluetooth";
    private static readonly Lazy<(GetPower? Get, SetPower? Set)> functions = new(Load);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPower();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetPower(int state);

    public static bool IsSupported => OperatingSystem.IsMacOS() && functions.Value.Get is not null && functions.Value.Set is not null;

    /// <summary>Current power state, or null when it cannot be read.</summary>
    public static bool? GetEnabled()
    {
        if (!IsSupported) return null;
        try { return functions.Value.Get!() != 0; }
        catch (Exception) { return null; }
    }

    /// <summary>Requests a power change and waits until the controller reports it (about 5 s at most).</summary>
    public static async Task<bool> TrySetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return false;
        if (GetEnabled() == enabled) return true;
        try { functions.Value.Set!(enabled ? 1 : 0); }
        catch (Exception) { return false; }
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (GetEnabled() == enabled) return true;
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }

    private static (GetPower?, SetPower?) Load()
    {
        if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(Framework, out var handle)) return (null, null);
        var get = NativeLibrary.TryGetExport(handle, "IOBluetoothPreferenceGetControllerPowerState", out var getPointer)
            ? Marshal.GetDelegateForFunctionPointer<GetPower>(getPointer) : null;
        var set = NativeLibrary.TryGetExport(handle, "IOBluetoothPreferenceSetControllerPowerState", out var setPointer)
            ? Marshal.GetDelegateForFunctionPointer<SetPower>(setPointer) : null;
        return (get, set);
    }
}
#endif
