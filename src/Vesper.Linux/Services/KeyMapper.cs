using System.Globalization;
using Avalonia.Input;
using Vesper.Core.Input;
using Vesper.Models;

namespace Vesper.Linux.Services;

/// <summary>
/// Turns key events into the X11 keysyms a Mac expects.
///
/// Modifiers: macOS's Screen Sharing agent reads Super keysyms as Command and Meta as Option. In
/// the Mac-style layout Ctrl sends Command, Alt sends Option and the Super key sends Control, so
/// PC muscle memory (Ctrl+C, Ctrl+Tab, Ctrl+Q) does the Mac equivalent. As labelled, Ctrl is
/// Control, Alt is Option and Super is Command.
///
/// Characters are what the keyboard layout typed (the event's key symbol), except under Ctrl or
/// Super, where the key's unshifted US character is sent: shortcuts then work under any layout or
/// modifier, and the Mac infers no Shift from an uppercase keysym (Command-Z under Caps Lock
/// would otherwise arrive as Command-Shift-Z). Some Linux sessions deliver key events without
/// the typed character, or with the unshifted one while Shift is held; then the key's shifted US
/// character is sent, because the Mac doesn't apply Shift to a digit or punctuation keysym itself
/// (Shift+1 would type "1", not "!"). A key's release always repeats the keysym its press sent.
/// </summary>
internal sealed class KeyMapper
{
    private readonly Dictionary<int, uint> _pressed = [];
    private bool _lshift, _rshift, _lctrl, _rctrl, _lalt, _ralt, _lsuper, _rsuper;

    public ModifierLayout Layout { get; set; } = ModifierLayout.MacStyle;

    public bool CtrlDown => _lctrl || _rctrl;
    public bool AltDown => _lalt || _ralt;
    public bool SuperDown => _lsuper || _rsuper;
    public bool ShiftDown => _lshift || _rshift;

    /// <summary>Translates one key event; appends the keysym events to send.</summary>
    public void Translate(PhysicalKey physical, Key key, string? symbol, bool down, List<(uint Keysym, bool Down)> output)
    {
        if (physical == PhysicalKey.None) physical = FromKey(key);
        int id = physical != PhysicalKey.None ? (int)physical : 0x10000 + (int)key;
        TrackModifier(physical, down);

        if (!down)
        {
            if (_pressed.Remove(id, out uint sent)) output.Add((sent, false));
            return;
        }
        if (_pressed.TryGetValue(id, out uint held))
        {
            output.Add((held, true)); // a repeat sends what the press sent
            return;
        }

        uint keysym = MapModifier(physical) ?? MapSpecial(physical) ?? MapCharacter(physical, symbol);
        if (keysym == 0) return;
        _pressed[id] = keysym;
        output.Add((keysym, true));
    }

    /// <summary>Releases every key the Mac still believes is held (on focus loss, or disconnect).</summary>
    public void ReleaseAll(List<(uint Keysym, bool Down)> output)
    {
        foreach (uint keysym in _pressed.Values) output.Add((keysym, false));
        _pressed.Clear();
        _lshift = _rshift = _lctrl = _rctrl = _lalt = _ralt = _lsuper = _rsuper = false;
    }

    /// <summary>The keysym a modifier key sends under the current layout.</summary>
    public uint? MapModifier(PhysicalKey key) => (key, Layout) switch
    {
        (PhysicalKey.ShiftLeft, _) => KeySym.ShiftL,
        (PhysicalKey.ShiftRight, _) => KeySym.ShiftR,
        (PhysicalKey.ControlLeft, ModifierLayout.MacStyle) => KeySym.SuperL,   // Command
        (PhysicalKey.ControlRight, ModifierLayout.MacStyle) => KeySym.SuperR,
        (PhysicalKey.ControlLeft, _) => KeySym.ControlL,
        (PhysicalKey.ControlRight, _) => KeySym.ControlR,
        (PhysicalKey.AltLeft, _) => KeySym.MetaL,                               // Option
        (PhysicalKey.AltRight, _) => KeySym.MetaR,
        (PhysicalKey.MetaLeft, ModifierLayout.MacStyle) => KeySym.ControlL,    // the Super key
        (PhysicalKey.MetaRight, ModifierLayout.MacStyle) => KeySym.ControlR,
        (PhysicalKey.MetaLeft, _) => KeySym.SuperL,
        (PhysicalKey.MetaRight, _) => KeySym.SuperR,
        (PhysicalKey.CapsLock, _) => KeySym.CapsLock,
        _ => null,
    };

