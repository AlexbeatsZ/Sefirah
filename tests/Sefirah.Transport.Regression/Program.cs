using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetCoreServer;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Services.Transfer;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

using (var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "messages.json"))))
{
    foreach (var fixture in fixtures.RootElement.EnumerateArray())
    {
        var message = Sefirah.Utils.Serialization.JsonMessageSerializer.DeserializeMessage(fixture.GetRawText());
        Check(message is not null, "protocol sample parsed");
        var encodedMessage = Sefirah.Utils.Serialization.JsonMessageSerializer.Serialize(message!);
        var roundTrip = Sefirah.Utils.Serialization.JsonMessageSerializer.DeserializeMessage(encodedMessage);
        Check(roundTrip?.GetType() == message!.GetType(), "protocol discriminator round trip");
    }
}

using (var identity = CreateCertificate())
{
    using var key = identity.GetECDsaPublicKey();
    var publicKey = Convert.ToBase64String(key!.ExportSubjectPublicKeyInfo());
    Check(SslHelper.VerifyPeerCertificate(identity.RawData, publicKey).SequenceEqual(identity.RawData), "TLS public key binding");
    try { SslHelper.VerifyPeerCertificate(identity.RawData, "unrelated-public-key"); throw new Exception("Unbound public key accepted"); }
    catch (AuthenticationException) { checks++; }
}

var text = "中文🙂 clipboard\r\nsecond\n";
var encoded = Encoding.UTF8.GetBytes(text);
for (var split = 0; split <= encoded.Length; split++)
{
    var framing = new Utf8LineBuffer();
    var lines = framing.Append(encoded.AsSpan(0, split));
    lines.AddRange(framing.Append(encoded.AsSpan(split)));
    Check(lines.SequenceEqual(new[] { "中文🙂 clipboard", "second" }), $"UTF-8 split {split}");
}
var oneByte = new Utf8LineBuffer();
var decoded = encoded.SelectMany(value => oneByte.Append(new[] { value })).ToArray();
Check(decoded.SequenceEqual(new[] { "中文🙂 clipboard", "second" }), "byte-at-a-time UTF-8");
foreach (var wire in new[] { "start\ncomplete\nstart\n", "startcompletestart", "start\r\ncomplete\r\nstart\n" })
{
    for (var split = 0; split <= wire.Length; split++)
    {
        var controls = new TransferControlBuffer();
        var messages = controls.Append(Encoding.ASCII.GetBytes(wire[..split]));
        messages.AddRange(controls.Append(Encoding.ASCII.GetBytes(wire[split..])));
        Check(messages.SequenceEqual(new[] { "start", "complete", "start" }), $"transfer split {split}");
    }
}
var directory = Path.Combine(Path.GetTempPath(), ".agents", "sefirah-transfer-" + Guid.NewGuid());
try
{
    using (var first = TransferFiles.CreateUnique(directory, "example.txt")) first.WriteByte(42);
    using (var duplicate = TransferFiles.CreateUnique(directory, "example.txt"))
        Check(Path.GetFileName(duplicate.Name) == "example (1).txt", "collision preserved");
    Check(File.ReadAllBytes(Path.Combine(directory, "example.txt")).SequenceEqual(new byte[] { 42 }), "existing file untouched");
    foreach (var name in new[] { "../escape", "..\\escape", "/absolute", "C:\\absolute", "..", "bad\0name" })
    {
        try { using var invalid = TransferFiles.CreateUnique(directory, name); throw new Exception("unsafe name accepted"); }
        catch (InvalidDataException) { checks++; }
    }
    using var empty = TransferFiles.CreateUnique(directory, "empty");
    Check(empty.Length == 0, "zero-byte file");
}
finally { Directory.Delete(directory, true); }

Console.WriteLine($"PASS {checks} framing, file naming and protocol checks");
using var serverCertificate = CreateCertificate();
var serverProvider = new ServerProvider();
var context = new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, serverCertificate,
    (sender, certificate, _, _) => TlsHandshake.Capture(sender, certificate)) { ClientCertificateRequired = true };
