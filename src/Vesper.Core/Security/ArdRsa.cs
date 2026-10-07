using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vesper.Core.Security;

/// <summary>
/// RFB security type 33, Apple's RSA login ("RSATunnel", plain-login form).
///
/// The client asks for the Mac's RSA public key, picks a random AES-128 key, encrypts the same
/// 128-byte credential block type 30 uses under it (ECB), and sends that together with the AES key
/// encrypted to the Mac's RSA key (PKCS#1 v1.5). The random AES key then wraps the record
/// layer's rekey. Unlike type 30, a man in the middle can't read the credentials without the
/// Mac's private key, and Vesper pins the key on first use so a substituted key is caught.
///
/// Wire format from the noVNC-ARD project (MPL-2.0, ard/ard-patch.js), which reports it working
/// against live screensharingd, and remotex's docs/apple-vnc-889.md "Other login types".
/// </summary>
public static class ArdRsa
{
    public const byte SecurityType = 33;

    /// <summary>The 14-byte key request: u32 length 10, u16le version 1, "RSA1", u16 kind 0, u16 pad.</summary>
    public static byte[] KeyRequest() =>
        [0, 0, 0, 10, 1, 0, (byte)'R', (byte)'S', (byte)'A', (byte)'1', 0, 0, 0, 0];

    /// <summary>
    /// Parses the key response payload (after its u32 length): four bytes of version/kind, a u16 BE
    /// key length, the DER key, and trailing padding. Returns the DER bytes.
    /// </summary>
    public static byte[] ParseKeyResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
        {
            throw new Rfb.ProtocolException("The Mac's RSA key response is too short.");
        }
        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        if (keyLength == 0 || 6 + keyLength > payload.Length)
        {
            throw new Rfb.ProtocolException("The Mac's RSA key response has a bad key length.");
        }
        return payload.Slice(6, keyLength).ToArray();
    }

    public static RSA ImportPublicKey(byte[] der)
    {
        var rsa = RSA.Create();
        try
        {
            try
            {
                rsa.ImportRSAPublicKey(der, out _);
            }
            catch (CryptographicException)
            {
                rsa.ImportSubjectPublicKeyInfo(der, out _);
            }
            if (rsa.KeySize < 2048)
            {
                throw new Rfb.ProtocolException($"The Mac offered a {rsa.KeySize}-bit RSA key; Vesper requires at least 2048 bits.");
            }
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>OpenSSH-style fingerprint of the DER key, for pinning and display.</summary>
    public static string Fingerprint(byte[] der) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(der)).TrimEnd('=');

    /// <summary>
    /// Builds the login message (with its u32 length prefix) and returns the random AES key, which
    /// becomes the record layer's wrap key. The caller owns and must zero the returned key.
    /// </summary>
    public static (byte[] Message, byte[] WrapKey) Login(RSA macKey, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password)
    {
        byte[] aesKey = RandomNumberGenerator.GetBytes(16);
        byte[] block = new byte[128];
        try
        {
            RandomNumberGenerator.Fill(block);
            CopyField(username, block.AsSpan(0, 64));
            CopyField(password, block.AsSpan(64, 64));

            byte[] credentials;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                credentials = aes.EncryptEcb(block, PaddingMode.None);
            }
            byte[] wrappedKey = macKey.Encrypt(aesKey, RSAEncryptionPadding.Pkcs1);

            int blob = 2 + 4 + 2 + credentials.Length + 2 + wrappedKey.Length;
            byte[] message = new byte[4 + blob];
            var m = message.AsSpan();
            BinaryPrimitives.WriteUInt32BigEndian(m, (uint)blob);
            m[4] = 1; m[5] = 0;                                     // u16le version 1
            "RSA1"u8.CopyTo(m[6..]);
            BinaryPrimitives.WriteUInt16BigEndian(m[10..], 1);      // kind 1: plain login
            credentials.CopyTo(m[12..]);
            BinaryPrimitives.WriteUInt16LittleEndian(m[(12 + credentials.Length)..], (ushort)wrappedKey.Length);
            wrappedKey.CopyTo(m[(14 + credentials.Length)..]);
            return (message, aesKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(aesKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
        }
    }

    private static void CopyField(ReadOnlySpan<byte> value, Span<byte> field)
    {
        int n = Math.Min(value.Length, 63);
        value[..n].CopyTo(field);
        field[n] = 0;
    }
}
