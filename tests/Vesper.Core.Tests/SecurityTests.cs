using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Vesper.Core.Rfb;
using Vesper.Core.Security;

namespace Vesper.Core.Tests;

public class SecurityTests
{
    /// <summary>
    /// noVNC's own test vector for type 30 (tests/test.rfb.js, "should return properly encrypted
    /// credentials and public key"): generator 0x7fff, prime and private key both the bytes 0..127,
    /// credential padding also 0..127, user "user", password "password". Matching it shows the DH,
    /// the zero-padded MD5 key, the credential layout and the ECB encryption agree byte for byte
    /// with an independent implementation that works against real Macs.
    /// </summary>
    [Fact]
    public void DiffieHellman_matches_noVNC_test_vector()
    {
        byte[] bytes0To127 = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        byte[] generator = [127, 255];
        var p = new BigInteger(bytes0To127, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(generator, isUnsigned: true, isBigEndian: true);
        var x = new BigInteger(bytes0To127, isUnsigned: true, isBigEndian: true);
        byte[] serverPublic = new byte[128];
        ArdDiffieHellman.WriteBigEndianPadded(BigInteger.ModPow(g, x, p), serverPublic);

        var server = new ArdDiffieHellman.ServerParameters(generator, bytes0To127, serverPublic);
        var (encrypted, clientPublic, wrapKey) = ArdDiffieHellman.Respond(
            server, "user"u8, "password"u8, (byte[])bytes0To127.Clone(), (byte[])bytes0To127.Clone());

        byte[] expected =
        [
            199, 39, 204, 95, 190, 70, 127, 66, 5, 106, 153, 228, 123, 236, 150, 206,
            62, 107, 11, 4, 21, 242, 92, 184, 9, 81, 35, 125, 56, 167, 1, 215,
            182, 145, 183, 75, 245, 197, 47, 19, 122, 94, 64, 76, 77, 163, 222, 143,
            186, 174, 84, 39, 244, 179, 227, 114, 83, 231, 42, 106, 205, 43, 159, 110,
            209, 240, 157, 246, 237, 206, 134, 153, 195, 112, 92, 60, 28, 234, 91, 66,
            131, 38, 187, 195, 110, 167, 212, 241, 32, 250, 212, 213, 202, 89, 180, 21,
            71, 217, 209, 81, 42, 61, 118, 248, 65, 123, 98, 78, 139, 111, 202, 137,
            50, 185, 37, 173, 58, 99, 187, 53, 42, 125, 13, 165, 232, 163, 151, 42, 0,
        ];
        Assert.Equal(expected[..128], encrypted);
        Assert.Equal(serverPublic, clientPublic); // same private key on both sides in the vector
        Assert.Equal(16, wrapKey.Length);
    }

    [Fact]
    public void DiffieHellman_rejects_degenerate_parameters()
    {
        byte[] prime = new byte[64];
        prime[0] = 0xC0;
        prime[^1] = 0x01;
        byte[] one = new byte[64];
        one[^1] = 1;
        Assert.Throws<ProtocolException>(() =>
            ArdDiffieHellman.Respond(new([0, 2], prime, one), "u"u8, "p"u8));
        Assert.Throws<ProtocolException>(() =>
            ArdDiffieHellman.Respond(new([0, 1], prime, prime), "u"u8, "p"u8));
        Assert.Throws<ProtocolException>(() => ArdDiffieHellman.ValidateKeyLength(16));
        Assert.Throws<ProtocolException>(() => ArdDiffieHellman.ValidateKeyLength(4096));
    }

    [Fact]
    public void Utf8_truncation_never_splits_a_character()
    {
        string name = new string('a', 62) + "é"; // 62 + 2 bytes = 64 > 63
        byte[] cut = ArdDiffieHellman.Utf8Truncated(name);
        Assert.Equal(62, cut.Length);
        Assert.Equal(new string('a', 62), Encoding.UTF8.GetString(cut));
    }

    [Fact]
    public void Rsa_login_round_trips_through_the_Mac_side()
    {
        using var mac = RSA.Create(2048);
        byte[] der = mac.ExportRSAPublicKey();
        using var imported = ArdRsa.ImportPublicKey(der);
        var (message, wrapKey) = ArdRsa.Login(imported, "admin"u8, "correct horse"u8);

        // u32 length, u16le 1, "RSA1", u16 kind 1, 128 bytes, u16le 256, 256 bytes.
        Assert.Equal(4 + 2 + 4 + 2 + 128 + 2 + 256, message.Length);
        Assert.Equal("RSA1", Encoding.ASCII.GetString(message, 6, 4));
        byte[] key = mac.Decrypt(message[(4 + 2 + 4 + 2 + 128 + 2)..], RSAEncryptionPadding.Pkcs1);
        Assert.Equal(wrapKey, key);

        using var aes = Aes.Create();
        aes.Key = key;
        byte[] block = aes.DecryptEcb(message.AsSpan(12, 128), PaddingMode.None);
        Assert.Equal("admin", Encoding.UTF8.GetString(block, 0, 5));
        Assert.Equal(0, block[5]);
        Assert.Equal("correct horse", Encoding.UTF8.GetString(block, 64, 13));
        Assert.Equal(0, block[64 + 13]);
    }

    [Fact]
    public void Rsa_keys_below_2048_bits_are_refused()
    {
        using var weak = RSA.Create(1024);
        Assert.Throws<ProtocolException>(() => ArdRsa.ImportPublicKey(weak.ExportRSAPublicKey()));
    }

    [Fact]
    public void Record_layer_round_trips_messages_of_every_size()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        using var writer = new RecordWriter(key, iv);
        var wire = new MemoryStream();
        var messages = new[] { 0, 1, 13, 16, 100, 65_498, 65_499, 200_000 }
            .Select(n => RandomNumberGenerator.GetBytes(n)).ToArray();
        foreach (var m in messages) wire.Write(writer.Frame(m).Span);

        wire.Position = 0;
        using var reader = new RecordReaderStream(wire, key, iv, []);
        var all = new MemoryStream();
        reader.CopyTo(all);
        Assert.Equal(messages.SelectMany(m => m).ToArray(), all.ToArray());
    }

