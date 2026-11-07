using Altrove.Core.Protocol;

namespace Altrove.Core.Input;

/// <summary>
/// One press or release for Windows' SendInput: a virtual key (<c>Unicode</c> 0), or a UTF-16 unit typed as it is
/// (<c>VirtualKey</c> 0, KEYEVENTF_UNICODE). <c>Extended</c> is KEYEVENTF_EXTENDEDKEY (arrows, Home, End, …).
/// </summary>
public readonly record struct KeyStroke(ushort VirtualKey, char Unicode, bool Up, bool Extended = false);

/// <summary>Turns a <see cref="KeyMessage"/> into the strokes Windows needs, without calling Windows (tested in Core).</summary>
public static class KeyStrokes
{
    public const ushort VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12,
        VkEscape = 0x1B, VkSpace = 0x20, VkPrior = 0x21, VkNext = 0x22, VkEnd = 0x23, VkHome = 0x24, VkLeft = 0x25,
        VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkDelete = 0x2E, VkLWin = 0x5B;

    // Name → (virtual key, extended).
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        [KeyNames.Enter] = (VkReturn, false),
        [KeyNames.Backspace] = (VkBack, false),
        [KeyNames.Delete] = (VkDelete, true),
        [KeyNames.Tab] = (VkTab, false),
        [KeyNames.Escape] = (VkEscape, false),
        [KeyNames.Space] = (VkSpace, false),
        [KeyNames.Left] = (VkLeft, true),
        [KeyNames.Right] = (VkRight, true),
        [KeyNames.Up] = (VkUp, true),
        [KeyNames.Down] = (VkDown, true),
        [KeyNames.Home] = (VkHome, true),
        [KeyNames.End] = (VkEnd, true),
        [KeyNames.PageUp] = (VkPrior, true),
        [KeyNames.PageDown] = (VkNext, true),
        [KeyNames.Win] = (VkLWin, true),
    };

    private static readonly Dictionary<string, (ushort Vk, bool Extended)> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        [KeyModifiers.Ctrl] = (VkControl, false),
        [KeyModifiers.Alt] = (VkMenu, false),
        [KeyModifiers.Shift] = (VkShift, false),
        [KeyModifiers.Win] = (VkLWin, true),
    };

    /// <summary>
    /// The strokes for <paramref name="message"/>; empty when there is nothing Windows can press.
    /// <paramref name="keyForChar"/> is VkKeyScanW: the key (and whether it needs Shift) of a character on the PC's
    /// keyboard layout, for shortcuts with other characters than letters and digits.
    /// </summary>
    public static IReadOnlyList<KeyStroke> For(KeyMessage message, Func<char, (ushort Vk, bool Shift)?>? keyForChar = null)
    {
        var strokes = new List<KeyStroke>();
        if (!string.IsNullOrEmpty(message.Text))
        {
            foreach (var c in message.Text)
            {
                switch (c)
                {
                    case '\r':
                        break; // "\r\n" is one Enter
                    case '\n':
                        Press(strokes, VkReturn, false);
                        break;
                    case '\t':
                        Press(strokes, VkTab, false);
                        break;
                    case '\b':
                        Press(strokes, VkBack, false);
                        break;
                    default:
                        // Each UTF-16 unit on its own, surrogate pairs included: Windows puts them back together.
                        strokes.Add(new KeyStroke(0, c, Up: false));
                        strokes.Add(new KeyStroke(0, c, Up: true));
                        break;
                }
            }
            return strokes;
        }

        if (string.IsNullOrEmpty(message.Key))
            return strokes;
        (ushort Vk, bool Extended) key;
        var shiftForChar = false;
        if (Named.TryGetValue(message.Key, out var named))
            key = named;
        else if (message.Key.Length == 1 && char.IsAsciiLetterOrDigit(message.Key[0]))
            key = (char.ToUpperInvariant(message.Key[0]), false); // VK_A…VK_Z and VK_0…VK_9 are the ASCII codes
        else if (message.Key.Length == 1 && keyForChar?.Invoke(message.Key[0]) is { } scanned)
        {
            key = (scanned.Vk, false);
            shiftForChar = scanned.Shift;
        }
        else
            return strokes;

        var held = new List<(ushort Vk, bool Extended)>();
        foreach (var name in message.Modifiers ?? [])
        {
            if (Modifiers.TryGetValue(name, out var modifier) && !held.Contains(modifier) && modifier.Vk != key.Vk)
                held.Add(modifier);
        }
        if (shiftForChar && !held.Contains(Modifiers[KeyModifiers.Shift]))
            held.Add(Modifiers[KeyModifiers.Shift]);

        foreach (var (vk, extended) in held)
            strokes.Add(new KeyStroke(vk, '\0', Up: false, extended));
        Press(strokes, key.Vk, key.Extended);
        for (var i = held.Count - 1; i >= 0; i--)
            strokes.Add(new KeyStroke(held[i].Vk, '\0', Up: true, held[i].Extended));
        return strokes;
    }

    private static void Press(List<KeyStroke> strokes, ushort vk, bool extended)
    {
        strokes.Add(new KeyStroke(vk, '\0', Up: false, extended));
        strokes.Add(new KeyStroke(vk, '\0', Up: true, extended));
    }
}
