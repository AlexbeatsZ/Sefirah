using Sefirah.Data.Models;
using Sefirah.Services;

var checks = 0;
void Check(bool result, string name)
{
    if (!result) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
    checks++;
}

Check(AdbDeviceIdentity.Matches("old-id", "old-id", "usb-serial", []), "matching app identity associates USB");
Check(!AdbDeviceIdentity.Matches("old-id", "new-id", "192.168.31.157:5555", ["192.168.31.157"]), "new app identity overrides old IP association");
Check(!AdbDeviceIdentity.Matches("old-id", "", "usb-serial", ["192.168.31.157"]), "reflashed USB device cannot inherit an old pairing");
Check(AdbDeviceIdentity.Matches("old-id", "", "192.168.31.157:5555", ["192.168.31.157"]), "legacy wireless endpoint still associates");
Check(!AdbDeviceIdentity.Matches("old-id", "", "192.168.31.118:5555", ["192.168.31.157"]), "unrelated wireless endpoint remains separate");
Check(!AdbDeviceIdentity.Matches("old-id", "", "adb-usb._adb-tls-connect._tcp", ["192.168.31.157"]), "mDNS serial cannot act as a USB pairing ID");
Check(AdbDeviceIdentity.ResolveAuthenticatedId("192.168.31.118", [("live-id", "192.168.31.118")]) == "live-id", "current authenticated endpoint identifies file-inaccessible USB device");
Check(AdbDeviceIdentity.ResolveAuthenticatedId("192.168.31.118", [("old-id", "192.168.31.157")]) == "", "stale peer address cannot identify current USB device");
Check(AdbDeviceIdentity.ResolveAuthenticatedId(null, [("old-id", "192.168.31.157")]) == "", "missing USB address cannot identify a network peer");
Check(AdbDeviceIdentity.ResolveAuthenticatedId("192.168.31.118", [("a", "192.168.31.118"), ("b", "192.168.31.118")]) == "", "ambiguous authenticated endpoint does not identify a USB device");
Check(WirelessAdbSetup.GetWifiAddress("inet 169.254.1.2/16") is null, "link-local Wi-Fi address is skipped");
Check(WirelessAdbSetup.GetWifiAddress("inet 127.0.0.1/8") is null, "loopback address is skipped");
Check(WirelessAdbSetup.GetWifiAddress("inet 999.1.1.1/24") is null, "malformed address is skipped");

var noWifi = new Scenario { Wifi = "" };
Check(await noWifi.Run() is null && noWifi.Connects.Count == 0 && noWifi.Enables == 0, "no Wi-Fi skips network attempts and TCP restart");

var existing = new Scenario { ConnectResult = true };
Check(await existing.Run() == "192.168.31.118:5555" && existing.Enables == 0, "current USB Wi-Fi address connects without restart");
Check(existing.Connects.All(endpoint => endpoint == "192.168.31.118:5555"), "saved stale address is never used for USB setup");

var refused = new Scenario();
await refused.Run();
await refused.Run();
Check(refused.Enables == 0 && refused.Connects.Count == 2, "refused enabled listener never restarts adbd across rediscovery");

var customPort = new Scenario { ServicePort = "5577", ConnectResult = true };
Check(await customPort.Run() == "192.168.31.118:5577" && customPort.Enables == 0, "existing non-default TCP port is reused");

var persistent = new Scenario { ServicePort = "", PersistentPort = "5566", ConnectResult = true };
Check(await persistent.Run() == "192.168.31.118:5566" && persistent.Enables == 0, "persistent TCP port is reused");

var available = new Scenario { ServicePort = "0", ConnectResult = true };
Check(await available.Run() is not null && available.Enables == 0, "working endpoint avoids unnecessary mode command");

var disabled = new Scenario { ServicePort = "-1", ConnectAfterEnable = true };
Check(await disabled.Run() == "192.168.31.118:5555" && disabled.Enables == 1, "disabled TCP mode enables once and connects");
Check(await disabled.Run() is not null && disabled.Enables == 1, "online event after mode change reuses listener");

var clock = new TestClock();
var failed = new Scenario(new WirelessAdbSetup(clock)) { ServicePort = "0", EnableResult = false };
await failed.Run();
await failed.Run();
Check(failed.Enables == 1, "failed mode command is throttled across repeated device events");
clock.Advance(TimeSpan.FromMinutes(1));
await failed.Run();
Check(failed.Enables == 2, "recoverable mode command can retry after cooldown");
failed.BootId = "new-boot";
await failed.Run();
Check(failed.Enables == 3, "actual reboot permits a fresh enable attempt");

var inFlight = new Scenario { ServicePort = "0", ConnectAfterEnable = true };
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
inFlight.BeforeRead = async () => { entered.TrySetResult(); await release.Task; };
var first = inFlight.Run();
await entered.Task;
Check(await inFlight.Run() is null, "concurrent device event does not duplicate setup");
release.SetResult();
Check(await first is not null && inFlight.Enables == 1, "single owner completes mode setup");

var reentrant = new Scenario { ServicePort = "0", ConnectAfterEnable = true };
reentrant.OnEnable = async () => Check(await reentrant.Run() is null, "restart-generated online event cannot reenter setup");
Check(await reentrant.Run() is not null && reentrant.Enables == 1, "restart event produces exactly one mode command");

var interrupted = new Scenario { BeforeRead = () => throw new IOException("USB disconnected") };
try { await interrupted.Run(); throw new InvalidOperationException("Expected disconnect"); }
catch (IOException) { }
interrupted.BeforeRead = null;
interrupted.ConnectResult = true;
Check(await interrupted.Run() is not null, "USB failure releases setup lock for recovery");

var unreachable = new Scenario { ServicePort = "0" };
Check(await unreachable.Run() is null && unreachable.Enables == 1 && unreachable.Connects.Count == 4, "failed post-enable connection has bounded retries");
await unreachable.Run();
Check(unreachable.Enables == 1, "continued connection failure keeps enabled adbd running");

Console.WriteLine($"{checks} ADB regression checks passed");

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

sealed class Scenario(WirelessAdbSetup? setup = null)
{
    private readonly WirelessAdbSetup coordinator = setup ?? new();
    public string Wifi = "19: wlan0 inet 192.168.31.118/24 scope global wlan0";
    public string ServicePort = "5555";
    public string PersistentPort = "";
    public string BootId = "boot-a";
    public bool ConnectResult;
    public bool ConnectAfterEnable;
    public bool EnableResult = true;
    public int Enables;
    public List<string> Connects = [];
    public Func<Task>? BeforeRead;
    public Func<Task>? OnEnable;

    public Task<string?> Run() => coordinator.ConnectAsync("tablet-serial", ReadShell, Connect, Enable, _ => { });

    private async Task<string> ReadShell(string command)
    {
        if (BeforeRead is not null) await BeforeRead();
        return command switch
        {
            "ip -o -4 addr show wlan0" => Wifi,
            "getprop service.adb.tcp.port" => ServicePort,
            "getprop persist.adb.tcp.port" => PersistentPort,
            "cat /proc/sys/kernel/random/boot_id" => BootId,
            _ => throw new InvalidOperationException(command)
        };
    }

    private Task<bool> Connect(string host, int port)
    {
        Connects.Add($"{host}:{port}");
        return Task.FromResult(ConnectResult || (ConnectAfterEnable && Enables > 0));
    }

    private async Task<bool> Enable()
    {
        Enables++;
        if (OnEnable is not null) await OnEnable();
        if (EnableResult) ServicePort = "5555";
        return EnableResult;
    }
}
