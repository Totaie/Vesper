using System.Buffers.Binary;

namespace Vesper.Core.Rfb;

/// <summary>One of the Mac's screens, from an AppleDisplayLayout (0x451) record.</summary>
public sealed record AppleDisplay(
    uint Id,
    string Label,
    bool IsMain,
    double Density,
    double ServerScale,
    int PointsWidth,
    int PointsHeight,
    int PixelX,
    int PixelY,
    int PixelWidth,
    int PixelHeight);

/// <summary>
/// The Mac's display layout: its screens, which one is being sent (null = all combined), and the
/// framebuffer size that follows. Arrives at connect and on every login, lock, switch or resize.
/// Layout from remotex's measurements (docs/apple-vnc-889.md, "Displays").
/// </summary>
public sealed record AppleDisplayLayout(int FramebufferWidth, int FramebufferHeight, uint? CurrentDisplay, IReadOnlyList<AppleDisplay> Displays)
{
    private const int Head = 0x14;
    private const int Record = 0x38;
    private const int MaxDisplays = 25;

    /// <summary>The server-side scale Apple applied (the same in every record).</summary>
    public double ServerScale => Displays.Count > 0 ? Displays[0].ServerScale : 1.0;

    /// <summary>Pixels per point of what is being sent, after server scaling.</summary>
    public double EffectiveDensity
    {
        get
        {
            var shown = CurrentDisplay is uint id ? Displays.Where(d => d.Id == id) : Displays;
            return shown.Select(d => d.Density * d.ServerScale).DefaultIfEmpty(1.0).Max();
        }
    }

    /// <summary>The densest native density among the screens being sent (2.0 on a Retina display).</summary>
    public double NativeDensity
    {
        get
        {
            var shown = CurrentDisplay is uint id ? Displays.Where(d => d.Id == id) : Displays;
            return shown.Select(d => d.Density).DefaultIfEmpty(1.0).Max();
        }
    }

    /// <summary>Parses the payload after the rectangle's u16 length (which counts what follows it).</summary>
    public static AppleDisplayLayout Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < Head)
        {
            throw new ProtocolException("The Mac's display layout is too short.");
        }
        int count = BinaryPrimitives.ReadUInt16BigEndian(p[0x12..]);
        if (count is < 1 or > MaxDisplays || p.Length < Head + count * Record)
        {
            throw new ProtocolException($"The Mac's display layout lists {count} displays in {p.Length} bytes.");
        }
        uint current = BinaryPrimitives.ReadUInt32BigEndian(p[0x0a..]);
        int fbWidth = BinaryPrimitives.ReadUInt16BigEndian(p[0x06..]);
        int fbHeight = BinaryPrimitives.ReadUInt16BigEndian(p[0x08..]);
        if (fbWidth == 0 || fbHeight == 0)
        {
            throw new ProtocolException("The Mac's display layout gives an empty framebuffer.");
        }

        var displays = new List<AppleDisplay>();
        var mirrorOrigins = new HashSet<(int, int)>();
        for (int i = 0; i < count; i++)
        {
            var r = p.Slice(Head + i * Record, Record);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(r[0x24..]);
            var (lw, lh, lx, ly) = Edges(r[0x14..]);
            var (bw, bh, bx, by) = Edges(r[0x1c..]);
            // Members of a mirror set share an origin; offer the first one only.
            if ((flags & 0x02) != 0 && !mirrorOrigins.Add((lx, ly)))
            {
                continue;
            }
            if (lw == 0 || lh == 0) continue;

            double density = BinaryPrimitives.ReadDoubleBigEndian(r);
            double scale = BinaryPrimitives.ReadDoubleBigEndian(r[0x08..]);
            if (!double.IsFinite(scale) || scale <= 0 || scale > 1) scale = 1.0;
            if (density == 0.0) density = (double)bw / lw / scale;
            if (!double.IsFinite(density) || density < 1 || density > 4) continue;

            displays.Add(new AppleDisplay(
                Id: BinaryPrimitives.ReadUInt32BigEndian(r[0x10..]),
                Label: $"Display {displays.Count + 1}",
                IsMain: (flags & 0x01) != 0,
                Density: density,
                ServerScale: scale,
                PointsWidth: lw, PointsHeight: lh,
                PixelX: bx, PixelY: by, PixelWidth: bw, PixelHeight: bh));
        }
        if (displays.Count == 0)
        {
            throw new ProtocolException("The Mac's display layout lists no usable display.");
        }
        return new AppleDisplayLayout(fbWidth, fbHeight, current == uint.MaxValue ? null : current, displays);

        // Rects are sent as (top, left, bottom, right) signed edges.
        static (int W, int H, int X, int Y) Edges(ReadOnlySpan<byte> e)
        {
            short top = BinaryPrimitives.ReadInt16BigEndian(e);
            short left = BinaryPrimitives.ReadInt16BigEndian(e[2..]);
            short bottom = BinaryPrimitives.ReadInt16BigEndian(e[4..]);
            short right = BinaryPrimitives.ReadInt16BigEndian(e[6..]);
            return (Math.Max(0, right - left), Math.Max(0, bottom - top), left, top);
        }
    }
}