    private static uint? MapSpecial(PhysicalKey key) => key switch
    {
        PhysicalKey.Backspace => KeySym.BackSpace,
        PhysicalKey.Tab => KeySym.Tab,
        PhysicalKey.Enter => KeySym.Return,
        PhysicalKey.NumPadEnter => KeySym.KpEnter,
        PhysicalKey.Escape => KeySym.Escape,
        PhysicalKey.Space => KeySym.Space,
        PhysicalKey.PageUp => KeySym.PageUp,
        PhysicalKey.PageDown => KeySym.PageDown,
        PhysicalKey.End => KeySym.End,
        PhysicalKey.Home => KeySym.Home,
        PhysicalKey.ArrowLeft => KeySym.Left,
        PhysicalKey.ArrowUp => KeySym.Up,
        PhysicalKey.ArrowRight => KeySym.Right,
        PhysicalKey.ArrowDown => KeySym.Down,
        PhysicalKey.PrintScreen => KeySym.F1 + 12,   // F13, where Apple's extended keyboard has it
        PhysicalKey.ScrollLock => KeySym.F1 + 13,    // F14
        PhysicalKey.Pause => KeySym.F1 + 14,         // F15
        PhysicalKey.Insert or PhysicalKey.Help => KeySym.Help, // Insert sits where the Mac's Help key was
        PhysicalKey.Delete => KeySym.Delete,
        >= PhysicalKey.F1 and <= PhysicalKey.F24 => KeySym.F1 + (uint)(key - PhysicalKey.F1),
        >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9 => KeySym.Kp0 + (uint)(key - PhysicalKey.NumPad0),
        PhysicalKey.NumPadMultiply => KeySym.KpMultiply,
        PhysicalKey.NumPadAdd => KeySym.KpAdd,
        PhysicalKey.NumPadComma => KeySym.KpSeparator,
        PhysicalKey.NumPadSubtract => KeySym.KpSubtract,
        PhysicalKey.NumPadDecimal => KeySym.KpDecimal,
        PhysicalKey.NumPadDivide => KeySym.KpDivide,
        PhysicalKey.NumPadEqual => KeySym.KpEqual,
        PhysicalKey.NumLock or PhysicalKey.NumPadClear => KeySym.NumLock, // arrives on the Mac as keypad Clear
        _ => null,
    };

    private uint MapCharacter(PhysicalKey key, string? symbol)
    {
        // Ctrl and Super are Command and Control in either layout: send the key itself.
        bool shortcut = CtrlDown || SuperDown;
        char? plain = UsCharacter(key);
        if (!shortcut && SingleCharacter(symbol) is { } typed)
        {
            // An unshifted symbol under Shift is the event leaving Shift out, not the layout.
            bool shiftMissing = ShiftDown && plain is { } p && typed == p && UsShifted(key) is not null;
            if (!shiftMissing) return KeySym.FromCodePoint(typed);
        }
        if (!shortcut && ShiftDown && UsShifted(key) is { } shifted) return KeySym.FromCodePoint(shifted);
        return plain is { } c ? KeySym.FromCodePoint(c) : 0;
    }

