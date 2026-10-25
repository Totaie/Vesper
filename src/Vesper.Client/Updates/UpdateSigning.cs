using System.Security.Cryptography;

namespace Vesper.Updates;

/// <summary>
/// Release signatures. Each update description is signed with an ECDSA P-256 key that exists only
/// on the release machine; Vesper carries the public half and installs nothing whose description
/// doesn't verify. GitHub, a network attacker or anyone who takes over the releases page can
/// withhold updates, but can't make Vesper install something else.
/// </summary>
public static class UpdateSigning
{
    /// <summary>The release key's public half: SubjectPublicKeyInfo, base64.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHKecoyDJn89bsOKKT5HQ2b0+HYFe23tHaJywrAX2LGvsywXy4tCylHsqCzGq13o0YUeqA86AoOVXngGzEAbUYA==";

    private const string P256 = "1.2.840.10045.3.1.7";

    /// <summary>Signatures cover this label too, so they can't be reused for anything but an update description.</summary>
    private static readonly byte[] Context = "Vesper update manifest v1\n"u8.ToArray();

    public static bool Verify(ReadOnlySpan<byte> manifest, string signature) => Verify(manifest, signature, PublicKey);

    public static bool Verify(ReadOnlySpan<byte> manifest, string signature, string publicKey)
    {
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (sig.Length != 64) return false;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (key.ExportParameters(false).Curve.Oid.Value != P256) return false;
        return key.VerifyData(Signed(manifest), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Signs a description (the release tool's job; the app never holds the private key).</summary>
    public static string Sign(ReadOnlySpan<byte> manifest, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(Signed(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static byte[] Signed(ReadOnlySpan<byte> manifest) => [.. Context, .. manifest];
}
