using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vesper.Core.Security;

/// <summary>
/// Apple's Screen Sharing record layer (RFB 003.889): after the server's rekey, every message in
/// both directions travels as
/// <code>
/// wire       u16 ciphertext_len || ciphertext
/// plaintext  u16 body_len || body || filler || SHA1(u32_be(seq) || plaintext-before-trailer)
/// </code>
/// encrypted with AES-128-CBC. Each direction keeps one CBC chain for the whole session (a
/// record's last ciphertext block is the next record's IV) and its own record counter.
///
/// Byte layout as measured by the remotex project (MIT): docs/apple-vnc-889.md, "The record layer".
/// </summary>
public static class AppleRecordLayer
{
    public const int Block = 16;
    public const int Trailer = 20;
    public const int RekeyLength = 4 + Block + Block;
    internal const int BodyLengthField = 2;
    internal const int MaxCiphertext = ushort.MaxValue / Block * Block; // 65520
    internal const int MaxBody = MaxCiphertext - BodyLengthField - Trailer;
    internal static readonly int MinCiphertext = BodyLengthField + FillerLength(0) + Trailer;

    internal static int FillerLength(int bodyLength) =>
        (Block - (BodyLengthField + bodyLength + Trailer) % Block) % Block;

    /// <summary>
    /// Unwraps the rekey rectangle's body: a u32 generation, then the session key and initial IV,
    /// each a single AES-128-ECB block under the authentication's wrap key.
    /// </summary>
    public static (uint Generation, byte[] Key, byte[] Iv) UnwrapRekey(ReadOnlySpan<byte> wrapKey, ReadOnlySpan<byte> body)
    {
        if (body.Length != RekeyLength)
        {
            throw new Rfb.ProtocolException($"The rekey carried {body.Length} bytes, not {RekeyLength}.");
        }
        using var aes = Aes.Create();
        byte[] keyCopy = wrapKey.ToArray();
        try
        {
            aes.Key = keyCopy; // Aes keeps its own copy, which it wipes when disposed
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
        uint generation = BinaryPrimitives.ReadUInt32BigEndian(body);
        byte[] key = aes.DecryptEcb(body.Slice(4, Block), PaddingMode.None);
        byte[] iv = aes.DecryptEcb(body.Slice(4 + Block, Block), PaddingMode.None);
        return (generation, key, iv);
    }
}

/// <summary>One direction's AES-CBC chain and record counter. Never reset for the life of a session.</summary>
internal sealed class RecordCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _chain = new byte[AppleRecordLayer.Block];
    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private readonly byte[] _seqBytes = new byte[4];
    private uint _seq;

    public RecordCipher(byte[] key, byte[] iv)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        iv.CopyTo(_chain, 0);
    }

    public uint Sequence => _seq;

    public void Encrypt(Span<byte> data)
    {
        _aes.EncryptCbc(data, _chain, data, PaddingMode.None);
        data[^AppleRecordLayer.Block..].CopyTo(_chain);
    }

    public void Decrypt(Span<byte> data)
    {
        Span<byte> nextChain = stackalloc byte[AppleRecordLayer.Block];
        data[^AppleRecordLayer.Block..].CopyTo(nextChain);
        _aes.DecryptCbc(data, _chain, data, PaddingMode.None);
        nextChain.CopyTo(_chain);
    }

    /// <summary>SHA1(u32_be(seq) || covered), consuming one sequence number.</summary>
    public void Trailer(ReadOnlySpan<byte> covered, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_seqBytes, _seq);
        _sha1.AppendData(_seqBytes);
        _sha1.AppendData(covered);
        _sha1.GetHashAndReset(destination);
        _seq++;
    }

    public void Dispose()
    {
        _aes.Dispose();
        _sha1.Dispose();
        CryptographicOperations.ZeroMemory(_chain);
    }
}

/// <summary>Frames whole client messages into records. Each message gets its own record(s), as Apple's viewer sends them.</summary>
internal sealed class RecordWriter(byte[] key, byte[] iv) : IDisposable
{
    private readonly RecordCipher _cipher = new(key, iv);
    private byte[] _buffer = new byte[4096];

    /// <summary>Returns the framed bytes for one message. Valid until the next call.</summary>
    public ReadOnlyMemory<byte> Frame(ReadOnlySpan<byte> message)
    {
        int records = Math.Max(1, (message.Length + AppleRecordLayer.MaxBody - 1) / AppleRecordLayer.MaxBody);
        int needed = message.Length + records * (2 + AppleRecordLayer.BodyLengthField + AppleRecordLayer.Trailer + AppleRecordLayer.Block);
        if (_buffer.Length < needed)
        {
            _buffer = new byte[Math.Max(needed, _buffer.Length * 2)];
        }
        int at = 0;
        var rest = message;
        do
        {
            var body = rest[..Math.Min(rest.Length, AppleRecordLayer.MaxBody)];
            rest = rest[body.Length..];
            at += WriteRecord(body, _buffer.AsSpan(at));
        }
        while (!rest.IsEmpty);
        return _buffer.AsMemory(0, at);
    }

