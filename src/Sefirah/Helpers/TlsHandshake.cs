using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Sefirah.Helpers;

/// <summary>
/// NetCoreServer starts SslStream authentication immediately after OnHandshaking.
/// ExecutionContext carries this per-handshake owner into certificate validation.
/// The certificate stays with that transport for its entire lifetime.
/// </summary>
public sealed class TlsHandshake
{
    private static readonly AsyncLocal<TlsHandshake?> Current = new();
    private SslStream? stream;
    public byte[]? PeerCertificate { get; private set; }

    public void Begin() => Current.Value = this;

    // Dispose cancels pending reads. NetCoreServer's ShutdownAsync before disposal can
    // race SecureTransport's SSLRead/SSLClose on macOS during crossed disconnects.
    public void Abort() => stream?.Dispose();

    public static bool Capture(object sender, X509Certificate? certificate)
    {
        var owner = Current.Value;
        if (owner is null || certificate is null || sender is not SslStream tlsStream) return false;
        if (owner.stream is not null && !ReferenceEquals(owner.stream, sender)) return false;
        owner.stream = tlsStream;
        owner.PeerCertificate = certificate.GetRawCertData();
        return true;
    }
}
