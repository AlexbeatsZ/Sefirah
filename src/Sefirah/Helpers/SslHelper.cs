using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCoreServer;

namespace Sefirah.Helpers;

/// <summary>
/// Manages SSL/TLS certificates and provides SslContext.
/// </summary>
public static class SslHelper
{
    private static readonly Lazy<X509Certificate2> LocalCertificate = new(LoadOrCreateCertificate);

    public static byte[] DevicePublicKeyEncoded => _devicePublicKeyEncoded ??= GetPublicKeyEncoded(GetOrCreateCertificate());
    private static byte[]? _devicePublicKeyEncoded;

    public static string DevicePublicKeyString => _devicePublicKeyString ??= GetPublicKeyStringBase64(DevicePublicKeyEncoded);
    private static string? _devicePublicKeyString;

    private static string GetPublicKeyStringBase64(byte[] encodedPublicKey)
    {
        return Convert.ToBase64String(encodedPublicKey);
    }

    private static byte[] GetPublicKeyEncoded(X509Certificate2 c)
    {
        using var ecdsa = c.GetECDsaPublicKey();
        if (ecdsa is not null) return ecdsa.ExportSubjectPublicKeyInfo();
        return [];
    }

    public static string GetVerificationCode(string theirPublicKeyBase64)
    {
        byte[] theirEncoded = Convert.FromBase64String(theirPublicKeyBase64); 

        if (theirEncoded.Length == 0) return "00000000";

        var concat = SortedConcatUnsigned(DevicePublicKeyEncoded, theirEncoded);
        return FormatVerificationCode(SHA256.HashData(concat));
    }

    /// <summary>concat in deterministic order (unsigned byte comparison). When a &lt; b returns b+a, else a+b.</summary>
    private static byte[] SortedConcatUnsigned(byte[] a, byte[] b)
    {
        return CompareUnsigned(a, b) < 0 ? [.. b, .. a] : [.. a, .. b];
    }

    private static int CompareUnsigned(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var u = a[i] & 0xFF;
            var v = b[i] & 0xFF;
            if (u != v) return u.CompareTo(v);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string FormatVerificationCode(byte[] hash)
    {
        var hex = Convert.ToHexString(hash).AsSpan(0, Math.Min(8, hash.Length * 2));
        return hex.ToString().ToUpperInvariant();
    }

    public static byte[] VerifyPeerCertificate(byte[]? certificate, string publicKey)
    {
        if (certificate is null) throw new AuthenticationException("TLS peer certificate is missing");
        using var peer = X509CertificateLoader.LoadCertificate(certificate);
        if (GetPublicKeyStringBase64(GetPublicKeyEncoded(peer)) != publicKey)
            throw new AuthenticationException("Authentication public key does not match this TLS peer");
        return certificate;
    }

    private static string CertificateFileName { get; } = "Sefirah.pfx";

    private static X509Certificate2 CreateECDSACertificate()
    {
        // Create ECDSA with NIST P-256 curve
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subjectName = new X500DistinguishedName("CN=SefirahCastle");
        CertificateRequest certRequest = new(subjectName, ecdsa, HashAlgorithmName.SHA256);

        // Add certificate extensions
        certRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        certRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));

        // Create self-signed certificate valid for 10 years
        using X509Certificate2 certificate = certRequest.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(10));

        // Ensure the certificate is exportable
        byte[] exportedData = certificate.Export(X509ContentType.Pfx);
        var certPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, CertificateFileName);
        var temporary = certPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, exportedData);
            try { File.Move(temporary, certPath, overwrite: false); }
            catch (IOException) when (File.Exists(certPath))
            { return X509CertificateLoader.LoadPkcs12FromFile(certPath, null); }
            return X509CertificateLoader.LoadPkcs12(exportedData, null, X509KeyStorageFlags.Exportable);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static X509Certificate2 GetOrCreateCertificate() => LocalCertificate.Value;

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        string certPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, CertificateFileName);

        // A read failure must preserve the existing identity and surface the error.
        if (File.Exists(certPath)) return X509CertificateLoader.LoadPkcs12FromFile(certPath, null);

        return CreateECDSACertificate();
    }

    public static SslContext GetSslContext()
    {
        return new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, GetOrCreateCertificate(), (sender, cert, chain, errors) => TlsHandshake.Capture(sender, cert))
        {
            ClientCertificateRequired = true
        };
    }

    /// <summary>SslContext which validates peer cert against expected cert.</summary>
    public static SslContext CreateSslContext(byte[] expectedPeerCert)
    {
        return new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, GetOrCreateCertificate(), Validate)
        {
            ClientCertificateRequired = true
        };

        bool Validate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            if (cert is null) return false;
            using var c = new X509Certificate2(cert);
            var raw = c.RawData;
            return raw.AsSpan().SequenceEqual(expectedPeerCert) && TlsHandshake.Capture(sender, cert);
        }
    }

}
