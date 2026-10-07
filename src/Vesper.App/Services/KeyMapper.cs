using Vesper.Core.Input;
using Vesper.Interop;
using Vesper.Models;

namespace Vesper.Services;

/// <summary>
/// Turns physical Windows key events into the X11 keysyms a Mac expects.
///
/// Modifiers: macOS's Screen Sharing agent reads Super/Alt keysyms as Command and Meta as Option.
/// In the Mac-style layout Ctrl sends Command, Alt sends Option and the Windows key sends
/// Control, so Windows muscle memory (Ctrl+C, Ctrl+Tab, Ctrl+Q) does the Mac equivalent.
///
/// Characters come from the active Windows keyboard layout. Under Command or Control the
/// unshifted, un-Caps-Locked keysym is sent, since the Mac infers Shift from an uppercase keysym
/// (Command-Z under Caps Lock would otherwise arrive as Command-Shift-Z). A key's release always
/// repeats the keysym its press sent.
/// </summary>
internal sealed unsafe class KeyMapper
{
    private const uint VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14,
        VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5,
        VkLWin = 0x5B, VkRWin = 0x5C;

    private readonly Dictionary<uint, uint> _pressed = [];
    private bool _lshift, _rshift, _lctrl, _rctrl, _lalt, _ralt, _lwin, _rwin;

    public ModifierLayout Layout { get; set; } = ModifierLayout.MacStyle;

    /// <summary>
    /// The Windows key only reaches the Mac while shortcuts are captured; otherwise Windows owns it
    /// (and would open Start on release anyway).
    /// </summary>
    public bool SendWindowsKey { get; set; }

    public bool CtrlDown => _lctrl || _rctrl;
    public bool AltDown => _lalt || _ralt;
    public bool WinDown => _lwin || _rwin;
    public bool ShiftDown => _lshift || _rshift;

    /// <summary>Translates one physical event; appends the keysym events to send.</summary>
    public void Translate(uint vk, uint scanCode, bool extended, bool down, List<(uint Keysym, bool Down)> output)
    {
        vk = Specific(vk, scanCode, extended);
        uint id = scanCode | (extended ? 0x10000u : 0) | (vk << 17);
        TrackModifier(vk, down);
        if (vk is VkLWin or VkRWin && !SendWindowsKey) return;

        if (!down)
        {
            if (_pressed.Remove(id, out uint sent)) output.Add((sent, false));
            return;
        }

        uint keysym = MapModifier(vk) ?? MapSpecial(vk, extended) ?? MapCharacter(vk, scanCode);
        if (keysym == 0) return;
        _pressed[id] = keysym; // a repeat keeps the first keysym
        output.Add((keysym, true));
    }

    /// <summary>Releases every key the Mac still believes is held (on focus loss, or disconnect).</summary>
    public void ReleaseAll(List<(uint Keysym, bool Down)> output)
    {
        foreach (uint keysym in _pressed.Values) output.Add((keysym, false));
        _pressed.Clear();
        _lshift = _rshift = _lctrl = _rctrl = _lalt = _ralt = _lwin = _rwin = false;
    }

    /// <summary>The keysym the given Windows modifier sends under the current layout.</summary>
    public uint? MapModifier(uint vk) => (vk, Layout) switch
    {
        (VkLShift, _) => KeySym.ShiftL,
        (VkRShift, _) => KeySym.ShiftR,
        (VkLControl, ModifierLayout.MacStyle) => KeySym.SuperL,
        (VkRControl, ModifierLayout.MacStyle) => KeySym.SuperR,
        (VkLControl, _) => KeySym.ControlL,
        (VkRControl, _) => KeySym.ControlR,
        (VkLMenu, _) => KeySym.MetaL,   // Option
        (VkRMenu, _) => KeySym.MetaR,
        (VkLWin, ModifierLayout.MacStyle) => KeySym.ControlL,
        (VkRWin, ModifierLayout.MacStyle) => KeySym.ControlR,
        (VkLWin, _) => KeySym.SuperL,   // Command
        (VkRWin, _) => KeySym.SuperR,
        (VkCapital, _) => KeySym.CapsLock,
        _ => null,
    };

