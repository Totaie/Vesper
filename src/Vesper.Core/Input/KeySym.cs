namespace Vesper.Core.Input;

/// <summary>The X11 keysyms RFB key events carry (RFC 6143 §7.5.4).</summary>
public static class KeySym
{
    public const uint BackSpace = 0xFF08, Tab = 0xFF09, Return = 0xFF0D, Pause = 0xFF13, ScrollLock = 0xFF14,
        Escape = 0xFF1B, Delete = 0xFFFF, Home = 0xFF50, Left = 0xFF51, Up = 0xFF52, Right = 0xFF53, Down = 0xFF54,
        PageUp = 0xFF55, PageDown = 0xFF56, End = 0xFF57, Print = 0xFF61, Insert = 0xFF63, Menu = 0xFF67,
        Help = 0xFF6A, NumLock = 0xFF7F,
        KpEnter = 0xFF8D, KpMultiply = 0xFFAA, KpAdd = 0xFFAB, KpSeparator = 0xFFAC, KpSubtract = 0xFFAD,
        KpDecimal = 0xFFAE, KpDivide = 0xFFAF, Kp0 = 0xFFB0, KpEqual = 0xFFBD,
        F1 = 0xFFBE,
        ShiftL = 0xFFE1, ShiftR = 0xFFE2, ControlL = 0xFFE3, ControlR = 0xFFE4, CapsLock = 0xFFE5,
        MetaL = 0xFFE7, MetaR = 0xFFE8, AltL = 0xFFE9, AltR = 0xFFEA, SuperL = 0xFFEB, SuperR = 0xFFEC,
        Space = 0x0020;

    /// <summary>
    /// Keysym for a Unicode character: Latin-1 maps directly, everything else uses the
    /// 0x01000000 + code point form that X11 (and Apple's server) accept.
    /// </summary>
    public static uint FromCodePoint(int codePoint) => codePoint switch
    {
        '\b' => BackSpace,
        '\t' => Tab,
        '\r' or '\n' => Return,
        0x1B => Escape,
        0x7F => Delete,
        >= 0x20 and <= 0x7E => (uint)codePoint,
        >= 0xA0 and <= 0xFF => (uint)codePoint,
        _ => 0x01000000u + (uint)codePoint,
    };

    public static bool IsModifier(uint keysym) => keysym is >= ShiftL and <= SuperR;
}