    private int WriteRecord(ReadOnlySpan<byte> body, Span<byte> output)
    {
        int total = AppleRecordLayer.BodyLengthField + body.Length + AppleRecordLayer.FillerLength(body.Length) + AppleRecordLayer.Trailer;
        BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)total);
        var plaintext = output.Slice(2, total);
        BinaryPrimitives.WriteUInt16BigEndian(plaintext, (ushort)body.Length);
        body.CopyTo(plaintext[AppleRecordLayer.BodyLengthField..]);
        int covered = total - AppleRecordLayer.Trailer;
        plaintext[(AppleRecordLayer.BodyLengthField + body.Length)..covered].Clear();
        _cipher.Trailer(plaintext[..covered], plaintext[covered..]);
        _cipher.Encrypt(plaintext);
        return 2 + total;
    }

    public void Dispose() => _cipher.Dispose();
}

/// <summary>
/// Reads records off the transport and exposes the concatenation of their verified bodies as a
/// plain byte stream, which is exactly Apple's reassembly rule. No byte is released before its
/// record's integrity trailer has checked out, and any failure is terminal.
/// </summary>
internal sealed class RecordReaderStream : Stream
{
    private readonly Stream _inner;
    private readonly RecordCipher _cipher;
    private readonly byte[] _record = new byte[AppleRecordLayer.MaxCiphertext];
    private byte[] _pending;   // ciphertext bytes the cleartext reader had already buffered
    private int _pendingPos;
    private int _bodyStart;
    private int _bodyEnd;
    private bool _failed;

    public RecordReaderStream(Stream inner, byte[] key, byte[] iv, byte[] alreadyBuffered)
    {
        _inner = inner;
        _cipher = new RecordCipher(key, iv);
        _pending = alreadyBuffered;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_bodyStart == _bodyEnd)
        {
            if (_failed) throw new Rfb.ProtocolException("The encrypted session already failed.");
            if (!await NextRecordAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
        int n = Math.Min(buffer.Length, _bodyEnd - _bodyStart);
        _record.AsMemory(_bodyStart, n).CopyTo(buffer);
        _bodyStart += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async ValueTask<bool> NextRecordAsync(CancellationToken ct)
    {
        try
        {
            if (!await FillAsync(2, ct, allowCleanEnd: true).ConfigureAwait(false))
            {
                return false;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(_record);
            if (length < AppleRecordLayer.MinCiphertext || length % AppleRecordLayer.Block != 0)
            {
                throw new Rfb.ProtocolException($"An encrypted record claims {length} bytes, which is not a valid record length.");
            }
            await FillAsync(length, ct, allowCleanEnd: false).ConfigureAwait(false);

            var plaintext = _record.AsSpan(0, length);
            _cipher.Decrypt(plaintext);
            int covered = length - AppleRecordLayer.Trailer;
            Span<byte> expected = stackalloc byte[AppleRecordLayer.Trailer];
            _cipher.Trailer(plaintext[..covered], expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, plaintext[covered..]))
            {
                throw new Rfb.ProtocolException("An encrypted record failed its integrity check. The connection may have been tampered with.");
            }
            int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext);
            if (AppleRecordLayer.BodyLengthField + bodyLength + AppleRecordLayer.Trailer > length)
            {
                throw new Rfb.ProtocolException("An encrypted record's body overruns the record.");
            }
            _bodyStart = AppleRecordLayer.BodyLengthField;
            _bodyEnd = AppleRecordLayer.BodyLengthField + bodyLength;
            return true;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes into the start of the record buffer.</summary>
    private async ValueTask<bool> FillAsync(int count, CancellationToken ct, bool allowCleanEnd)
    {
        int filled = 0;
        while (filled < count && _pendingPos < _pending.Length)
        {
            int n = Math.Min(count - filled, _pending.Length - _pendingPos);
            _pending.AsSpan(_pendingPos, n).CopyTo(_record.AsSpan(filled));
            _pendingPos += n;
            filled += n;
        }
        if (_pendingPos == _pending.Length && _pending.Length > 0)
        {
            _pending = [];
            _pendingPos = 0;
        }
        while (filled < count)
        {
            int n = await _inner.ReadAsync(_record.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                if (filled == 0 && allowCleanEnd) return false;
                throw new Rfb.ConnectionClosedException();
            }
            filled += n;
        }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cipher.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
