using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.WinUI;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;

public class NetworkService(
    Lazy<IMessageHandler> messageHandler,
    ILogger<NetworkService> logger,
    IDeviceManager deviceManager,
    IAdbService adbService,
    IPAddress? listenAddress = null,
    int? listenPort = null) : INetworkService, ISessionManager, ITcpServerProvider, ITcpClientProvider
{
    public static int ServerPort { get; private set; }

    private Server? server;
    private bool isRunning;

    private static readonly IEnumerable<int> PORT_RANGE = Enumerable.Range(5150, 20); // 5150 to 5169

    private readonly ConcurrentDictionary<Guid, Utf8LineBuffer> connectionBuffers = [];
    private readonly ConcurrentDictionary<Guid, Channel<SocketMessage>> messageQueues = [];
    private readonly ConcurrentDictionary<Guid, Task> messageWorkers = [];
    private readonly HashSet<string> connectingDeviceIds = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> handshakeCompletion = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> connectionCancellationTokens = [];
    // Pinned client connections to paired devices that have not yet confirmed us
    private readonly ConcurrentDictionary<Guid, PairedDevice> awaitingServerAuthentication = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> authenticationCompletion = [];
    // Last session each paired client was sent an Authentication reply on
    private readonly Dictionary<string, (Guid SessionId, long Ticks)> authenticationReplies = [];
    private static readonly TimeSpan DuplicateConnectionWindow = TimeSpan.FromSeconds(3);

    // Detect a peer that slept or left the network instead of keeping a half-open link forever.
    private const int KeepAliveTimeSeconds = 30;
    private const int KeepAliveIntervalSeconds = 10;
    private const int KeepAliveRetryCount = 3;

    // Desktop peers only meet again through discovery, which runs once at startup. Phones keep
    // reconnecting on their own, so only computer peers are retried here. The side with the
    // smaller device ID retries on every tick and the other side only as a fallback, so both
    // ends rarely connect at once and close each other's link.
    private static readonly TimeSpan DesktopReconnectTick = TimeSpan.FromSeconds(20);
    private const int DesktopReconnectFallbackTicks = 4;
    private static readonly TimeSpan DesktopReconnectMaxBackoff = TimeSpan.FromMinutes(5);
    private int desktopReconnectTicks;
    private readonly ConcurrentDictionary<string, (int Failures, DateTime NextAttempt)> desktopReconnectBackoff = [];
    private CancellationTokenSource? desktopReconnectCts;


    private PairedDevice[] PairedDevices => pairedSnapshot;
    private volatile PairedDevice[] pairedSnapshot = [];
    private volatile DiscoveredDevice[] discoveredSnapshot = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> peerGates = [];
    private ObservableCollection<DiscoveredDevice> DiscoveredDevices => deviceManager.DiscoveredDevices;

    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    public event EventHandler<PairedDevice>? ConnectionStatusChanged;

    public async Task StartServerAsync()
    {
        if (isRunning) return;
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            pairedSnapshot = deviceManager.PairedDevices.ToArray();
            discoveredSnapshot = DiscoveredDevices.ToArray();
            DiscoveredDevices.CollectionChanged += (_, _) => discoveredSnapshot = DiscoveredDevices.ToArray();
            deviceManager.PairedDevices.CollectionChanged += (_, _) => pairedSnapshot = deviceManager.PairedDevices.ToArray();
        });

        ConnectionStatusChanged += ConnectionStatusChangedEvent;

        foreach (int port in listenPort.HasValue ? new[] { listenPort.Value } : PORT_RANGE)
        {
            try
            {
                server = new Server(SslHelper.GetSslContext(), listenAddress ?? IPAddress.IPv6Any, port, this)
                {
                    OptionReuseAddress = true,
                    OptionDualMode = true,
                    OptionKeepAlive = true,
                    OptionTcpKeepAliveTime = KeepAliveTimeSeconds,
                    OptionTcpKeepAliveInterval = KeepAliveIntervalSeconds,
                    OptionTcpKeepAliveRetryCount = KeepAliveRetryCount,
                };

                if (server.Start())
                {
                    ServerPort = ((IPEndPoint)server.Endpoint).Port;
                    isRunning = true;
                    logger.Info($"Server started on port: {ServerPort}");
                    StartDesktopReconnectLoop();
                    return;
                }
                server.Dispose();
                server = null;
            }
            catch (Exception ex)
            {
                logger.Error($"Error starting server on port {port}", ex);
                server?.Dispose();
                server = null;
            }
        }
        logger.Error($"Failed to start server");
    }

    private void StartDesktopReconnectLoop()
    {
        if (desktopReconnectCts is not null) return;
        desktopReconnectCts = new CancellationTokenSource();
        var token = desktopReconnectCts.Token;

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(DesktopReconnectTick);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var tick = Interlocked.Increment(ref desktopReconnectTicks);
                    await ReconnectDesktopPeersAsync(fallback: tick % DesktopReconnectFallbackTicks == 0);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        try
        {
            // Let the new address settle (DHCP, VPN routes) before dialing.
            await Task.Delay(TimeSpan.FromSeconds(5));
            desktopReconnectBackoff.Clear();
            await ReconnectDesktopPeersAsync(fallback: true);
        }
        catch (Exception ex)
        {
            logger.Debug("Desktop reconnect after network change failed", ex);
        }
    }

    private async Task ReconnectDesktopPeersAsync(bool fallback)
    {
        try
        {
            var localId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
            foreach (var device in PairedDevices.ToList())
            {
                if (!device.IsComputer || device.IsConnectedOrConnecting || device.IsForcedDisconnect)
                    continue;
                if (device.Certificate is not { Length: > 0 })
                    continue;

                var preferred = string.CompareOrdinal(localId, device.Id) < 0;
                if (!preferred && !fallback)
                    continue;

                // Back off for peers that stay unreachable (asleep, away) to keep logs and sockets quiet.
                var now = DateTime.UtcNow;
                var state = desktopReconnectBackoff.GetValueOrDefault(device.Id);
                if (now < state.NextAttempt)
                    continue;
                var delay = TimeSpan.FromTicks(Math.Min(
                    DesktopReconnectMaxBackoff.Ticks,
                    DesktopReconnectTick.Ticks << Math.Min(state.Failures, 4)));
                desktopReconnectBackoff[device.Id] = (state.Failures + 1, now + delay);

                var addresses = device.GetEnabledAddresses();
                if (!string.IsNullOrEmpty(device.Address) && addresses.Remove(device.Address))
                    addresses.Insert(0, device.Address);
                if (addresses.Count == 0)
                    continue;

                logger.Debug($"Reconnecting desktop peer {device.Name}");
                ConnectCore(device, addresses);
            }
        }
        catch (Exception ex)
        {
            logger.Debug("Desktop reconnect pass failed", ex);
        }
    }

    private async void ConnectionStatusChangedEvent(object? sender, PairedDevice device)
    {
        if (device.IsConnected)
        {
            desktopReconnectBackoff.TryRemove(device.Id, out _);
            await SendDeviceInfo(device);

            if (device.IsMobile && device.DeviceSettings.AdbAutoConnect)
            {
                await adbService.TryConnectTcp(device.Address, device.Model);
            }
        }
    }

    private async Task SendDeviceInfo(PairedDevice device)
    {
        try
        {
            await Ioc.Default.GetRequiredService<DevicePresentationService>().PublishAsync(device);
        }
        catch (Exception ex)
        {
            logger.Error($"Exception occurred while sending device info", ex);
        }
    }

    public void DisconnectDevice(PairedDevice device, bool forcedDisconnect = false, bool notifyPeer = true)
    {
        if (forcedDisconnect && notifyPeer)
        {
            var bytes = EncodeMessage(new Disconnect());
            try
            {
                if (device.Session?.IsHandshaked == true) device.Session.Send(bytes);
                else if (device.Client?.IsHandshaked == true) device.Client.Send(bytes);
            }
            catch (Exception ex) { logger.Debug("Could not send disconnect", ex); }
        }
        if (connectionCancellationTokens.TryGetValue(device.Id, out var attempt)) attempt.Cancel();
        var session = device.Session;
        var client = device.Client;
        if (session is not null) DisconnectSession(session, forcedDisconnect);
        if (client is not null) DisconnectClient(client, forcedDisconnect);
        if (session is null && client is null) SetDisconnected(device, forcedDisconnect);
    }

    public void SendMessage(ServerSession session, SocketMessage message)
    {
        try
        {
            var bytes = EncodeMessage(message);
            session.SendAsync(bytes);
        }
        catch (Exception ex)
        {
            logger.Error($"Error sending message", ex);
        }
    }

    public void SendMessage(Client client, SocketMessage message)
    {
        try
        {
            var bytes = EncodeMessage(message);
            client.SendAsync(bytes);
        }
        catch (Exception ex)
        {
            logger.Error($"Error sending message to client", ex);
        }
    }

    public void BroadcastMessage(SocketMessage message)
    {
        if (PairedDevices.Length == 0) return;

        foreach (var device in PairedDevices.Where(d => d.IsConnected))
        {
            device.SendMessage(message);
        }
    }

    private static byte[] EncodeMessage(SocketMessage message) =>
        Encoding.UTF8.GetBytes(JsonMessageSerializer.Serialize(message) + "\n");

    /// <summary>
    /// Extracts complete newline-delimited messages from the buffer, deserializes them, removes from buffer, returns list.
    /// </summary>
    private static List<SocketMessage> DecodeMessages(List<string> lines, ILogger logger)
    {
        List<SocketMessage> messages = [];
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonMessageSerializer.DeserializeMessage(line) is { } message) messages.Add(message);
            }
            catch (Exception ex) { logger.Warn("Skipping invalid message line", ex); }
        }
        return messages;
    }

    private async Task<Authentication> CreateAuthenticationMessageAsync()
    {
        var localDevice = await deviceManager.GetLocalDeviceAsync();

        return new Authentication
        {
            ProtocolVersion = 1,
            DeviceId = localDevice.DeviceId,
            DeviceName = localDevice.DeviceName,
            PublicKey = SslHelper.DevicePublicKeyString,
            Model = Environment.MachineName
        };
    }

    public async void SendAuthenticationMessage(Action<SocketMessage> send)
    {
        send(await CreateAuthenticationMessageAsync());
    }

    #region Server events
    public void OnConnected(ServerSession session) 
    {
        logger.Debug($"New session connected: {session.Id}");
        connectionBuffers[session.Id] = new Utf8LineBuffer();
        StartMessageQueue(session.Id, message => ProcessSessionMessageAsync(session, message), () => DisconnectSession(session));
        _ = ExpireUnauthenticatedSessionAsync(session);
    }

    public async void OnDisconnected(ServerSession session)
    {
        logger.Debug($"Session disconnected: {session.Id}");
        if (!connectionBuffers.ContainsKey(session.Id)) return;

        await DrainMessagesAsync(session.Id);
        DisconnectSession(session);
    }

    public void OnError(SocketError error)
    {
        logger.Error($"Error on socket {error}");
    }

    public void OnReceived(ServerSession session, byte[] buffer, long offset, long size) =>
        EnqueueReceived(session.Id, buffer, offset, size, () => DisconnectSession(session));

    private void StartMessageQueue(Guid id, Func<SocketMessage, Task> handle, Action disconnect)
    {
        var channel = Channel.CreateBounded<SocketMessage>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        messageQueues[id] = channel;
        messageWorkers[id] = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in channel.Reader.ReadAllAsync())
                {
                    if (!messageQueues.TryGetValue(id, out var current) || current != channel) break;
                    await handle(message);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Connection message processing failed", ex);
                disconnect();
            }
        });
    }

    private async Task DrainMessagesAsync(Guid id)
    {
        if (messageQueues.TryGetValue(id, out var queue)) queue.Writer.TryComplete();
        if (messageWorkers.TryGetValue(id, out var worker)) await worker;
    }

    private void EnqueueReceived(Guid id, byte[] buffer, long offset, long size, Action disconnect)
    {
        try
        {
            if (!connectionBuffers.TryGetValue(id, out var framing) || !messageQueues.TryGetValue(id, out var queue)) return;
            foreach (var message in DecodeMessages(framing.Append(buffer.AsSpan((int)offset, (int)size)), logger))
                queue.Writer.WriteAsync(message).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Warn("Invalid connection frame", ex);
            disconnect();
        }
    }

    private async Task ProcessSessionMessageAsync(ServerSession session, SocketMessage message)
    {
        if (message is Authentication authentication)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HandleServerSessionAuthentication(session, authentication, timeout.Token);
        }
        else await RouteMessageAsync(session.Id, message);
    }

    private async Task ProcessClientMessageAsync(Client client, SocketMessage message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (message is Authentication authentication)
            await HandleServerAuthentication(client, authentication, timeout.Token);
        else
        {
            if (awaitingServerAuthentication.TryGetValue(client.Id, out var legacy) &&
                client.Socket.RemoteEndPoint is IPEndPoint endpoint)
                await AuthenticatePairedDeviceServer(client, legacy, endpoint.Address.ToString(), timeout.Token);
            await RouteMessageAsync(client.Id, message);
        }
    }

    private async Task RouteMessageAsync(Guid id, SocketMessage message)
    {
        var pairedDevice = PairedDevices.FirstOrDefault(d => d.Client?.Id == id || d.Session?.Id == id);
        if (pairedDevice is not null && pairedDevice.IsConnected)
        {
            await messageHandler.Value.HandleMessageAsync(pairedDevice, message);
            return;
        }
        var discovered = await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            discoveredSnapshot.FirstOrDefault(d => d.Client?.Id == id || d.Session?.Id == id));
        if (discovered is not null && message is PairMessage pairing) HandlePairMessage(discovered, pairing);
    }

    private void HandlePairMessage(DiscoveredDevice device, PairMessage pairMessage)
    {
        if (device.IsPairing)
            HandlePairResponse(device, pairMessage);
        else if (pairMessage.Pair)
            HandlePairRequest(device);
    }

    #endregion

    #region Server Authentication

    private async Task HandleServerSessionAuthentication(ServerSession session, Authentication authMessage, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Socket.RemoteEndPoint is not IPEndPoint endPoint) return;

            var ip = endPoint.Address;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            var address = ip.ToString();
            logger.Info($"Received connection from {address}");

            // Bind Authentication to the certificate from this transport's TLS handshake.
            var cert = SslHelper.VerifyPeerCertificate(session.Handshake.PeerCertificate, authMessage.PublicKey);
            if (cert is null || cert.Length == 0)
            {
                logger.Warn($"No client certificate or PublicKey mismatch; rejecting connection");
                throw new Exception("Client certificate required");
            }


            var pairedDevice = PairedDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
            if (pairedDevice is not null)
            {
                if (pairedDevice.IsForcedDisconnect) throw new IOException("Peer is manually disconnected");
                if (pairedDevice.Certificate.Length == 0 || cert.Length != pairedDevice.Certificate.Length || !cert.AsSpan().SequenceEqual(pairedDevice.Certificate))
                {
                    throw new Exception("Certificate verification failed for paired device");
                }
                await AuthenticatePairedDeviceClient(session, pairedDevice, address, cancellationToken, authMessage.ProtocolVersion);
                return;
            }

            await AddDiscoveredDevice(session, authMessage, address, cert);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error($"Error in session authentication", ex);
            DisconnectSession(session);
        }
    }

    private async Task AuthenticatePairedDeviceClient(ServerSession session, PairedDevice pairedDevice, string address, CancellationToken cancellationToken, int protocolVersion)
    {
        var gate = peerGates.GetOrAdd(pairedDevice.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await AcceptPairedDeviceClientAsync(session, pairedDevice, address, cancellationToken, protocolVersion); }
        finally { gate.Release(); }
    }

    private async Task AcceptPairedDeviceClientAsync(ServerSession session, PairedDevice pairedDevice, string address, CancellationToken cancellationToken, int protocolVersion)
    {
        logger.Info($"Paired device {pairedDevice.Name} verified, updating connection");

        var localId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
        cancellationToken.ThrowIfCancellationRequested();
        if (pairedDevice.IsComputer && pairedDevice.Client?.IsHandshaked == true && string.CompareOrdinal(localId, pairedDevice.Id) < 0)
        {
            DisconnectSession(session);
            return;
        }
        if (pairedDevice.IsConnected && pairedDevice.Session is not null && pairedDevice.Session != session)
        {
            DisconnectSession(pairedDevice.Session);
        }

        cancellationToken.ThrowIfCancellationRequested();

        pairedDevice.Session = session;

        // Reply like Android servers do, so a connecting desktop can mark this server connected.
        // Sent before ConnectionStatusChanged so it precedes DeviceInfo on the wire.
        var authReply = await CreateAuthenticationMessageAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ReplyAuthentication(session, pairedDevice, authReply, protocolVersion);

        // Address and status are bound to the UI; WinUI throws RPC_E_WRONG_THREAD off the UI thread.
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsConnected || pairedDevice.Session != session) return;
            pairedDevice.Address = address;
            pairedDevice.TryAddAddress(address);
            pairedDevice.ConnectionStatus = new Connected();
            deviceManager.ActiveDevice = pairedDevice;
        });

        cancellationToken.ThrowIfCancellationRequested();

        ConnectionStatusChanged?.Invoke(this, pairedDevice);

        await deviceManager.UpdateDevice(pairedDevice);

        if (connectionCancellationTokens.TryRemove(pairedDevice.Id, out var cts))
        {
            cts.Cancel();
        }

        if (pairedDevice.Client is not null)
            DisconnectClient(pairedDevice.Client);
    }

    /// <summary>
    /// A desktop client briefly shows Disconnected after its handshake, so a discovery burst can make it
    /// connect twice before the first reply arrives. Clients up to 3.2.0 that accept one reply and then get
    /// another on their newer connection close that connection while staying Connected. Answer only the
    /// newest session of a burst, and only once: without a second reply they keep their newest connection.
    /// </summary>
    private void ReplyAuthentication(ServerSession session, PairedDevice pairedDevice, Authentication authReply, int protocolVersion)
    {
        lock (authenticationReplies)
        {
            if (pairedDevice.Session != session)
            {
                logger.Debug($"Not replying to superseded session {session.Id} from {pairedDevice.Name}");
                return;
            }

            if (protocolVersion < 1 && authenticationReplies.TryGetValue(pairedDevice.Id, out var lastReply)
                && lastReply.SessionId != session.Id
                && Environment.TickCount64 - lastReply.Ticks < DuplicateConnectionWindow.TotalMilliseconds)
            {
                logger.Info($"Not replying to duplicate connection from {pairedDevice.Name}; an earlier one was just answered");
                return;
            }

            SendMessage(session, authReply);
            authenticationReplies[pairedDevice.Id] = (session.Id, Environment.TickCount64);
        }
    }

    private async Task AddDiscoveredDevice(ServerSession session, Authentication authMessage, string address, byte[] certificate)
    {
        var existingDevice = discoveredSnapshot.FirstOrDefault(d => d.Id == authMessage.DeviceId);
        if (existingDevice is not null && existingDevice.Session is not null)
        {
            DisconnectSession(existingDevice.Session);
        }

        var device = new DiscoveredDevice
        {
            Id = authMessage.DeviceId,
            Name = authMessage.DeviceName,
            Model = authMessage.Model,
            Address = address,
            Certificate = certificate,
            VerificationKey = SslHelper.GetVerificationCode(authMessage.PublicKey),
            Session = session
        };

        await App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Add(device));

        SendAuthenticationMessage(m => SendMessage(session, m));
    }

    private readonly ConcurrentDictionary<string, (DiscoveredDevice Device, TaskCompletionSource<bool> Completion)> managementPairings = new();

    public (string DeviceId, string VerificationCode)[] PendingManagementPairings =>
        managementPairings.Values.Select(p => (p.Device.Id, p.Device.VerificationKey)).ToArray();

    public bool ResolveManagementPairing(string id, string code, bool accept)
    {
        return managementPairings.TryGetValue(id, out var pending)
            && discoveredSnapshot.Contains(pending.Device)
            && pending.Device.VerificationKey == code
            && pending.Completion.TrySetResult(accept);
    }

    private async void HandlePairRequest(DiscoveredDevice device)
    {
        logger.Info($"Received pairing request from {device.Name}");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!managementPairings.TryAdd(device.Id, (device, tcs))) return;
        using var expiry = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var timeoutRegistration = expiry.Token.Register(() => tcs.TrySetResult(false));
        await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            try
            {
                var frame = (Frame)App.MainWindow.Content!;
                var dialog = new ConnectionRequestDialog(device.Name, device.VerificationKey, frame)
                {
                    XamlRoot = App.MainWindow.Content!.XamlRoot
                };

                if (tcs.Task.IsCompleted) return;
                var showing = dialog.ShowAsync().AsTask();
                if (await Task.WhenAny(showing, tcs.Task) == showing)
                    tcs.TrySetResult(await showing is ContentDialogResult.Primary);
                else dialog.Hide();
            }
            catch (Exception ex)
            {
                logger.Error($"Error showing connection request dialog", ex);
                tcs.TrySetResult(false);
            }
        });

        var accepted = await tcs.Task;
        managementPairings.TryRemove(device.Id, out _);
        if (!discoveredSnapshot.Contains(device)) return;

        device.SendMessage(new PairMessage { Pair = accepted });
        if (accepted)
        {
            var pairedDevice = await deviceManager.AddDevice(device);
            ConnectionStatusChanged?.Invoke(this, pairedDevice);
        }
    }

    private async void HandlePairResponse(DiscoveredDevice device, PairMessage pairMessage)
    {
        if (!pairMessage.Pair)
        {
            logger.Info($"Device {device.Name} rejected pairing request");
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.IsPairing = false);
            return;
        }

        logger.Info($"Device {device.Name} accepted pairing request");
        var pairedDevice = await deviceManager.AddDevice(device);
        ConnectionStatusChanged?.Invoke(this, pairedDevice);
    }
    #endregion

    public void DisconnectSession(ServerSession session, bool forcedDisconnect = false)
    {
        if (!session.TryBeginClose()) return;
        logger.Debug($"disconnecing session: {session.Id}");
        try
        {
            connectionBuffers.TryRemove(session.Id, out _);
            messageWorkers.TryRemove(session.Id, out _);
            if (messageQueues.TryRemove(session.Id, out var messages)) messages.Writer.TryComplete();
            session.Disconnect();
            session.Dispose();
            
            var pairedDevice = PairedDevices.FirstOrDefault(d => d.Session == session);   
            if (pairedDevice is not null)
            {
                pairedDevice.Session = null;
                if (pairedDevice.Client is null)
                    SetDisconnected(pairedDevice, forcedDisconnect);
            }
            else
            {
                var discoveredDevice = discoveredSnapshot.FirstOrDefault(d => d.Session == session);
                if (discoveredDevice is not null)
                {
                    App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Remove(discoveredDevice));
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error in Disconnecting", ex);
        }
    }

    public void DisconnectClient(Client client, bool forcedDisconnect = false)
    {
        if (!client.TryBeginClose()) return;
        try
        {
            logger.Debug($"disconnecing client session: {client.Id}");
            connectionBuffers.TryRemove(client.Id, out _);
            messageWorkers.TryRemove(client.Id, out _);
            awaitingServerAuthentication.TryRemove(client.Id, out _);
            if (authenticationCompletion.TryRemove(client.Id, out var authPending))
                authPending.TrySetException(new IOException("Disconnected before authentication completed"));
            if (messageQueues.TryRemove(client.Id, out var messages)) messages.Writer.TryComplete();

            client.Disconnect();
            client.Dispose();

            var device = PairedDevices.FirstOrDefault(d => d.Client == client);
            if (device is not null)
            {
                device.Client = null;
                if (device.Session is null)
                    SetDisconnected(device, forcedDisconnect);
            }

            var discoveredDevice = discoveredSnapshot.FirstOrDefault(d => d.Client == client);
            if (discoveredDevice is not null)
            {
                App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Remove(discoveredDevice));
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error disconnecting client", ex);
        }
    }

    private async void SetDisconnected(PairedDevice device, bool forcedDisconnect)
    {
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                device.ConnectionStatus = new Disconnected(forcedDisconnect));
            ConnectionStatusChanged?.Invoke(this, device);
        }
        catch (Exception ex)
        {
            logger.Error("Error updating disconnected status", ex);
        }
    }

    #region Client

    public async void Connect(string deviceId, string address, int port)
    {
        var existingDevice = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
        if (existingDevice is not null)
        {
            if (existingDevice.IsConnectedOrConnecting || existingDevice.IsForcedDisconnect)
                return;

            existingDevice.Port = port;
            ConnectCore(existingDevice, [address]);
            return;
        }

        if (discoveredSnapshot.Any(d => d.Id == deviceId)) return;

        lock (connectingDeviceIds)
        {
            if (connectingDeviceIds.Contains(deviceId)) return;
            connectingDeviceIds.Add(deviceId);
        }

        Client? client = null;
        try
        {
            logger.Info($"Connecting to {address}:{port}");

            client = new Client(SslHelper.GetSslContext(), address, port, this);
            var tcs = new TaskCompletionSource<bool>();
            handshakeCompletion[client.Id] = tcs;

            try
            {
                if (client.ConnectAsync())
                {
                    if (!client.IsHandshaked)
                        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    SendAuthenticationMessage(m => SendMessage(client, m));
                    _ = ExpireUnpairedClientAsync(client);
                }
                else DisconnectClient(client);
            }
            catch (Exception ex)
            {
                logger.Warn($"Error connecting to {address}:{port}", ex);
                DisconnectClient(client);
            }
        }
        finally
        {
            if (client is not null)
                handshakeCompletion.TryRemove(client.Id, out _);

            lock (connectingDeviceIds)
            {
                connectingDeviceIds.Remove(deviceId);
            }
        }
    }

    private async Task ExpireUnauthenticatedSessionAsync(ServerSession session)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (!PairedDevices.Any(device => device.Session == session) && !discoveredSnapshot.Any(device => device.Session == session))
            DisconnectSession(session);
    }

    private async Task ExpireUnpairedClientAsync(Client client)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (!PairedDevices.Any(d => d.Client == client)) DisconnectClient(client);
    }

    public void Connect(PairedDevice device)
    {
        if (connectionCancellationTokens.TryRemove(device.Id, out var removedCts))
        {
            removedCts.Cancel();
            if (device.IsConnecting)
                App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.ConnectionStatus = new Disconnected());
            return;
        }

        ConnectCore(device, device.GetEnabledAddresses());
    }

    public void Connect(PairedDevice device, string address)
    {
        if (connectionCancellationTokens.TryRemove(device.Id, out var removedCts))
        {
            removedCts.Cancel();
        }
        ConnectCore(device, [address]);
    }

    private async void ConnectCore(PairedDevice device, IReadOnlyList<string> addresses)
    {
        lock (connectingDeviceIds)
        {
            if (connectingDeviceIds.Contains(device.Id)) return;
            connectingDeviceIds.Add(device.Id);
        }

        logger.Info($"Connecting to paired device {device.Name}");

        var cts = new CancellationTokenSource();
        connectionCancellationTokens[device.Id] = cts;
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.ConnectionStatus = new Connecting());
        }
        catch
        {
            lock (connectingDeviceIds) connectingDeviceIds.Remove(device.Id);
            connectionCancellationTokens.TryRemove(device.Id, out _);
            cts.Dispose();
            throw;
        }

        try
        {
            foreach (var address in addresses)
            {
                cts.Token.ThrowIfCancellationRequested();

                var clientContext = SslHelper.CreateSslContext(device.Certificate);

                logger.Info($"Connecting to {address}:{device.Port}");
                var client = new Client(clientContext, address, device.Port, this)
                {
                    OptionKeepAlive = true,
                    OptionTcpKeepAliveTime = KeepAliveTimeSeconds,
                    OptionTcpKeepAliveInterval = KeepAliveIntervalSeconds,
                    OptionTcpKeepAliveRetryCount = KeepAliveRetryCount,
                };
                var tcs = new TaskCompletionSource<bool>();
                handshakeCompletion[client.Id] = tcs;

                try
                {
                    if (!client.ConnectAsync())
                    {
                        handshakeCompletion.TryRemove(client.Id, out _);
                        continue;
                    }

                    if (!client.IsHandshaked)
                    {
                        using (cts.Token.Register(() => tcs.TrySetCanceled()))
                            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                    }

                    cts.Token.ThrowIfCancellationRequested();

                    var authentication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    authenticationCompletion[client.Id] = authentication;
                    var gate = peerGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var localId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
                        if (device.IsComputer && device.Session?.IsHandshaked == true && string.CompareOrdinal(localId, device.Id) > 0)
                            throw new IOException("The preferred inbound connection is already active");
                        var oldClient = device.Client;
                        device.Client = client;
                        if (oldClient is not null && oldClient != client) DisconnectClient(oldClient);
                        awaitingServerAuthentication[client.Id] = device;
                    }
                    finally { gate.Release(); }
                    SendAuthenticationMessage(m => SendMessage(client, m));
                    await authentication.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Debug($"Failed to connect to {address}:{device.Port}", ex);
                }
                finally
                {
                    handshakeCompletion.TryRemove(client.Id, out _);
                    authenticationCompletion.TryRemove(client.Id, out _);
                    if (!device.IsConnected || device.Client != client) DisconnectClient(client);
                }
            }
            logger.Warn($"Failed to connect to device {device.Name} on any IP address/port combination");
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Connection attempt cancelled for device {device.Name}");
        }
        finally
        {
            lock (connectingDeviceIds)
            {
                connectingDeviceIds.Remove(device.Id);
            }

            if (device.IsConnecting)
            {
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.ConnectionStatus = new Disconnected());
            }

            connectionCancellationTokens.TryRemove(KeyValuePair.Create(device.Id, cts));
            cts.Dispose();
        }
    }

    public void Pair(DiscoveredDevice device)
    {
        device.SendMessage(new PairMessage { Pair = true });
        App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.IsPairing = true);
    }

    #region Client events
    public void OnConnected(Client client)
    {
        logger.Info($"OnConnected for client {client.Id}");
        connectionBuffers[client.Id] = new Utf8LineBuffer();
        StartMessageQueue(client.Id, message => ProcessClientMessageAsync(client, message), () => DisconnectClient(client));
    }

    public void OnHandshaked(Client client)
    {
        if (handshakeCompletion.TryGetValue(client.Id, out var tcs))
            tcs.TrySetResult(true);
    }

    public async void OnDisconnected(Client client)
    {
        if (handshakeCompletion.TryRemove(client.Id, out var tcs))
            tcs.TrySetException(new IOException("Disconnected before TLS handshake completed"));

        if (!connectionBuffers.ContainsKey(client.Id)) return;
        
        await DrainMessagesAsync(client.Id);
        DisconnectClient(client);
    }

    public void OnError(Client client, SocketError error)
    {
        if (handshakeCompletion.TryRemove(client.Id, out var tcs))
            tcs.TrySetException(new IOException($"Socket error before TLS handshake completed: {error}"));
        logger.Error($"Error on client socket {error}");
        DisconnectClient(client);
    }

    public void OnReceived(Client client, byte[] buffer, long offset, long size) =>
        EnqueueReceived(client.Id, buffer, offset, size, () => DisconnectClient(client));
    #endregion

    #region Client Authentication

    private async Task HandleServerAuthentication(Client client, Authentication authMessage, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IPEndPoint? endPoint = client.Socket.RemoteEndPoint as IPEndPoint;
            var address = endPoint?.Address.ToString();

            if (string.IsNullOrEmpty(address)) return;

            logger.Info($"Received AuthenticationMessage from server at {address}");

            var certificate = SslHelper.VerifyPeerCertificate(client.Handshake.PeerCertificate, authMessage.PublicKey);
            if (awaitingServerAuthentication.TryGetValue(client.Id, out var intended) && intended.Id != authMessage.DeviceId)
                throw new System.Security.Authentication.AuthenticationException("Unexpected device ID on pinned connection");
            var pairedDevice = PairedDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
            if (pairedDevice is not null)
            {
                await AuthenticatePairedDeviceServer(client, pairedDevice, address, cancellationToken);
            }
            else
            {
                // Keep this transport's TLS certificate with the pending pairing.
                if (certificate is null || certificate.Length == 0)
                {
                    throw new Exception("No server certificate or PublicKey mismatch; rejecting");
                }
                await AddDiscoveredDevice(client, authMessage, address, endPoint?.Port ?? 5150, certificate);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error($"Error in client authentication", ex);
            DisconnectClient(client);
        }
    }

    private async Task AuthenticatePairedDeviceServer(Client client, PairedDevice pairedDevice, string address, CancellationToken cancellationToken)
    {
        var gate = peerGates.GetOrAdd(pairedDevice.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await AcceptPairedDeviceServerAsync(client, pairedDevice, address, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task AcceptPairedDeviceServerAsync(Client client, PairedDevice pairedDevice, string address, CancellationToken cancellationToken)
    {
        var localId = (await deviceManager.GetLocalDeviceAsync()).DeviceId;
        cancellationToken.ThrowIfCancellationRequested();
        if (pairedDevice.Client != client) return;
        if (pairedDevice.IsComputer && pairedDevice.Session?.IsHandshaked == true && string.CompareOrdinal(localId, pairedDevice.Id) > 0)
        {
            DisconnectClient(client);
            return;
        }
        // Already accepted on this client (e.g. legacy acceptance followed by a late Authentication);
        // disconnecting pairedDevice.Client here would close this very connection.
        if (pairedDevice.IsConnected && pairedDevice.Client == client && pairedDevice.Session is null)
        {
            awaitingServerAuthentication.TryRemove(client.Id, out _);
            if (authenticationCompletion.TryGetValue(client.Id, out var existing)) existing.TrySetResult(true);
            return;
        }

        if (pairedDevice.Client is not null && pairedDevice.Client != client)
        {
            logger.Warn($"Device {pairedDevice.Name} is already connected, disconnect the current client");
            DisconnectClient(pairedDevice.Client);
        }

        cancellationToken.ThrowIfCancellationRequested();

        pairedDevice.Client = client;
        pairedDevice.Port = client.Port;

        // Address and status are bound to the UI; WinUI throws RPC_E_WRONG_THREAD off the UI thread.
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.IsConnected || pairedDevice.Client != client) return;
            pairedDevice.Address = address;
            pairedDevice.TryAddAddress(address);
            pairedDevice.ConnectionStatus = new Connected();
            deviceManager.ActiveDevice = pairedDevice;
        });

        cancellationToken.ThrowIfCancellationRequested();

        ConnectionStatusChanged?.Invoke(this, pairedDevice);

        if (pairedDevice.Session is not null)
            DisconnectSession(pairedDevice.Session);

        await deviceManager.UpdateDevice(pairedDevice);

        awaitingServerAuthentication.TryRemove(client.Id, out _);
        if (authenticationCompletion.TryGetValue(client.Id, out var accepted)) accepted.TrySetResult(true);
        logger.Info($"Paired device {pairedDevice.Name} connected successfully");
    }

    private async Task AddDiscoveredDevice(Client client, Authentication authMessage, string address, int port, byte[] certificate)
    {
        var verificationKey = SslHelper.GetVerificationCode(authMessage.PublicKey);

        var existingDevice = discoveredSnapshot.FirstOrDefault(d => d.Id == authMessage.DeviceId);
        if (existingDevice is not null && existingDevice.Session is not null)
        {
            DisconnectSession(existingDevice.Session);
        }

        var device = new DiscoveredDevice
        {
            Id = authMessage.DeviceId,
            Name = authMessage.DeviceName,
            Model = authMessage.Model,
            Address = address,
            Port = port,
            Certificate = certificate,
            VerificationKey = verificationKey,
            Client = client
        };

        await App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Add(device));
    }
    #endregion

    #endregion
}
