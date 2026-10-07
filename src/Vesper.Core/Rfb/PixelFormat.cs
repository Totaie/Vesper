using System.Buffers.Binary;

namespace Vesper.Core.Rfb;

/// <summary>RFB PIXEL_FORMAT (RFC 6143 §7.4).</summary>
public readonly record struct PixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColour,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    /// <summary>
    /// What Vesper always asks for: 32bpp little-endian xRGB. In memory that is B,G,R,x per pixel,
    /// which is exactly DXGI_FORMAT_B8G8R8A8, so decoded pixels go to the GPU without conversion.
    /// </summary>
    public static PixelFormat Bgrx32 { get; } = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    public int BytesPerPixel => BitsPerPixel / 8;

    /// <summary>
    /// ZRLE's CPIXEL: 3 bytes when a 32bpp true-colour pixel's colour bits fit in its low 24 bits.
    /// </summary>
    public int CompactBytesPerPixel =>
        BitsPerPixel == 32 && Depth <= 24 && TrueColour && FitsIn24Bits ? 3 : BytesPerPixel;

    private bool FitsIn24Bits =>
        ((uint)RedMax << RedShift | (uint)GreenMax << GreenShift | (uint)BlueMax << BlueShift) <= 0xFFFFFF;

    public static PixelFormat Parse(ReadOnlySpan<byte> s) => new(
        s[0], s[1], s[2] != 0, s[3] != 0,
        BinaryPrimitives.ReadUInt16BigEndian(s[4..]),
        BinaryPrimitives.ReadUInt16BigEndian(s[6..]),
        BinaryPrimitives.ReadUInt16BigEndian(s[8..]),
        s[10], s[11], s[12]);

    public void Write(Span<byte> d)
    {
        d[0] = BitsPerPixel;
        d[1] = Depth;
        d[2] = (byte)(BigEndian ? 1 : 0);
        d[3] = (byte)(TrueColour ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(d[4..], RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(d[6..], GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(d[8..], BlueMax);
        d[10] = RedShift;
        d[11] = GreenShift;
        d[12] = BlueShift;
        d[13] = d[14] = d[15] = 0;
    }

    public override string ToString() =>
        $"{BitsPerPixel}bpp depth {Depth} {(BigEndian ? "BE" : "LE")} {(TrueColour ? "true colour" : "palette")} " +
        $"R{RedMax}<<{RedShift} G{GreenMax}<<{GreenShift} B{BlueMax}<<{BlueShift}";
}
