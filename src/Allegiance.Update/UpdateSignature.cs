using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Allegiance.Update;

/// <summary>
/// P810 — ECDSA P-256 signatures over the exact manifest bytes (SHA-256 digest).
/// Native shipped files with no authenticity check at all (the FTP credentials
/// rode every logon response, <c>lobby/messageslc.h:45-49</c>) — classified an
/// era constraint; the port signs the feed so a tampered manifest or package is
/// refused before anything is staged. Pure BCL (no NuGet dependency), so the
/// AOT-published server can verify too.
/// </summary>
public static class UpdateSignature
{
    /// <summary>PEM label of the PKCS#8 private key the release pipeline holds.</summary>
    public const string PrivateKeyPemLabel = "PRIVATE KEY";

    /// <summary>Sign <paramref name="data"/> with a PKCS#8 PEM private key.
    /// Returns the IEEE-P1363 (r‖s) signature, base64-encoded.</summary>
    public static string Sign(byte[] data, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256)
            throw new InvalidOperationException($"the update signing key must be P-256 (got {ecdsa.KeySize} bits)");
        return Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }

    /// <summary>Verify a base64 signature over <paramref name="data"/> with a
    /// SubjectPublicKeyInfo PEM public key. Any malformed input is simply false.</summary>
    public static bool Verify(byte[] data, string signatureBase64, string publicKeyPem)
    {
        try
        {
            byte[] sig = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or AsnContentException
                                       or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Generate a throwaway P-256 key pair (tests, tooling). Returns
    /// (privatePem, publicPem).</summary>
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }
}
