using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sefirah.Data.Models;
using Sefirah.Services;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="ILocalBluetoothController"/> using system_profiler
/// for catalog discovery and blueutil / IOBluetooth for connection management.
/// </summary>
public sealed class MacBluetoothController(ILogger<MacBluetoothController> logger) : ILocalBluetoothController
{
    private static bool? _blueutilAvailable;

    public async Task<BluetoothDeviceCatalog> GetCatalogAsync(string requestId)
    {
        try
        {
            var (exitCode, jsonOutput) = await RunCommandAsync("system_profiler", "SPBluetoothDataType -json");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(jsonOutput))
            {
                logger.Warn($"system_profiler returned exit code {exitCode}");
                return new BluetoothDeviceCatalog
                {
                    RequestId = requestId,
                    ControllerAvailable = false,
                    RadioEnabled = false,
                    SupportsPerDeviceControl = false,
                    ErrorCode = "profiler_failed"
                };
            }

            using var doc = JsonDocument.Parse(jsonOutput);
            if (!doc.RootElement.TryGetProperty("SPBluetoothDataType", out var array) || array.GetArrayLength() == 0)
            {
                return new BluetoothDeviceCatalog
                {
                    RequestId = requestId,
                    ControllerAvailable = false,
                    RadioEnabled = false,
                    SupportsPerDeviceControl = false,
                    ErrorCode = "no_controller"
                };
            }

            var root = array[0];
            bool radioOn = false;
            if (root.TryGetProperty("controller_properties", out var controllerProps))
            {
                if (controllerProps.TryGetProperty("controller_state", out var stateProp))
                {
                    var stateStr = stateProp.GetString() ?? "";
                    radioOn = stateStr.Contains("on", StringComparison.OrdinalIgnoreCase);
                }
            }

            var catalog = new BluetoothDeviceCatalog
            {
                RequestId = requestId,
                ControllerAvailable = true,
                RadioEnabled = radioOn,
                SupportsPerDeviceControl = await CheckPerDeviceControlSupportAsync()
            };

            var deviceDict = new Dictionary<string, BluetoothCatalogDevice>(StringComparer.OrdinalIgnoreCase);

            // Devices can appear under "devices_list", "connected_devices", or "not_connected_devices"
            ExtractDevices(root, deviceDict);

            catalog.Devices.AddRange(deviceDict.Values
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase));