using var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using var server = new Server(context, IPAddress.Loopback, port, serverProvider);
Check(server.Start(), "TLS server start");
await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
{
    using var certificate = CreateCertificate();
    var clientContext = new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, certificate,
        (sender, peer, _, _) => TlsHandshake.Capture(sender, peer));
    var provider = new ClientProvider();
    using var client = new Client(clientContext, "127.0.0.1", port, provider);
    if (!client.ConnectAsync()) throw new Exception("Client failed to connect");
    await provider.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    if (client.Handshake.PeerCertificate is not { } peer || !peer.AsSpan().SequenceEqual(serverCertificate.RawData))
        throw new Exception("Client TLS certificate bound to wrong transport");
    var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    client.Send(Encoding.ASCII.GetBytes(fingerprint));
    await serverProvider.Received.GetOrAdd(fingerprint, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
        .Task.WaitAsync(TimeSpan.FromSeconds(10));
    client.Disconnect();
}));
Check(serverProvider.Errors.IsEmpty, "concurrent certificate isolation");
Console.WriteLine("PASS concurrent TLS certificate isolation");
server.Stop();
var transferRoot = Path.Combine(Path.GetTempPath(), ".agents", "sefirah-handler-" + Guid.NewGuid());
Directory.CreateDirectory(transferRoot);
ApplicationData.Current.LocalFolder.Path = transferRoot;
try
{
    var source = Path.Combine(transferRoot, "source");
    var destination = Path.Combine(transferRoot, "received");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(destination);
    var contents = new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("中文🙂", 100_000))) };
    var metadata = new List<Sefirah.Data.Models.FileMetadata>();
    var sources = new List<StorageFile>();
    for (var index = 0; index < contents.Length; index++)
    {
        var path = Path.Combine(source, $"{index}.bin");
        File.WriteAllBytes(path, contents[index]);
        sources.Add(new StorageFile(path));
        metadata.Add(new("repeated.bin", "application/octet-stream", contents[index].Length));
    }
    File.WriteAllText(Path.Combine(destination, "repeated.bin"), "existing file");
    var host = new TestHost();
    var peer = new Sefirah.Data.Models.PairedDevice();
    var transferContext = SslHelper.GetOrCreateCertificate().RawData;
    var ready = new TaskCompletionSource<Sefirah.Data.Models.ServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sender = new SendFileHandler(sources.ToArray(), metadata, peer, transferContext,
        info => ready.TrySetResult(info), host, host, silent: false);
    var connected = sender.WaitForConnectionAsync();
    var info = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    using var receiver = new ReceiveFileHandler(metadata, info, peer, transferContext, destination, false, host, host);
    await receiver.ConnectAsync();
    await connected.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAll(sender.SendAsync(), receiver.ReceiveAsync()).WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine("PASS real zero-byte and bulk file handlers");
    await NetworkRegression.RunAsync(Check);
    Check(host.Failures.Count == 0, "real file handler completion: " + string.Join(", ", host.Failures));
    Check(File.ReadAllText(Path.Combine(destination, "repeated.bin")) == "existing file", "handler preserves existing files");
    for (var index = 0; index < contents.Length; index++)
        Check(File.ReadAllBytes(Path.Combine(destination, $"repeated ({index + 1}).bin")).SequenceEqual(contents[index]), $"handler file {index}");
}
finally { Directory.Delete(transferRoot, true); }
Console.WriteLine($"PASS {checks} transport checks, including 24 concurrent mutually authenticated TLS connections");

static X509Certificate2 CreateCertificate()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var certificate = new CertificateRequest("CN=TransportRegression", key, HashAlgorithmName.SHA256)
        .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Schannel needs a PFX-imported private key rather than CreateSelfSigned's ephemeral CNG key.
    return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
}

sealed class ServerProvider : ITcpServerProvider
{
    public ConcurrentDictionary<string, TaskCompletionSource<bool>> Received { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();
    private readonly ConcurrentDictionary<Guid, string> buffers = new();
    public void OnConnected(ServerSession session) { }
    public void OnDisconnected(ServerSession session) { }
    public void OnError(SocketError error) => Errors.Enqueue(error.ToString());
    public void OnReceived(ServerSession session, byte[] buffer, long offset, long size)
    {
        var payload = buffers.AddOrUpdate(session.Id, Encoding.ASCII.GetString(buffer, (int)offset, (int)size),
            (_, prior) => prior + Encoding.ASCII.GetString(buffer, (int)offset, (int)size));
        if (payload.Length < 64) return;
        var actual = Convert.ToHexString(SHA256.HashData(session.Handshake.PeerCertificate ?? []));
        if (payload != actual) Errors.Enqueue("TLS identity crossed sessions");
        Received.GetOrAdd(payload, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
    }
}
sealed class ClientProvider : ITcpClientProvider
{
    public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void OnConnected(Client client) { }
    public void OnDisconnected(Client client) { }
    public void OnHandshaked(Client client) => Ready.TrySetResult(true);
    public void OnReceived(Client client, byte[] buffer, long offset, long size) { }
    public void OnError(Client client, SocketError error) => Ready.TrySetException(new IOException(error.ToString()));
}
public interface ILogger
{
    void Debug(string message);
    void Info(string message);
    void Error(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception exception) => Error(message + exception.Message);
    void Debug(string message, Exception exception) => Debug(message + exception.Message);
}
