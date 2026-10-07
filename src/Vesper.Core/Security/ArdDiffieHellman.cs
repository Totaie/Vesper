using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Vesper.Core.Security;

/// <summary>
/// RFB security type 30, Apple's "Diffie-Hellman" authentication (as used by Screen Sharing and
/// implemented by libvncclient and noVNC).
///
/// The server sends a generator, a key length, a prime and its public value. The client answers
/// with 128 bytes of AES-128-ECB-encrypted credentials (user name and password in two 64-byte,
/// NUL-terminated, random-padded halves) followed by its own public value. The AES key is
/// MD5 of the shared secret, left-padded to the key length.
///
/// The exchange has no server authentication, so on its own it hides the password only from a
/// passive eavesdropper. Vesper relies on the transport (SSH or a WireGuard VPN) for real
/// confidentiality, and treats a bare connection as untrusted.
/// </summary>
public static class ArdDiffieHellman
{
    public const byte SecurityType = 30;

    private const int MinKeyBytes = 64;   // 512-bit; anything smaller is a broken or hostile server
    private const int MaxKeyBytes = 1024; // 8192-bit; a bound on allocation and modpow cost

    public sealed record ServerParameters(byte[] Generator, byte[] Prime, byte[] ServerPublicKey)
    {
        public int KeyLength => Prime.Length;
    }

    public static void ValidateKeyLength(int keyLength)
    {
        if (keyLength < MinKeyBytes || keyLength > MaxKeyBytes)
        {
            throw new Rfb.ProtocolException($"The Mac proposed an unusable Diffie-Hellman key length ({keyLength} bytes).");
        }
    }

    /// <summary>
    /// Builds the 128-byte credentials block plus public key the client sends back, and returns
    /// the wrap key (MD5 of the shared secret) that Apple's record layer rekey is wrapped under.
    /// The caller owns and must zero the wrap key.
    /// </summary>
    /// <param name="username">UTF-8 user name bytes. Truncated to 63 bytes.</param>
    /// <param name="password">UTF-8 password bytes. Truncated to 63 bytes. Not retained.</param>
    public static (byte[] EncryptedCredentials, byte[] ClientPublicKey, byte[] WrapKey) Respond(ServerParameters server, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password)
    {
        ValidateKeyLength(server.KeyLength);
        byte[] privateBytes = RandomNumberGenerator.GetBytes(server.KeyLength);
        byte[] padding = RandomNumberGenerator.GetBytes(128);
        try
        {
            return Respond(server, username, password, privateBytes, padding);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(padding);
        }
    }

    /// <summary>
    /// The deterministic core: the private exponent and the credential block's padding are passed
    /// in, so the computation can be checked against other implementations' test vectors.
    /// </summary>
    internal static (byte[] EncryptedCredentials, byte[] ClientPublicKey, byte[] WrapKey) Respond(
        ServerParameters server, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, byte[] privateBytes, byte[] padding)
    {
        ValidateKeyLength(server.KeyLength);
        if (server.ServerPublicKey.Length != server.KeyLength || server.Generator.Length == 0)
        {
            throw new Rfb.ProtocolException("Diffie-Hellman parameter length mismatch.");
        }

        var p = FromBigEndian(server.Prime);
        var g = FromBigEndian(server.Generator);
        var serverPublic = FromBigEndian(server.ServerPublicKey);

        // Reject degenerate parameters that would make the shared secret predictable.
        if (p.IsEven || p < 5)
        {
            throw new Rfb.ProtocolException("The Mac sent an invalid Diffie-Hellman prime.");
        }
        if (g < 2 || g >= p - 1)
        {
            throw new Rfb.ProtocolException("The Mac sent an invalid Diffie-Hellman generator.");
        }
        if (serverPublic < 2 || serverPublic >= p - 1)
        {
            throw new Rfb.ProtocolException("The Mac sent an invalid Diffie-Hellman public key.");
        }

        int keyLength = server.KeyLength;
        byte[] sharedBytes = new byte[keyLength];
        byte[] block = new byte[128];
        byte[] aesKey = [];
        try
        {
            // A key-length random exponent, as noVNC and libvncclient use.
            var privateKey = FromBigEndian(privateBytes);
            if (privateKey < 2)
            {
                throw new Rfb.ProtocolException("Diffie-Hellman private key generation failed.");
            }
            var clientPublic = BigInteger.ModPow(g, privateKey, p);
            var shared = BigInteger.ModPow(serverPublic, privateKey, p);
            if (shared <= 1 || shared == p - 1)
            {
                throw new Rfb.ProtocolException("Diffie-Hellman produced a degenerate shared secret.");
            }

            // MD5 over the secret left-padded to the full key length: dropping its leading zeros
            // would make the two ends disagree in a fraction of connections.
            WriteBigEndianPadded(shared, sharedBytes);
            aesKey = MD5.HashData(sharedBytes);

            // Random padding first, then the NUL-terminated strings on top.
            padding.AsSpan(0, 128).CopyTo(block);
            CopyField(username, block.AsSpan(0, 64));
            CopyField(password, block.AsSpan(64, 64));

            using var aes = Aes.Create();
            aes.Key = aesKey;
            byte[] encrypted = aes.EncryptEcb(block, PaddingMode.None);

            byte[] publicBytes = new byte[keyLength];
            WriteBigEndianPadded(clientPublic, publicBytes);
            return (encrypted, publicBytes, aesKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(aesKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedBytes);
            CryptographicOperations.ZeroMemory(block);
        }
    }

    /// <summary>63 bytes of string, then a NUL; the rest of the 64-byte field keeps its random fill.</summary>
    private static void CopyField(ReadOnlySpan<byte> value, Span<byte> field)
    {
        int n = Math.Min(value.Length, 63);
        value[..n].CopyTo(field);
        field[n] = 0;
    }

    /// <summary>
    /// Truncates UTF-8 to at most <paramref name="maxBytes"/> without splitting a multi-byte character.
    /// </summary>
    public static byte[] Utf8Truncated(string value, int maxBytes = 63)
    {
        byte[] all = Encoding.UTF8.GetBytes(value);
        if (all.Length <= maxBytes) return all;
        int n = maxBytes;
        while (n > 0 && (all[n] & 0xC0) == 0x80) n--;
        byte[] cut = all[..n];
        CryptographicOperations.ZeroMemory(all);
        return cut;
    }

    internal static BigInteger FromBigEndian(ReadOnlySpan<byte> bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: true);

    internal static void WriteBigEndianPadded(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        int count = value.GetByteCount(isUnsigned: true);
        if (count > destination.Length)
        {
            throw new Rfb.ProtocolException("Diffie-Hellman value exceeds the key length.");
        }
        if (!value.TryWriteBytes(destination[(destination.Length - count)..], out _, isUnsigned: true, isBigEndian: true))
        {
            throw new Rfb.ProtocolException("Could not encode a Diffie-Hellman value.");
        }
    }
}