            return catalog;
        }
        catch (Exception ex)
        {
            logger.Error("Failed to query macOS Bluetooth catalog", ex);
            return new BluetoothDeviceCatalog
            {
                RequestId = requestId,
                ControllerAvailable = false,
                RadioEnabled = false,
                SupportsPerDeviceControl = false,
                ErrorCode = "catalog_exception",
                ErrorMessage = ex.Message
            };
        }
    }

    private static void ExtractDevices(JsonElement root, Dictionary<string, BluetoothCatalogDevice> deviceDict)
    {
        var possibleKeys = new[] { "devices_list", "connected_devices", "not_connected_devices" };
        foreach (var key in possibleKeys)
        {
            if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in item.EnumerateObject())
                    {
                        var name = prop.Name;
                        var details = prop.Value;
                        ParseDeviceObject(name, details, deviceDict);
                    }
                }
            }
        }
    }

    private static void ParseDeviceObject(string name, JsonElement details, Dictionary<string, BluetoothCatalogDevice> deviceDict)
    {
        if (details.ValueKind != JsonValueKind.Object)
            return;

        string? rawAddress = null;
        if (details.TryGetProperty("device_address", out var addrProp))
            rawAddress = addrProp.GetString();

        if (string.IsNullOrWhiteSpace(rawAddress))
            return;

        var normalizedAddress = BluetoothDeviceIdentity.NormalizeAddress(rawAddress);
        bool isConnected = false;
        if (details.TryGetProperty("device_connected", out var connProp))
        {
            var connStr = connProp.GetString() ?? "";
            isConnected = connStr.Contains("yes", StringComparison.OrdinalIgnoreCase) ||
                          connStr.Contains("true", StringComparison.OrdinalIgnoreCase);
        }

        bool isHeadset = false;
        if (details.TryGetProperty("device_minorType", out var minorProp))
        {
            var minor = minorProp.GetString() ?? "";
            isHeadset |= minor.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
                         minor.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                         minor.Contains("Audio", StringComparison.OrdinalIgnoreCase);
        }
        if (details.TryGetProperty("device_majorType", out var majorProp))
        {
            var major = majorProp.GetString() ?? "";
            isHeadset |= major.Contains("Audio", StringComparison.OrdinalIgnoreCase);
        }
        if (details.TryGetProperty("device_isAudio", out var audioProp))
        {
            var audio = audioProp.GetString() ?? "";
            isHeadset |= audio.Contains("yes", StringComparison.OrdinalIgnoreCase);
        }

        // Common headset name heuristics if metadata is missing
        if (!isHeadset)
        {
            var upperName = name.ToUpperInvariant();
            isHeadset = upperName.Contains("HEADSET") || upperName.Contains("EARBUD") ||
                        upperName.Contains("AIRPODS") || upperName.Contains("QCY") ||
                        upperName.Contains("HEADPHONE") || upperName.Contains("WH-") ||
                        upperName.Contains("WF-");
        }

        deviceDict[normalizedAddress] = new BluetoothCatalogDevice
        {
            DeviceKey = normalizedAddress,
            DisplayName = name,
            BluetoothAddress = normalizedAddress,
            IsConnected = isConnected,
            IsHeadset = isHeadset
        };
    }

    public async Task<BluetoothHandoffResult> ExecuteAsync(BluetoothHandoffCommand command)
    {
        logger.Info($"Executing macOS Bluetooth command {command.Action} for device {command.DeviceKey} (OpId: {command.OperationId})");

        if (string.Equals(command.Action, "setRadio", StringComparison.OrdinalIgnoreCase))
        {
            var powerArg = command.Enabled == true ? "1" : "0";
            var (exitCode, output) = await RunCommandAsync("blueutil", $"--power {powerArg}");
            bool success = exitCode == 0;
            return new BluetoothHandoffResult
            {
                OperationId = command.OperationId,
                Action = command.Action,
                Success = success,
                RadioEnabled = command.Enabled,
                ErrorCode = success ? null : "radio_command_failed",
                ErrorMessage = success ? null : output
            };
        }

        var address = command.DeviceKey;
        if (string.IsNullOrWhiteSpace(address))
        {
            return new BluetoothHandoffResult
            {
                OperationId = command.OperationId,
                Action = command.Action,
                Success = false,
                ErrorCode = "missing_device_key"
            };
        }

        bool isConnect = string.Equals(command.Action, "connect", StringComparison.OrdinalIgnoreCase);

        var hasBlueutil = await CheckPerDeviceControlSupportAsync();
        if (hasBlueutil)
        {
            var actionArg = isConnect ? "--connect" : "--disconnect";
            var (exitCode, output) = await RunCommandAsync("blueutil", $"{actionArg} \"{address}\"");
            if (exitCode == 0)
            {
                return new BluetoothHandoffResult
                {
                    OperationId = command.OperationId,
                    Action = command.Action,
                    Success = true,
                    DeviceConnected = isConnect
                };
            }

            logger.Warn($"blueutil failed with exit code {exitCode}: {output}");
        }

        // Try native IOBluetooth P/Invoke
        try
        {
            var success = MacBluetoothNative.ExecuteAction(address, isConnect);
            if (success)
            {
                return new BluetoothHandoffResult
                {
                    OperationId = command.OperationId,
                    Action = command.Action,
                    Success = true,
                    DeviceConnected = isConnect
                };
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Native IOBluetooth operation failed: {ex.Message}");
        }

        return new BluetoothHandoffResult
        {
            OperationId = command.OperationId,
            Action = command.Action,
            Success = false,
            ErrorCode = "action_failed",
            ErrorMessage = $"Unable to execute {command.Action} for device {address} on macOS"
        };
    }


    private async Task<bool> CheckPerDeviceControlSupportAsync()
    {
        if (_blueutilAvailable.HasValue)
            return _blueutilAvailable.Value;

        try
        {
            var (exitCode, _) = await RunCommandAsync("which", "blueutil");
            _blueutilAvailable = exitCode == 0;
        }
        catch
        {
            _blueutilAvailable = false;
        }

        // If blueutil is not installed, we can still use native IOBluetooth
        if (!_blueutilAvailable.Value)
            _blueutilAvailable = MacBluetoothNative.IsAvailable();

        return _blueutilAvailable.Value;
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(string fileName, string args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}

/// <summary>
/// Minimal Objective-C runtime P/Invoke for IOBluetoothDevice connection/disconnection.
/// </summary>
internal static class MacBluetoothNative
{
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
    private const string IOBluetoothFramework = "/System/Library/Frameworks/IOBluetooth.framework/IOBluetooth";

    [DllImport(ObjCRuntime, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(string className);

    [DllImport(ObjCRuntime, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(string selectorName);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_IntPtr_IntPtr(nint receiver, nint selector, nint arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern int objc_msgSend_int(nint receiver, nint selector);

    [DllImport("libSystem.dylib")]
    private static extern nint dlopen(string path, int mode);

    private static bool _frameworkLoaded;

    public static bool IsAvailable()
    {
        EnsureLoaded();
        return objc_getClass("IOBluetoothDevice") != 0;
    }

    private static void EnsureLoaded()
    {
        if (_frameworkLoaded) return;
        try
        {
            dlopen(IOBluetoothFramework, 2 /* RTLD_NOW */);
            _frameworkLoaded = true;
        }
        catch
        {
            // ignore
        }
    }

    public static bool ExecuteAction(string address, bool connect)
    {
        EnsureLoaded();
        var cls = objc_getClass("IOBluetoothDevice");
        if (cls == 0) return false;

        var nsStringCls = objc_getClass("NSString");
        var selUtf8 = sel_registerName("stringWithUTF8String:");
        var selDeviceWithAddr = sel_registerName("deviceWithAddressString:");

        var strPtr = Marshal.StringToHGlobalAnsi(address);
        try
        {
            var nsAddr = objc_msgSend_IntPtr_IntPtr(nsStringCls, selUtf8, strPtr);
            if (nsAddr == 0) return false;

            var device = objc_msgSend_IntPtr_IntPtr(cls, selDeviceWithAddr, nsAddr);
            if (device == 0) return false;

            var selAction = sel_registerName(connect ? "openConnection" : "closeConnection");
            var status = objc_msgSend_int(device, selAction);
            return status == 0; // kIOReturnSuccess = 0
        }
        finally
        {
            Marshal.FreeHGlobal(strPtr);
        }
    }
}