    /// <summary>The code point when <paramref name="symbol"/> is exactly one printable character.</summary>
    private static int? SingleCharacter(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 2) return null;
        int codePoint;
        if (symbol.Length == 2)
        {
            if (!char.IsSurrogatePair(symbol[0], symbol[1])) return null;
            codePoint = char.ConvertToUtf32(symbol[0], symbol[1]);
        }
        else
        {
            codePoint = symbol[0];
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) return null;
        return codePoint;
    }

    /// <summary>What the key types on a US keyboard, unshifted.</summary>
    private static char? UsCharacter(PhysicalKey key) => key switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => (char)('a' + (key - PhysicalKey.A)),
        >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => (char)('0' + (key - PhysicalKey.Digit0)),
        PhysicalKey.Minus => '-',
        PhysicalKey.Equal => '=',
        PhysicalKey.BracketLeft => '[',
        PhysicalKey.BracketRight => ']',
        PhysicalKey.Backslash => '\\',
        PhysicalKey.Semicolon => ';',
        PhysicalKey.Quote => '\'',
        PhysicalKey.Backquote => '`',
        PhysicalKey.Comma => ',',
        PhysicalKey.Period => '.',
        PhysicalKey.Slash => '/',
        PhysicalKey.IntlBackslash => '<',
        _ => null,
    };

    /// <summary>What the key types on a US keyboard with Shift held (letters are left to the Mac's Shift).</summary>
    private static char? UsShifted(PhysicalKey key) => key switch
    {
        PhysicalKey.Digit1 => '!',
        PhysicalKey.Digit2 => '@',
        PhysicalKey.Digit3 => '#',
        PhysicalKey.Digit4 => '$',
        PhysicalKey.Digit5 => '%',
        PhysicalKey.Digit6 => '^',
        PhysicalKey.Digit7 => '&',
        PhysicalKey.Digit8 => '*',
        PhysicalKey.Digit9 => '(',
        PhysicalKey.Digit0 => ')',
        PhysicalKey.Minus => '_',
        PhysicalKey.Equal => '+',
        PhysicalKey.BracketLeft => '{',
        PhysicalKey.BracketRight => '}',
        PhysicalKey.Backslash => '|',
        PhysicalKey.Semicolon => ':',
        PhysicalKey.Quote => '"',
        PhysicalKey.Backquote => '~',
        PhysicalKey.Comma => '<',
        PhysicalKey.Period => '>',
        PhysicalKey.Slash => '?',
        PhysicalKey.IntlBackslash => '>',
        _ => null,
    };

    /// <summary>For events without a physical key, the logical key's best guess.</summary>
    private static PhysicalKey FromKey(Key key) => key switch
    {
        Key.LeftShift => PhysicalKey.ShiftLeft,
        Key.RightShift => PhysicalKey.ShiftRight,
        Key.LeftCtrl => PhysicalKey.ControlLeft,
        Key.RightCtrl => PhysicalKey.ControlRight,
        Key.LeftAlt => PhysicalKey.AltLeft,
        Key.RightAlt => PhysicalKey.AltRight,
        Key.LWin => PhysicalKey.MetaLeft,
        Key.RWin => PhysicalKey.MetaRight,
        Key.CapsLock => PhysicalKey.CapsLock,
        Key.Back => PhysicalKey.Backspace,
        Key.Tab => PhysicalKey.Tab,
        Key.Enter => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        Key.Space => PhysicalKey.Space,
        Key.PageUp => PhysicalKey.PageUp,
        Key.PageDown => PhysicalKey.PageDown,
        Key.End => PhysicalKey.End,
        Key.Home => PhysicalKey.Home,
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Down => PhysicalKey.ArrowDown,
        Key.Insert => PhysicalKey.Insert,
        Key.Delete => PhysicalKey.Delete,
        >= Key.A and <= Key.Z => PhysicalKey.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => PhysicalKey.Digit0 + (key - Key.D0),
        >= Key.F1 and <= Key.F24 => PhysicalKey.F1 + (key - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => PhysicalKey.NumPad0 + (key - Key.NumPad0),
        _ => PhysicalKey.None,
    };

    private void TrackModifier(PhysicalKey key, bool down)
    {
        switch (key)
        {
            case PhysicalKey.ShiftLeft: _lshift = down; break;
            case PhysicalKey.ShiftRight: _rshift = down; break;
            case PhysicalKey.ControlLeft: _lctrl = down; break;
            case PhysicalKey.ControlRight: _rctrl = down; break;
            case PhysicalKey.AltLeft: _lalt = down; break;
            case PhysicalKey.AltRight: _ralt = down; break;
            case PhysicalKey.MetaLeft: _lsuper = down; break;
            case PhysicalKey.MetaRight: _rsuper = down; break;
        }
    }
}
