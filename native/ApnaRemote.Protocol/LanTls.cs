using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApnaRemote.Protocol;

/// <summary>
/// BCL-only ephemeral TLS for attended LAN. Fingerprint must be compared out-of-band.
/// PIN is never sent in cleartext; proof is bound to the server certificate.
/// </summary>
public static class LanTls
{
    public const int MaxPinFailuresPerListen = 5;
    public const int PinProofBytes = 32;
    /// <summary>Hard ceiling so a half-open TCP peer cannot hang AuthenticateAs* forever.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(12);
    private const int PinKdfIterations = 80_000;
    private static readonly byte[] PinProofInfo = Encoding.UTF8.GetBytes("ApnaRemote-PIN-Proof-v1");

    public static X509Certificate2 CreateEphemeral(out string fingerprintDisplay)
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ApnaRemote.LAN", ecdsa, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(12));
        // Windows Schannel server authentication requires a persisted key container. Without
        // PersistKeySet the temporary key is removed when this listen-window certificate is disposed.
        X509Certificate2 usable = X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        fingerprintDisplay = FormatFingerprint(usable);
        return usable;
    }

    public static string FormatFingerprint(X509Certificate2 certificate)
    {
        byte[] hash = SHA256.HashData(certificate.RawData);
        Span<char> hex = stackalloc char[16];
        for (int i = 0; i < 8; i++)
        {
            byte b = hash[i];
            hex[i * 2] = ToHex(b >> 4);
            hex[i * 2 + 1] = ToHex(b & 0xF);
        }

        return new string(hex[..4]) + "-" + new string(hex.Slice(4, 4)) + "-"
            + new string(hex.Slice(8, 4)) + "-" + new string(hex.Slice(12, 4));
    }

    public static string NormalizeFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(16);
        foreach (char c in value)
        {
            if (c is '-' or ' ' or '\t') continue;
            if (c is >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'a' and <= 'f') sb.Append((char)(c - 32));
            else if (c is >= 'A' and <= 'F') sb.Append(c);
            else return "";
            if (sb.Length > 16) return "";
        }

        return sb.Length == 16 ? sb.ToString() : "";
    }

    public static bool IsValidFingerprintInput(string value)
        => NormalizeFingerprint(value).Length == 16;

    public static byte[] ComputePinProof(string pin, X509Certificate2 serverCertificate)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        byte[] salt = SHA256.HashData(serverCertificate.RawData);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), salt, PinKdfIterations, HashAlgorithmName.SHA256, PinProofBytes);
        try
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(PinProofInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool VerifyPinProof(string expectedPin, X509Certificate2 serverCertificate, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != PinProofBytes) return false;
        byte[] expected = ComputePinProof(expectedPin, serverCertificate);
        try { return CryptographicOperations.FixedTimeEquals(expected, proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public static bool MatchesFingerprint(string expectedDisplay, X509Certificate2 remote)
    {
        string expected = NormalizeFingerprint(expectedDisplay);
        string actual = NormalizeFingerprint(FormatFingerprint(remote));
        return expected.Length == 16 && FixedTimeEquals(expected, actual);
    }

    public static async Task<SslStream> AuthenticateAsServerAsync(
        Stream inner, X509Certificate2 serverCertificate, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(HandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(options, linked.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<SslStream> AuthenticateAsClientAsync(
        Stream inner, string expectedFingerprint, CancellationToken ct)
    {
        string expected = NormalizeFingerprint(expectedFingerprint);
        if (expected.Length != 16)
            throw new InvalidOperationException("Enter the host fingerprint exactly (XXXX-XXXX-XXXX-XXXX).");

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "ApnaRemote.LAN",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var remote = new X509Certificate2(certificate);
                return MatchesFingerprint(expected, remote);
            },
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(HandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsClientAsync(options, linked.Token).ConfigureAwait(false);
            return ssl;
        }
        catch (Exception ex) when (IsCertificateRejected(ex))
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException(
                "Wrong fingerprint. Open the host Share panel, copy the Fingerprint exactly (XXXX-XXXX-XXXX-XXXX), then Connect. Nearby Find only fills the IP — compare fingerprint on the host screen. If the host clicked Stop/Start sharing, PIN and fingerprint both changed.");
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsCertificateRejected(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return true;
            string m = e.Message;
            if (m.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase)) return true;
            if (m.Contains("certificate was rejected", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static char ToHex(int value) => (char)(value < 10 ? '0' + value : 'A' + (value - 10));

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