    private static uint? MapSpecial(uint vk, bool extended) => vk switch
    {
        0x08 => KeySym.BackSpace,
        0x09 => KeySym.Tab,
        0x0D => extended ? KeySym.KpEnter : KeySym.Return,
        0x1B => KeySym.Escape,
        0x20 => KeySym.Space,
        0x21 => KeySym.PageUp,
        0x22 => KeySym.PageDown,
        0x23 => KeySym.End,
        0x24 => KeySym.Home,
        0x25 => KeySym.Left,
        0x26 => KeySym.Up,
        0x27 => KeySym.Right,
        0x28 => KeySym.Down,
        0x2C => KeySym.F1 + 12,   // Print Screen → F13, where Apple's extended keyboard has it
        0x91 => KeySym.F1 + 13,   // Scroll Lock → F14
        0x13 => KeySym.F1 + 14,   // Pause → F15
        0x2D => KeySym.Help,      // Insert sits where the Mac's Help key was
        0x2E => KeySym.Delete,
        >= 0x70 and <= 0x87 => KeySym.F1 + (vk - 0x70),
        >= 0x60 and <= 0x69 => KeySym.Kp0 + (vk - 0x60),
        0x6A => KeySym.KpMultiply,
        0x6B => KeySym.KpAdd,
        0x6C => KeySym.KpSeparator,
        0x6D => KeySym.KpSubtract,
        0x6E => KeySym.KpDecimal,
        0x6F => KeySym.KpDivide,
        0x90 => KeySym.NumLock,   // arrives on the Mac as keypad Clear
        _ => null,
    };

    private uint MapCharacter(uint vk, uint scanCode)
    {
        // Volume and media keys stay with Windows.
        if (vk is >= 0xA6 and <= 0xB7) return 0;

        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        // Ctrl and the Windows key are Command and Control in either layout.
        bool shortcut = CtrlDown || WinDown;
        if (ShiftDown) state[VkShift] = 0x80;
        if (!shortcut && (Native.GetKeyState((int)VkCapital) & 1) != 0) state[VkCapital] = 0x01;

        char* buffer = stackalloc char[4];
        nint layout = Native.GetKeyboardLayout(0);
        // Flag 4: don't touch the system's dead-key state.
        int n = Native.ToUnicodeEx(vk, scanCode, state, buffer, 4, 4, layout);
        if (n == 0) return 0;
        int count = Math.Abs(n);
        int codePoint = count >= 2 && char.IsHighSurrogate(buffer[0]) ? char.ConvertToUtf32(buffer[0], buffer[1]) : buffer[0];
        if (codePoint < 0x20) return 0;
        return KeySym.FromCodePoint(codePoint);
    }

    /// <summary>Generic VK_SHIFT/CONTROL/MENU (from XAML key events) to their left/right forms.</summary>
    private static uint Specific(uint vk, uint scanCode, bool extended) => vk switch
    {
        VkShift => scanCode == 0x36 ? VkRShift : VkLShift,
        VkControl => extended ? VkRControl : VkLControl,
        VkMenu => extended ? VkRMenu : VkLMenu,
        _ => vk,
    };

    private void TrackModifier(uint vk, bool down)
    {
        switch (vk)
        {
            case VkLShift: _lshift = down; break;
            case VkRShift: _rshift = down; break;
            case VkLControl: _lctrl = down; break;
            case VkRControl: _rctrl = down; break;
            case VkLMenu: _lalt = down; break;
            case VkRMenu: _ralt = down; break;
            case VkLWin: _lwin = down; break;
            case VkRWin: _rwin = down; break;
        }
    }
}
