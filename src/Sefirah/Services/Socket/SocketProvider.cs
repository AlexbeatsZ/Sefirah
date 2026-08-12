using NetCoreServer;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using UdpClient = NetCoreServer.UdpClient;

namespace Sefirah.Services.Socket;

internal sealed class SerializedSocketReceiver
{
    internal const int DefaultCapacity = 64;

    private readonly Channel<byte[]> queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(DefaultCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Action<byte[]> dispatch;
    private int stopped;

    public SerializedSocketReceiver(Action<byte[]> dispatch)
    {
        this.dispatch = dispatch;
        _ = Task.Run(ProcessAsync);
    }

    public bool TryDispatch(byte[] buffer) =>
        Volatile.Read(ref stopped) == 0 && queue.Writer.TryWrite(buffer);

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        queue.Writer.TryComplete();
        cancellationTokenSource.Cancel();
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var buffer in queue.Reader.ReadAllAsync(cancellationTokenSource.Token))
                dispatch(buffer);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public partial class ServerSession : SslSession
{
    private const int TransportSendBufferLimit =
        (int)(SerializedSocketSender.DefaultMaxFrameBytes + SerializedSocketSender.DefaultMaxControlFrameBytes);
    private readonly ITcpServerProvider socketProvider;
    private readonly SerializedSocketReceiver receiver;
    private readonly SerializedSocketSender sender;

    public ServerSession(SslServer server, ITcpServerProvider socketProvider) : base(server)
    {
        OptionSendBufferLimit = TransportSendBufferLimit;
        this.socketProvider = socketProvider;
        receiver = new SerializedSocketReceiver(
            buffer => socketProvider.OnReceived(this, buffer, 0, buffer.LongLength));
        sender = new SerializedSocketSender(
            buffer => SendAsync(buffer),
            () => IsConnected,
            () => BytesPending,
            () => BytesSending);
    }

    public bool SendApplicationAsync(byte[] buffer) => sender.TrySendApplication(buffer);

    public bool TrySendControl(byte[] buffer) => sender.TrySendControl(buffer);

    protected override void OnDisconnected()
    {
        sender.Stop();
        receiver.Stop();
        socketProvider.OnDisconnected(this);
    }

    protected override void OnConnected()
    {
        socketProvider.OnConnected(this);
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        if (receiver.TryDispatch(buffer.AsSpan((int)offset, (int)size).ToArray()))
            return;

        sender.Stop();
        receiver.Stop();
        socketProvider.OnError(this, SocketError.NoBufferSpaceAvailable);
        Disconnect();
    }

    protected override void OnError(SocketError error)
    {
        sender.Stop();
        receiver.Stop();
        socketProvider.OnError(this, error);
    }
}

public partial class Server(SslContext context, IPAddress address, int port, ITcpServerProvider socketProvider) : SslServer(context, address, port)
{
    protected override SslSession CreateSession()
    {
        return new ServerSession(this, socketProvider);
    }

    protected override void OnError(SocketError error)
    {
        socketProvider.OnServerError(error);
    }
}

public partial class Client : SslClient
{
    private const int TransportSendBufferLimit =
        (int)(SerializedSocketSender.DefaultMaxFrameBytes + SerializedSocketSender.DefaultMaxControlFrameBytes);
    private readonly ITcpClientProvider socketProvider;
    private readonly SerializedSocketReceiver receiver;
    private readonly SerializedSocketSender sender;

    public Client(SslContext context, string address, int port, ITcpClientProvider socketProvider) : base(context, address, port)
    {
        OptionSendBufferLimit = TransportSendBufferLimit;
        this.socketProvider = socketProvider;
        receiver = new SerializedSocketReceiver(
            buffer => socketProvider.OnReceived(this, buffer, 0, buffer.LongLength));
        sender = new SerializedSocketSender(
            buffer => SendAsync(buffer),
            () => IsConnected,
            () => BytesPending,
            () => BytesSending);
    }

    public bool SendApplicationAsync(byte[] buffer) => sender.TrySendApplication(buffer);

    public bool TrySendControl(byte[] buffer) => sender.TrySendControl(buffer);

    protected override void OnConnected()
    {
        socketProvider.OnConnected(this);
    }

    protected override void OnDisconnected()
    {
        sender.Stop();
        receiver.Stop();
        socketProvider.OnDisconnected(this);
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        if (receiver.TryDispatch(buffer.AsSpan((int)offset, (int)size).ToArray()))
            return;

        sender.Stop();
        receiver.Stop();
        socketProvider.OnError(this, SocketError.NoBufferSpaceAvailable);
        DisconnectAsync();
    }

    protected override void OnError(SocketError error)
    {
        sender.Stop();
        receiver.Stop();
        socketProvider.OnError(this, error);
    }

    protected override void OnHandshaked()
    {
        socketProvider.OnHandshaked(this);
    }
}


public partial class MulticastClient(string address, int port, IUdpClientProvider socketProvider, ILogger logger) : UdpClient(address, port)
{

    protected override void OnConnected()
    {
        ReceiveAsync();
    }

    protected override void OnDisconnected()
    {
    }

    protected override void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        socketProvider.OnReceived(endpoint, buffer, offset, size);
        ReceiveAsync();
    }
    protected override void OnError(SocketError error)
    {
        logger.Error($"Session {Id} encountered error: {error}");
    }
}
