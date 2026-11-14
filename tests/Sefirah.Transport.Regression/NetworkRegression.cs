using System.Collections.Concurrent;

static class NetworkRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var a = new DeviceRegistry("00000000-0000-0000-0000-000000000001");
        var b = new DeviceRegistry("00000000-0000-0000-0000-000000000002");
        var aMessages = new Messages();
        var bMessages = new Messages();
        var host = new TestHost();
        var aNetwork = new NetworkService(new(() => aMessages), host, a, new NoAdb(), System.Net.IPAddress.Loopback, 0);
        var bNetwork = new NetworkService(new(() => bMessages), host, b, new NoAdb(), System.Net.IPAddress.Loopback, 0);
        aMessages.Network = aNetwork;
        bMessages.Network = bNetwork;
        await aNetwork.StartServerAsync();
        var aPort = NetworkService.ServerPort;
        await bNetwork.StartServerAsync();
        var bPort = NetworkService.ServerPort;
        var certificate = Sefirah.Helpers.SslHelper.GetOrCreateCertificate().RawData;
        var aPeer = new PairedDevice { Id = b.Local.DeviceId, Port = bPort, Certificate = certificate };
        var bPeer = new PairedDevice { Id = a.Local.DeviceId, Port = aPort, Certificate = certificate };
        a.PairedDevices.Add(aPeer);
        b.PairedDevices.Add(bPeer);
        for (var round = 0; round < 5; round++)
        {
            aPeer.ConnectionStatus = new Disconnected();
            bPeer.ConnectionStatus = new Disconnected();
            await Task.WhenAll(Task.Run(() => aNetwork.Connect(aPeer)), Task.Run(() => bNetwork.Connect(bPeer)));
            try { await WaitAsync(() => aPeer.IsConnected && bPeer.IsConnected); }
            catch
            {
                foreach (var item in host.Trace.Concat(host.Failures)) Console.Error.WriteLine(item);
                throw;
            }
            await Task.Delay(300);
            check(aPeer.IsConnected && bPeer.IsConnected, $"cross-connect stays connected round {round}");
            check((aPeer.Client is not null) != (aPeer.Session is not null), "one A transport");
            check((bPeer.Client is not null) != (bPeer.Session is not null), "one B transport");
            var content = string.Concat(Enumerable.Repeat("中文🙂", 10_000)) + round;
            aPeer.SendMessage(new ClipboardInfo { ClipboardType = "text/plain", Content = content });
            bPeer.SendMessage(new ClipboardInfo { ClipboardType = "text/plain", Content = content });
            await WaitAsync(() => aMessages.Clipboard.Contains(content) && bMessages.Clipboard.Contains(content));
            check(aMessages.Clipboard.Count(item => item == content) == 1 && bMessages.Clipboard.Count(item => item == content) == 1,
                "one intact Unicode message each direction");
            if (round == 0)
            {
                for (var index = 0; index < 2000; index++)
                    aPeer.SendMessage(new BatteryState { BatteryLevel = index % 101 });
                await WaitAsync(() => bMessages.Battery.Count == 2000);
                check(bMessages.Battery.SequenceEqual(Enumerable.Range(0, 2000).Select(index => index % 101)),
                    "backpressure preserves a 2000-message burst in order");
            }
            aNetwork.DisconnectDevice(aPeer, true);
            try { await WaitAsync(() => aPeer.IsForcedDisconnect && bPeer.IsForcedDisconnect); }
            catch
            {
                Console.Error.WriteLine($"Disconnect states A={aPeer.ConnectionStatus.GetType().Name}/{aPeer.IsForcedDisconnect}, B={bPeer.ConnectionStatus.GetType().Name}/{bPeer.IsForcedDisconnect}");
                foreach (var item in host.Trace.Concat(host.Failures)) Console.Error.WriteLine(item);
                throw;
            }
            check(aPeer.Client is null && aPeer.Session is null && bPeer.Client is null && bPeer.Session is null, "disconnect closes both endpoints");
            await Task.Delay(50);
        }
        check(!host.Failures.Any(message => message.Contains("Certificate") || message.Contains("session authentication")), "network authentication succeeds");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Network regression condition timed out");
            await Task.Delay(10);
        }
    }

    private sealed class DeviceRegistry(string id) : IDeviceManager
    {
        public TestLocalDevice Local { get; } = new(id, "Regression");
        public ObservableCollection<PairedDevice> PairedDevices { get; } = [];
        public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];
        public PairedDevice? ActiveDevice { get; set; }
        public Task<TestLocalDevice> GetLocalDeviceAsync() => Task.FromResult(Local);
        public Task UpdateDevice(PairedDevice device) => Task.CompletedTask;
        public Task<PairedDevice> AddDevice(DiscoveredDevice device) => throw new InvalidOperationException("Unexpected pairing");
    }
    private sealed class NoAdb : IAdbService { public Task TryConnectTcp(string address, string model) => Task.CompletedTask; }
    private sealed class Messages : IMessageHandler
    {
        public NetworkService Network { get; set; } = null!;
        public ConcurrentBag<string> Clipboard { get; } = [];
        public ConcurrentQueue<int> Battery { get; } = [];
        public async Task HandleMessageAsync(PairedDevice device, SocketMessage message)
        {
            if (message is BatteryState battery)
            {
                await Task.Delay(1);
                Battery.Enqueue(battery.BatteryLevel);
            }
            if (message is ClipboardInfo clipboard) Clipboard.Add(clipboard.Content);
            if (message is Disconnect) Network.DisconnectDevice(device, true, notifyPeer: false);
        }
    }
}