    [Fact]
    public void Record_layer_detects_tampering()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        using var writer = new RecordWriter(key, iv);
        byte[] wire = writer.Frame("keystrokes"u8.ToArray()).ToArray();
        wire[^1] ^= 1;
        using var reader = new RecordReaderStream(new MemoryStream(wire), key, iv, []);
        Assert.Throws<ProtocolException>(() => reader.ReadByte());
    }

    [Fact]
    public void Record_layer_detects_replayed_records()
    {
        // The sequence number is in the hash, so the same record twice fails the second time.
        byte[] key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        using var writer = new RecordWriter(key, iv);
        byte[] record = writer.Frame("one"u8.ToArray()).ToArray();
        using var reader = new RecordReaderStream(new MemoryStream([.. record, .. record]), key, iv, []);
        var buffer = new byte[3];
        reader.ReadExactly(buffer);
        Assert.Throws<ProtocolException>(() => reader.ReadByte());
    }

    [Fact]
    public void Rekey_unwraps_each_half_with_ecb()
    {
        byte[] wrap = RandomNumberGenerator.GetBytes(16), key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = wrap;
        byte[] body = [0, 0, 0, 7, .. aes.EncryptEcb(key, PaddingMode.None), .. aes.EncryptEcb(iv, PaddingMode.None)];
        var (generation, gotKey, gotIv) = AppleRecordLayer.UnwrapRekey(wrap, body);
        Assert.Equal(7u, generation);
        Assert.Equal(key, gotKey);
        Assert.Equal(iv, gotIv);
    }
}
