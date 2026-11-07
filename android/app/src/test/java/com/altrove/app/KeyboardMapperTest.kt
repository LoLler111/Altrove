package com.altrove.app

import android.view.KeyEvent
import com.altrove.app.protocol.Key
import com.altrove.app.protocol.KeyModifiers
import com.altrove.app.protocol.KeyNames
import com.altrove.app.receiver.KeyboardMapper
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class KeyboardMapperTest {
    private val backspace = Key(key = KeyNames.BACKSPACE)

    @Test
    fun composing_a_word_types_only_what_changes() {
        assertEquals(listOf(Key(text = "c")), KeyboardMapper.edit("", "c"))
        assertEquals(listOf(Key(text = "a")), KeyboardMapper.edit("ci", "cia"))
        // The keyboard corrects "ciso" into "ciao": two backspaces, then "ao".
        assertEquals(listOf(backspace, backspace, Key(text = "ao")), KeyboardMapper.edit("ciso", "ciao"))
        // Committing the same word types nothing more.
        assertEquals(emptyList<Key>(), KeyboardMapper.edit("ciao", "ciao"))
        assertEquals(listOf(backspace, backspace), KeyboardMapper.edit("ok", ""))
    }

    @Test
    fun an_emoji_is_one_backspace_and_never_split() {
        assertEquals(listOf(backspace), KeyboardMapper.edit("a😀", "a"))
        // "😀" and "😃" share the first UTF-16 unit: both units go.
        assertEquals(listOf(backspace, Key(text = "😃")), KeyboardMapper.edit("😀", "😃"))
    }

    @Test
    fun held_modifiers_turn_one_character_into_a_shortcut() {
        val ctrl = setOf(KeyModifiers.CTRL)
        assertEquals(listOf(Key(key = "c", modifiers = listOf("ctrl"))), KeyboardMapper.edit("", "C", ctrl))
        assertEquals(Key(key = KeyNames.SPACE, modifiers = listOf("alt", "ctrl")), KeyboardMapper.text(" ", setOf("ctrl", "alt")))
        // Longer text stays text.
        assertEquals(Key(text = "ciao"), KeyboardMapper.text("ciao", ctrl))
        assertEquals(Key(text = "\n"), KeyboardMapper.text("\n", ctrl))
    }

    @Test
    fun hardware_keys() {
        fun key(code: Int, char: Int = 0, base: Int = char, ctrl: Boolean = false, alt: Boolean = false, shift: Boolean = false, meta: Boolean = false, held: Set<String> = emptySet()) =
            KeyboardMapper.hardwareKey(code, char, base, ctrl, alt, shift, meta, held)

        assertEquals(Key(text = "è"), key(KeyEvent.KEYCODE_APOSTROPHE, 'è'.code))
        assertEquals(Key(text = "A"), key(KeyEvent.KEYCODE_A, 'A'.code, 'a'.code, shift = true))
        assertEquals(Key(key = KeyNames.ENTER), key(KeyEvent.KEYCODE_ENTER, '\n'.code))
        assertEquals(Key(key = KeyNames.LEFT, modifiers = listOf("shift")), key(KeyEvent.KEYCODE_DPAD_LEFT, shift = true))
        assertEquals(Key(key = KeyNames.BACKSPACE), key(KeyEvent.KEYCODE_DEL))
        assertEquals(Key(key = "t", modifiers = listOf("ctrl", "shift")), key(KeyEvent.KEYCODE_T, 'T'.code, 't'.code, ctrl = true, shift = true))
        assertEquals(Key(key = "c", modifiers = listOf("ctrl")), key(KeyEvent.KEYCODE_C, 'c'.code, held = setOf("ctrl")))
        assertEquals(Key(key = "d", modifiers = listOf("win")), key(KeyEvent.KEYCODE_D, 'd'.code, meta = true))
        assertNull(key(KeyEvent.KEYCODE_SHIFT_LEFT))
        assertNull(key(KeyEvent.KEYCODE_VOLUME_UP))
        assertNull(key(KeyEvent.KEYCODE_BACK))
        assertNull(key(KeyEvent.KEYCODE_GRAVE, 0x80000060.toInt())) // a dead key
    }
}
