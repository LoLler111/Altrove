using Altrove.Core.Input;
using Altrove.Core.Protocol;

namespace Altrove.Core.Tests;

public class KeyStrokesTests
{
    [Fact]
    public void Text_is_typed_as_unicode_with_enter_and_tab_as_keys()
    {
        var strokes = KeyStrokes.For(new KeyMessage(Text: "è\r\n\t"));
        Assert.Equal(
        [
            new KeyStroke(0, 'è', false), new KeyStroke(0, 'è', true),
            new KeyStroke(KeyStrokes.VkReturn, '\0', false), new KeyStroke(KeyStrokes.VkReturn, '\0', true),
            new KeyStroke(KeyStrokes.VkTab, '\0', false), new KeyStroke(KeyStrokes.VkTab, '\0', true),
        ], strokes);
    }

    [Fact]
    public void Emoji_go_as_their_two_utf16_units()
    {
        var strokes = KeyStrokes.For(new KeyMessage(Text: "😀"));
        Assert.Equal(4, strokes.Count);
        Assert.Equal("😀", new string([strokes[0].Unicode, strokes[2].Unicode]));
    }

    [Fact]
    public void A_shortcut_holds_the_modifiers_around_the_key()
    {
        var strokes = KeyStrokes.For(new KeyMessage(Key: "c", Modifiers: [KeyModifiers.Ctrl, KeyModifiers.Shift]));
        Assert.Equal(
        [
            new KeyStroke(KeyStrokes.VkControl, '\0', false), new KeyStroke(KeyStrokes.VkShift, '\0', false),
            new KeyStroke('C', '\0', false), new KeyStroke('C', '\0', true),
            new KeyStroke(KeyStrokes.VkShift, '\0', true), new KeyStroke(KeyStrokes.VkControl, '\0', true),
        ], strokes);
    }

    [Fact]
    public void Named_keys_and_extended_flags()
    {
        var left = KeyStrokes.For(new KeyMessage(Key: KeyNames.Left));
        Assert.Equal([new KeyStroke(KeyStrokes.VkLeft, '\0', false, true), new KeyStroke(KeyStrokes.VkLeft, '\0', true, true)], left);
        var escape = KeyStrokes.For(new KeyMessage(Key: KeyNames.Escape));
        Assert.False(escape[0].Extended);
        // Win alone opens Start; Win as a modifier of itself is not pressed twice.
        Assert.Equal(2, KeyStrokes.For(new KeyMessage(Key: KeyNames.Win, Modifiers: [KeyModifiers.Win])).Count);
    }

    [Fact]
    public void Other_characters_use_the_layout_and_unknown_keys_do_nothing()
    {
        // On an Italian layout "+" is its own key, "*" is Shift and the same key.
        (ushort, bool)? Layout(char c) => c switch { '+' => ((ushort)0xBB, false), '*' => ((ushort)0xBB, true), _ => null };
        var plus = KeyStrokes.For(new KeyMessage(Key: "+", Modifiers: [KeyModifiers.Ctrl]), Layout);
        Assert.Equal([KeyStrokes.VkControl, (ushort)0xBB, (ushort)0xBB, KeyStrokes.VkControl], plus.Select(s => s.VirtualKey));
        var star = KeyStrokes.For(new KeyMessage(Key: "*"), Layout);
        Assert.Equal([KeyStrokes.VkShift, (ushort)0xBB, (ushort)0xBB, KeyStrokes.VkShift], star.Select(s => s.VirtualKey));
        Assert.Empty(KeyStrokes.For(new KeyMessage(Key: "f99")));
        Assert.Empty(KeyStrokes.For(new KeyMessage(Key: "§"), Layout));
        Assert.Empty(KeyStrokes.For(new KeyMessage()));
    }
}
