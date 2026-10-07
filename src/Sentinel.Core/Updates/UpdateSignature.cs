using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Core.Updates;

/// <summary>
/// Detached ECDSA P-256 / SHA-256 signatures for release packages. The private key never leaves the maintainer's
/// machine; the app only embeds the public key, so a package that was not signed by the maintainer — whether
/// tampered in transit, replaced on the server, or served by anyone else — is rejected before it is unpacked.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Public key of the official Sentinel release signing key (SubjectPublicKeyInfo, PEM).</summary>
    public const string OfficialPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGAFKAqOWd/o7O1SrbM0GKSbu9Uwd
        ScF37ToWRujdSZO9ntDKkefatpS7RHyecBP650ok+i2OB9wcwZYwgEGUlA==
        -----END PUBLIC KEY-----
        """;

    /// <summary>Signature file format: a single line of base64 (IEEE P1363 r||s).</summary>
    public static string Sign(Stream data, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256) throw new CryptographicException("Release keys must be P-256.");
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(Stream data, string signatureText, string publicKeyPem)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64) return false;
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (key.KeySize != 256) return false;
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public static (string PrivatePem, string PublicPem) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>SHA-256 of a public key, shown in Settings so users can compare it with the one published in the repository.</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
            var sb = new StringBuilder();
            for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 7 ? ":" : "");
            return sb.ToString();
        }
        catch (ArgumentException)
        {
            return "not configured";
        }
    }
}
