using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Vesper.Core.Rfb;

/// <summary>
/// macOS doesn't bridge its pasteboard through RFB cut-text; Apple's viewers exchange a zlib
/// "archive" of pasteboard items instead (message 0x1f both ways). Each item is a u32 flavor count,
/// and each flavor a counted name, a reserved u32, counted key/value tags, and counted data, all
/// counts big-endian u32. Vesper only ever reads or writes the UTF-8 text flavor.
/// </summary>
internal static class ApplePasteboard
{
    public const string TextFlavor = "public.utf8-plain-text";

    /// <summary>The most text Vesper moves in either direction.</summary>
    public const int MaxTextBytes = 4 << 20;

    /// <summary>Archives above this are skipped unread (they are images, files, rich documents).</summary>
    public const uint MaxArchiveBytes = 64 << 20;

    /// <summary>Builds a full 0x1f message putting <paramref name="text"/> on the Mac's pasteboard.</summary>
    public static byte[] BuildSendMessage(string text, uint sessionId)
    {
        byte[] archive = BuildArchive(text);
        byte[] compressed = Deflate(archive);
        var m = new byte[16 + compressed.Length];
        m[0] = AppleMessages.PasteboardData;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(8), (uint)archive.Length);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(12), (uint)compressed.Length);
        compressed.CopyTo(m.AsSpan(16));
        return m;
    }

    internal static byte[] BuildArchive(string text)
    {
        // An item with no flavors clears the Mac's pasteboard; an empty text flavor would instead
        // read as a promise the Mac then waits on.
        if (text.Length == 0)
        {
            return [0, 0, 0, 0];
        }
        byte[] name = Encoding.ASCII.GetBytes(TextFlavor);
        byte[] data = Encoding.UTF8.GetBytes(text);
        if (data.Length > MaxTextBytes)
        {
            throw new InvalidOperationException("The clipboard text is too large to send.");
        }
        var a = new byte[4 + 4 + name.Length + 4 + 4 + 4 + data.Length];
        var s = a.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 1);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)name.Length);
        name.CopyTo(s[8..]);
        int at = 8 + name.Length;
        at += 4; // reserved
        at += 4; // tag count 0
        BinaryPrimitives.WriteUInt32BigEndian(s[at..], (uint)data.Length);
        data.CopyTo(s[(at + 4)..]);
        return a;
    }

    /// <summary>zlib, sync-flushed and left open, as Apple's agent writes it.</summary>
    private static byte[] Deflate(byte[] archive)
    {
        using var output = new MemoryStream();
        var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        zlib.Write(archive);
        zlib.Flush();
        byte[] bytes = output.ToArray();
        zlib.Dispose();
        return bytes;
    }

    /// <summary>
    /// Reads the text flavor out of a compressed archive. Returns null when the pasteboard holds no
    /// text, or "" when the archive is empty (the Mac's pasteboard was cleared).
    /// </summary>
    public static string? ExtractText(ReadOnlySpan<byte> compressed, uint declaredUncompressed)
    {
        if (declaredUncompressed == 0) return "";
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var reader = new BoundedReader(zlib, declaredUncompressed);
        try
        {
            while (reader.Remaining > 0)
            {
                uint flavors = reader.U32();
                for (uint f = 0; f < flavors; f++)
                {
                    uint nameLength = reader.U32();
                    if (nameLength > 1024) throw new ProtocolException("Pasteboard flavor name too long.");
                    string name = Encoding.UTF8.GetString(reader.Bytes((int)nameLength));
                    reader.U32(); // reserved
                    uint tags = reader.U32();
                    if (tags > 64) throw new ProtocolException("Pasteboard flavor has too many tags.");
                    for (uint t = 0; t < tags * 2; t++)
                    {
                        uint length = reader.U32();
                        if (length > 4096) throw new ProtocolException("Pasteboard tag too long.");
                        reader.Skip(length);
                    }
                    uint dataLength = reader.U32();
                    if (name == TextFlavor)
                    {
                        if (dataLength > MaxTextBytes) return null;
                        return Encoding.UTF8.GetString(reader.Bytes((int)dataLength));
                    }
                    reader.Skip(dataLength);
                }
            }
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new ProtocolException("The Mac sent a corrupt pasteboard archive.");
        }
    }

    /// <summary>Sequential big-endian reads that refuse to go past the declared archive size.</summary>
    private sealed class BoundedReader(Stream stream, uint limit)
    {
        private readonly byte[] _scratch = new byte[64 * 1024];
        public long Remaining { get; private set; } = limit;

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));

        public byte[] Bytes(int count)
        {
            Take(count);
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public void Skip(uint count)
        {
            Take(count);
            while (count > 0)
            {
                int n = (int)Math.Min(count, (uint)_scratch.Length);
                stream.ReadExactly(_scratch, 0, n);
                count -= (uint)n;
            }
        }

        private void Take(long count)
        {
            if (count > Remaining) throw new ProtocolException("Pasteboard archive overruns its declared size.");
            Remaining -= count;
        }
    }
}
