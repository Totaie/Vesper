using System.Buffers.Binary;

namespace Vesper.Core.Rfb;

/// <summary>
/// Apple's RFB 003.889 extension messages and encodings. Byte layouts follow remotex's measured
/// reference (MIT, docs/apple-vnc-889.md); see docs/PROTOCOL.md in this repository.
/// </summary>
internal static class AppleMessages
{
    // Client → server message types.
    public const byte SetServerScaling = 0x08;
    public const byte AutoFrameBufferUpdate = 0x09;
    public const byte SetMode = 0x0a;
    public const byte PasteboardFetch = 0x0b;
    public const byte SetDisplay = 0x0d;
    public const byte EncryptedInputEvent = 0x10;
    public const byte SetEncryption = 0x12;
    public const byte AutoPasteboard = 0x15;
    public const byte Event2 = 0x17;
    public const byte SetDisplayConfiguration = 0x1d;
    public const byte PasteboardData = 0x1f;
    public const byte ViewerInfo = 0x21;

    // Server → client message types beyond RFC 6143's 0–3.
    public const byte MiscStatus = 0x14;
    // PasteboardData (0x1f) is also the server's reply type.

    // Encodings.
    public const int EncodingOneBitZlib = 0x3e8;
    public const int EncodingGrayZlib = 0x3e9;
    public const int EncodingRgb555Zlib = 0x3ea;

    /// <summary>Apple's adaptive codec (MVS): 8×8 tiles, DCT-coded and refined progressively.</summary>
    public const int EncodingMvs = 0x3f3;
    public const int EncodingCursorPos = 0x44c;
    public const int EncodingDisplayInfo = 0x44d;
    public const int EncodingUserInfo = 0x44e;
    public const int EncodingRekey = 0x44f;
    public const int EncodingCursorImage = 0x450;
    public const int EncodingDisplayLayout = 0x451;
    public const int EncodingVendorKeysyms = 0x453;
    public const int EncodingKeyboardSource = 0x455;
    public const int EncodingDeviceInfo = 0x456;

    /// <summary>MiscStatus commands the session acts on.</summary>
    public const ushort StatusSessionEnded = 1, StatusPasteboardChanged = 2, StatusPasteboardRequested = 3,
        StatusDisplaysAsleep = 5, StatusDisplaysAwake = 6;

    /// <summary>
    /// ViewerInfo (0x21): 66 bytes of numbers. A mis-sized body makes the Mac swallow the next
    /// message, so this is the measured form verbatim: application class 1, id 2, version 6.1.0,
    /// OS 26.6.2, and the 32-byte bitmap of server messages this viewer handles.
    /// </summary>
    public static byte[] ViewerInfoMessage()
    {
        var m = new byte[66];
        m[0] = ViewerInfo;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), 62);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(10), 6);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(22), 26);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(26), 6);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(30), 2);
        m[34] = 0xb0;
        m[36] = 0x0c;
        m[37] = 0x03;
        m[38] = 0x90;
        m[44] = 0x40;
        return m;
    }

    /// <summary>SetMode (0x0a): 0 observe, 1 control.</summary>
    public static byte[] SetModeMessage(bool control) => [SetMode, 0, 0, (byte)(control ? 1 : 0)];

    /// <summary>AutoPasteboard (0x15): 1 starts watching the Mac's pasteboard, 2 stops.</summary>
    public static byte[] AutoPasteboardMessage(bool start) => [AutoPasteboard, 0, 0, (byte)(start ? 1 : 2), 0, 0, 0, 0];

    /// <summary>SetEncryption command 1: propose AES-128 (method 1); the Mac answers with a rekey.</summary>
    public static byte[] SetEncryptionStart() => [SetEncryption, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1];

    /// <summary>SetEncryption command 2 with 1: the Mac decrypts everything it receives from now on.</summary>
    public static byte[] SetEncryptionInbound() => [SetEncryption, 0, 0, 2, 0, 1, 0, 0];

    /// <summary>
    /// AutoFrameBufferUpdate (0x09): the Mac pushes changes in the armed region no faster than one
    /// per <paramref name="intervalMicroseconds"/>. Without it a physical display answers
    /// incremental requests late, which is the lag plain VNC clients see.
    /// </summary>
    public static byte[] AutoFrameBufferUpdateMessage(uint intervalMicroseconds, int width, int height)
    {
        var m = new byte[16];
        m[0] = AutoFrameBufferUpdate;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), intervalMicroseconds);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(14), (ushort)height);
        return m;
    }

    /// <summary>SetServerScaling (0x08): the Mac renders at <paramref name="factor"/> in (0, 1] before encoding.</summary>
    public static byte[] SetServerScalingMessage(double factor)
    {
        if (!(factor > 0 && factor <= 1)) throw new ArgumentOutOfRangeException(nameof(factor));
        var m = new byte[10];
        m[0] = SetServerScaling;
        BinaryPrimitives.WriteDoubleBigEndian(m.AsSpan(2), factor);
        return m;
    }

    /// <summary>SetDisplay (0x0d): share one display by id, or all of them combined.</summary>
    public static byte[] SetDisplayMessage(uint? displayId)
    {
        var m = new byte[8];
        m[0] = SetDisplay;
        m[1] = (byte)(displayId is null ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), displayId ?? 0);
        return m;
    }

    /// <summary>
    /// The scroll-wheel event (0x17, kind 11): a precise scroll by a distance in points, which
    /// macOS applications receive as a continuous (trackpad-like) scroll. Positive dx/dy scroll
    /// content right/down as Windows counts them; the Mac counts up/left as positive.
    /// </summary>
    public static byte[] ScrollWheelMessage(int dxPoints, int dyPoints, int x, int y)
    {
        const int PointsPerLine = 10;
        const uint Continuous = 0x02;
        Span<int> axes = [-dxPoints, -dyPoints, 0];
        var m = new byte[58];
        var s = m.AsSpan();
        s[0] = Event2;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 54);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 11);
        int at = 8;
        foreach (int p in axes) { BinaryPrimitives.WriteInt16BigEndian(s[at..], (short)(p / PointsPerLine)); at += 2; }
        foreach (int p in axes) { BinaryPrimitives.WriteInt32BigEndian(s[at..], (int)((long)p * 65536 / PointsPerLine)); at += 4; }
        foreach (int p in axes) { BinaryPrimitives.WriteInt32BigEndian(s[at..], p); at += 4; }
        at += 12; // scroll phase, momentum phase, scroll count
        BinaryPrimitives.WriteUInt32BigEndian(s[at..], Continuous);
        at += 4;
        BinaryPrimitives.WriteUInt16BigEndian(s[at..], (ushort)Math.Clamp(x, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(s[(at + 2)..], (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        return m;
    }

    /// <summary>Pasteboard fetch (0x0b): ask for the Mac's pasteboard; the id is echoed back.</summary>
    public static byte[] PasteboardFetchMessage(uint requestId)
    {
        var m = new byte[8];
        m[0] = PasteboardFetch;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), requestId);
        return m;
    }

    /// <summary>Whether the extended ServerInit's command bitmap lists client message <paramref name="type"/>.</summary>
    public static bool Accepts(ReadOnlySpan<byte> commandBitmap, byte type) =>
        type / 8 < commandBitmap.Length && ((commandBitmap[type / 8] >> (7 - type % 8)) & 1) == 1;
}
