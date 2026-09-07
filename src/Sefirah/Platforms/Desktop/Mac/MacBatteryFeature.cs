using System.Diagnostics;
using System.Text.RegularExpressions;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// macOS implementation of <see cref="IBatteryFeature"/> using `pmset -g batt`
/// to read power source, charge percentage, and charging state.
/// </summary>
public sealed partial class MacBatteryFeature(
    ILogger<MacBatteryFeature> logger,
    ISessionManager sessionManager) : IBatteryFeature, IDisposable
{
    private BatteryState? _lastBatteryState;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        _cts = new CancellationTokenSource();
        _ = PollBatteryLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public void SendBatteryStatus(PairedDevice device)
    {
        if (!device.IsConnected) return;

        var state = GetCurrentBatteryState();
        if (state is not null)
            device.SendMessage(state);
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        SendBatteryStatus(device);
    }

    private async Task PollBatteryLoopAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                var state = GetCurrentBatteryState();
                if (state is not null && HasChanged(state))
                {
                    _lastBatteryState = state;
                    sessionManager.BroadcastMessage(state);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal exit
        }
        catch (Exception ex)
        {
            logger.Warn($"Battery polling encountered an error: {ex.Message}");
        }
    }

    private bool HasChanged(BatteryState state)
    {
        return _lastBatteryState is null ||
               _lastBatteryState.BatteryLevel != state.BatteryLevel ||
               _lastBatteryState.IsCharging != state.IsCharging;
    }

    private BatteryState? GetCurrentBatteryState()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pmset",
                Arguments = "-g batt",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Match percentage: \b(\d+)%
            var matchPercent = PercentRegex().Match(output);
            if (!matchPercent.Success || !int.TryParse(matchPercent.Groups[1].Value, out var percent))
                return null;

            bool isCharging = output.Contains("charging", StringComparison.OrdinalIgnoreCase) ||
                             output.Contains("charged", StringComparison.OrdinalIgnoreCase) ||
                             output.Contains("AC Power", StringComparison.OrdinalIgnoreCase);

            return new BatteryState
            {
                BatteryLevel = percent,
                IsCharging = isCharging
            };
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to read macOS battery: {ex.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex PercentRegex();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }
}
