using Avalonia.Input;
using Vesper.Core.Input;
using Vesper.Linux.Services;
using Vesper.Models;

namespace Vesper.Core.Tests;

/// <summary>What the Linux app sends the Mac for key events, as different Linux sessions deliver them.</summary>
public sealed class LinuxKeyMapperTests
{
    private static List<(uint Keysym, bool Down)> Type(KeyMapper keys, PhysicalKey physical, Key key, string? symbol)
    {
        var output = new List<(uint, bool)>();
        keys.Translate(physical, key, symbol, down: true, output);
        keys.Translate(physical, key, symbol, down: false, output);
        return output;
    }

    private static void Hold(KeyMapper keys, PhysicalKey physical, Key key) => keys.Translate(physical, key, null, true, []);

    [Theory]
    [InlineData(PhysicalKey.Digit7, Key.D7, null, '&')]   // no typed character in the event
    [InlineData(PhysicalKey.Digit7, Key.D7, "7", '&')]    // the unshifted one, Shift left out
    [InlineData(PhysicalKey.Digit7, Key.D7, "&", '&')]    // the right one
    [InlineData(PhysicalKey.Digit4, Key.D4, null, '$')]
    [InlineData(PhysicalKey.Digit1, Key.D1, "1", '!')]
    [InlineData(PhysicalKey.Slash, Key.OemQuestion, null, '?')]
    [InlineData(PhysicalKey.Quote, Key.OemQuotes, "'", '"')]
    public void Shift_with_a_digit_or_punctuation_sends_the_shifted_character(PhysicalKey physical, Key key, string? symbol, char expected)
    {
        var keys = new KeyMapper();
        Hold(keys, PhysicalKey.ShiftLeft, Key.LeftShift);
        var sent = Type(keys, physical, key, symbol);
        Assert.Equal([(KeySym.FromCodePoint(expected), true), (KeySym.FromCodePoint(expected), false)], sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("7")]
    public void Without_Shift_a_digit_is_the_digit(string? symbol)
    {
        var sent = Type(new KeyMapper(), PhysicalKey.Digit7, Key.D7, symbol);
        Assert.Equal((KeySym.FromCodePoint('7'), true), sent[0]);
    }

    [Fact]
    public void Shift_with_a_letter_leaves_the_capital_to_the_Mac()
    {
        var keys = new KeyMapper();
        Hold(keys, PhysicalKey.ShiftLeft, Key.LeftShift);
        Assert.Equal((KeySym.FromCodePoint('a'), true), Type(keys, PhysicalKey.A, Key.A, null)[0]);
        Assert.Equal((KeySym.FromCodePoint('A'), true), Type(keys, PhysicalKey.A, Key.A, "A")[0]);
    }

    [Fact]
    public void Shortcuts_send_the_plain_key_under_Command()
    {
        var keys = new KeyMapper { Layout = ModifierLayout.MacStyle };
        Hold(keys, PhysicalKey.ControlLeft, Key.LeftCtrl);
        Hold(keys, PhysicalKey.ShiftLeft, Key.LeftShift);
        Assert.Equal((KeySym.FromCodePoint('4'), true), Type(keys, PhysicalKey.Digit4, Key.D4, "$")[0]); // Cmd-Shift-4
    }

    [Fact]
    public void A_release_repeats_what_the_press_sent_even_if_Shift_let_go_first()
    {
        var keys = new KeyMapper();
        var output = new List<(uint, bool)>();
        keys.Translate(PhysicalKey.ShiftLeft, Key.LeftShift, null, true, output);
        keys.Translate(PhysicalKey.Digit2, Key.D2, null, true, output);
        keys.Translate(PhysicalKey.ShiftLeft, Key.LeftShift, null, false, output);
        keys.Translate(PhysicalKey.Digit2, Key.D2, null, false, output);
        Assert.Equal((KeySym.FromCodePoint('@'), true), output[1]);
        Assert.Equal((KeySym.FromCodePoint('@'), false), output[3]);
    }
}
