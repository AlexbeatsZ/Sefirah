using System.Diagnostics;
using Sefirah.Data.Models;
using Sefirah.Services;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="ILocalBluetoothController"/> using system_profiler
/// for catalog discovery and blueutil / IOBluetooth for connection management.
/// </summary>
public sealed class MacBluetoothController(ILogger<MacBluetoothController> logger) : ILocalBluetoothController
{
    private static readonly string[] BlueutilCandidates =
    [
        "/opt/homebrew/bin/blueutil",
        "/usr/local/bin/blueutil",
    ];

    private static string? _blueutilPath;
    private static bool _blueutilPathChecked;

    public async Task<BluetoothDeviceCatalog> GetCatalogAsync(string requestId)
    {
        try
        {
            var (exitCode, jsonOutput) = await RunCommandAsync(
                "/usr/sbin/system_profiler",
                "SPBluetoothDataType",
                "-json");
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

            var snapshot = MacBluetoothCatalogParser.Parse(jsonOutput);
            if (!snapshot.ControllerAvailable)
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

            var catalog = new BluetoothDeviceCatalog
            {
                RequestId = requestId,
                ControllerAvailable = true,
                RadioEnabled = snapshot.RadioEnabled,
                SupportsPerDeviceControl = CheckPerDeviceControlSupport()
            };
            catalog.Devices.AddRange(snapshot.Devices.Select(device => new BluetoothCatalogDevice
            {
                DeviceKey = device.DeviceKey,
                DisplayName = device.DisplayName,
                BluetoothAddress = device.BluetoothAddress,
                IsConnected = device.IsConnected,
                IsHeadset = device.IsHeadset,
            }));

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

    public async Task<BluetoothHandoffResult> ExecuteAsync(BluetoothHandoffCommand command)
    {
        logger.Info($"Executing macOS Bluetooth command {command.Action} for device {command.DeviceKey} (OpId: {command.OperationId})");

        if (string.Equals(command.Action, "setRadio", StringComparison.OrdinalIgnoreCase))
        {
            var radioUtilityPath = ResolveBlueutilPath();
            if (radioUtilityPath is null)
            {
                return Failed(
                    command,
                    "radio_control_unavailable",
                    "Changing the macOS Bluetooth radio requires blueutil; per-device switching remains available.");
            }

            var powerArg = command.Enabled == true ? "1" : "0";
            var (exitCode, output) = await RunCommandAsync(radioUtilityPath, "--power", powerArg);
            var success = exitCode == 0;
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

        var isConnect = string.Equals(command.Action, "connect", StringComparison.OrdinalIgnoreCase);
        var isDisconnect = string.Equals(command.Action, "disconnect", StringComparison.OrdinalIgnoreCase);
        if (!isConnect && !isDisconnect)
        {
            return Failed(
                command,
                "unsupported_action",
                $"Unsupported macOS Bluetooth action: {command.Action}");
        }

        var address = command.DeviceKey;
        if (string.IsNullOrWhiteSpace(address))
        {
            return Failed(command, "missing_device_key");
        }

        if (ResolveBlueutilPath() is { } blueutilPath)
        {
            var actionArg = isConnect ? "--connect" : "--disconnect";
            try
            {
                var (exitCode, output) = await RunCommandAsync(blueutilPath, actionArg, address);
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
            catch (Exception ex)
            {
                logger.Warn($"blueutil could not be started; falling back to IOBluetooth: {ex.Message}");
            }
        }

        var bundledHelperPath = Path.Combine(AppContext.BaseDirectory, "sefirah-bluetooth");
        if (File.Exists(bundledHelperPath))
        {
            var actionArg = isConnect ? "--connect" : "--disconnect";
            var (exitCode, output) = await RunCommandAsync(
                bundledHelperPath,
                TimeSpan.FromSeconds(15),
                actionArg,
                address);
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

            logger.Warn($"Bundled IOBluetooth helper failed with exit code {exitCode}: {output}");
        }

        return Failed(
            command,
            "action_failed",
            $"Unable to execute {command.Action} for device {address} on macOS");
    }

    private static bool CheckPerDeviceControlSupport()
    {
        return ResolveBlueutilPath() is not null ||
               File.Exists(Path.Combine(AppContext.BaseDirectory, "sefirah-bluetooth"));
    }

    private static string? ResolveBlueutilPath()
    {
        if (_blueutilPathChecked) return _blueutilPath;
        _blueutilPathChecked = true;

        var pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "blueutil"));
        _blueutilPath = BlueutilCandidates
            .Concat(pathCandidates)
            .FirstOrDefault(File.Exists);
        return _blueutilPath;
    }

    private static BluetoothHandoffResult Failed(
        BluetoothHandoffCommand command,
        string errorCode,
        string? errorMessage = null) => new()
    {
        OperationId = command.OperationId,
        Action = command.Action,
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage,
    };

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string fileName,
        params string[] arguments) =>
        await RunCommandAsync(fileName, Timeout.InfiniteTimeSpan, arguments);

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string fileName,
        TimeSpan timeout,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeoutSource = timeout == Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource()
                : new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill attempt.
            }
            await process.WaitForExitAsync();
            await standardOutput;
            await standardError;
            return (-1, $"Command timed out after {timeout.TotalSeconds:0} seconds");
        }

        var output = await standardOutput;
        var error = await standardError;
        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }
}
