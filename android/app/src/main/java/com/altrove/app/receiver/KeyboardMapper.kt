package com.altrove.app.receiver

import android.view.KeyEvent
import com.altrove.app.protocol.Key
import com.altrove.app.protocol.KeyModifiers
import com.altrove.app.protocol.KeyNames

/**
 * What this device's keyboards send to the PC as [Key] messages. Plain values in and out, so the JVM
 * tests need no Android: the on-screen keyboard's text edits, and the keys of a hardware keyboard.
 */
object KeyboardMapper {
    /**
     * The keys that turn [old] (the word being composed, already typed on the PC) into [new]: a backspace
     * for every character that changed, then the new end. An on-screen keyboard that corrects a word does
     * exactly this.
     */
    fun edit(old: String, new: String, modifiers: Set<String> = emptySet()): List<Key> {
        var common = 0
        while (common < old.length && common < new.length && old[common] == new[common]) common++
        // Never split an emoji (two UTF-16 units): one backspace on the PC removes both.
        if (common > 0 && Character.isHighSurrogate(old[common - 1])) common--
        val keys = ArrayList<Key>()
        repeat(old.codePointCount(common, old.length)) { keys += Key(key = KeyNames.BACKSPACE) }
        if (common < new.length) keys += text(new.substring(common), modifiers)
        return keys
    }

    /** Typed text; with modifiers held (Ctrl from the bar, then "c") a single character becomes a shortcut. */
    fun text(text: String, modifiers: Set<String> = emptySet()): Key {
        if (modifiers.isNotEmpty() && text.codePointCount(0, text.length) == 1 && text != "\n") {
            return Key(key = if (text == " ") KeyNames.SPACE else text.lowercase(), modifiers = modifiers.sorted())
        }
        return Key(text = text)
    }

    /** The PC's name for an Android key code that is not a character; null for the others. */
    fun namedKey(keyCode: Int): String? = when (keyCode) {
        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> KeyNames.ENTER
        KeyEvent.KEYCODE_DEL -> KeyNames.BACKSPACE
        KeyEvent.KEYCODE_FORWARD_DEL -> KeyNames.DELETE
        KeyEvent.KEYCODE_TAB -> KeyNames.TAB
        KeyEvent.KEYCODE_ESCAPE -> KeyNames.ESCAPE
        KeyEvent.KEYCODE_DPAD_LEFT -> KeyNames.LEFT
        KeyEvent.KEYCODE_DPAD_RIGHT -> KeyNames.RIGHT
        KeyEvent.KEYCODE_DPAD_UP -> KeyNames.UP
        KeyEvent.KEYCODE_DPAD_DOWN -> KeyNames.DOWN
        KeyEvent.KEYCODE_MOVE_HOME -> KeyNames.HOME
        KeyEvent.KEYCODE_MOVE_END -> KeyNames.END
        KeyEvent.KEYCODE_PAGE_UP -> KeyNames.PAGE_UP
        KeyEvent.KEYCODE_PAGE_DOWN -> KeyNames.PAGE_DOWN
        else -> null
    }

    /**
     * One key of a hardware keyboard (or a key event from the on-screen one). [char] is the character with
     * Shift applied, [baseChar] the one without any modifier, both 0 when there is none; [held] are the
     * modifiers from the bar on screen. Null for keys the PC does not need (Back, volume, Shift alone…).
     */
    fun hardwareKey(
        keyCode: Int,
        char: Int,
        baseChar: Int,
        ctrl: Boolean,
        alt: Boolean,
        shift: Boolean,
        meta: Boolean,
        held: Set<String> = emptySet(),
    ): Key? {
        val modifiers = held.toMutableSet()
        if (ctrl) modifiers += KeyModifiers.CTRL
        if (alt) modifiers += KeyModifiers.ALT
        if (meta) modifiers += KeyModifiers.WIN
        namedKey(keyCode)?.let { name ->
            if (shift) modifiers += KeyModifiers.SHIFT
            return Key(key = name, modifiers = modifiers.sorted().ifEmpty { null })
        }
        // 0: no character (Shift alone, volume, Back); negative: a dead key (KeyCharacterMap.COMBINING_ACCENT).
        if (char <= 0) return null
        if (modifiers.isEmpty()) return Key(text = String(Character.toChars(char)))
        // A shortcut: the key itself (Ctrl+Shift+T is "t" with ctrl and shift), not the shifted character.
        val base = if (baseChar != 0) baseChar else char
        if (shift) modifiers += KeyModifiers.SHIFT
        val name = if (base == ' '.code) KeyNames.SPACE else String(Character.toChars(base)).lowercase()
        return Key(key = name, modifiers = modifiers.sorted())
    }
}
