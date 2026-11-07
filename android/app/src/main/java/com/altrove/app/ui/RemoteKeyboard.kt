package com.altrove.app.ui

import android.annotation.SuppressLint
import android.content.Context
import android.text.InputType
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.altrove.app.protocol.Key
import com.altrove.app.protocol.KeyModifiers
import com.altrove.app.protocol.KeyNames
import com.altrove.app.receiver.KeyboardMapper

/**
 * An invisible text field that hands what the on-screen keyboard (or a hardware one) types to [onKeys],
 * instead of keeping it: the text goes to the PC. No suggestions and no full-screen editor in landscape.
 */
@SuppressLint("ViewConstructor")
class RemoteKeyboardView(
    context: Context,
    private val onKeys: (List<Key>) -> Unit,
    /** The modifiers held on the bar; they apply to the next key and are then let go. */
    private val takeHeld: () -> Set<String>,
) : View(context) {
    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    private val inputMethods get() = context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager

    fun showKeyboard() {
        requestFocus()
        inputMethods.showSoftInput(this, InputMethodManager.SHOW_IMPLICIT)
    }

    fun hideKeyboard() {
        inputMethods.hideSoftInputFromWindow(windowToken, 0)
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        // A visible password: keyboards type letter by letter, without guessing or correcting words.
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or
            InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI or
            EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_FLAG_NO_ENTER_ACTION
        return Connection()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean = handle(event) || super.onKeyDown(keyCode, event)

    private fun handle(event: KeyEvent): Boolean {
        if (event.action != KeyEvent.ACTION_DOWN) return false
        // Some keyboards hand over whole strings (ACTION_MULTIPLE) or single characters as key events.
        val modifiersOff = event.metaState and (KeyEvent.META_CTRL_MASK or KeyEvent.META_ALT_MASK or KeyEvent.META_META_MASK).inv()
        val key = KeyboardMapper.hardwareKey(
            keyCode = event.keyCode,
            char = event.getUnicodeChar(modifiersOff),
            baseChar = event.getUnicodeChar(0),
            ctrl = event.isCtrlPressed,
            alt = event.isAltPressed,
            shift = event.isShiftPressed,
            meta = event.isMetaPressed,
            held = takeHeld(),
        ) ?: return false
        onKeys(listOf(key))
        return true
    }

    private inner class Connection : BaseInputConnection(this, false) {
        // The word the keyboard is still composing: already typed on the PC, and replaced as it changes.
        private var composing = ""

        override fun commitText(text: CharSequence, newCursorPosition: Int): Boolean {
            onKeys(KeyboardMapper.edit(composing, text.toString(), takeHeld()))
            composing = ""
            return true
        }

        override fun setComposingText(text: CharSequence, newCursorPosition: Int): Boolean {
            onKeys(KeyboardMapper.edit(composing, text.toString(), takeHeld()))
            composing = text.toString()
            return true
        }

        override fun finishComposingText(): Boolean {
            composing = ""
            return true
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            onKeys(List(beforeLength) { Key(key = KeyNames.BACKSPACE) } + List(afterLength) { Key(key = KeyNames.DELETE) })
            return true
        }

        override fun sendKeyEvent(event: KeyEvent): Boolean {
            handle(event)
            return true
        }

        override fun performEditorAction(actionCode: Int): Boolean {
            onKeys(listOf(Key(key = KeyNames.ENTER)))
            return true
        }
    }
}

/**
 * The bar over the on-screen keyboard while typing on the PC: the keys a phone keyboard lacks, Ctrl and
 * Alt that stay held for the next key, and the way out.
 */
@Composable
fun KeyboardBar(send: (Key) -> Unit, onClose: () -> Unit, modifier: Modifier = Modifier) {
    var ctrl by remember { mutableStateOf(false) }
    var alt by remember { mutableStateOf(false) }
    var input by remember { mutableStateOf<RemoteKeyboardView?>(null) }

    fun held(): Set<String> = buildSet {
        if (ctrl) add(KeyModifiers.CTRL)
        if (alt) add(KeyModifiers.ALT)
    }

    fun takeHeld(): Set<String> = held().also {
        ctrl = false
        alt = false
    }

    fun press(name: String) = send(Key(key = name, modifiers = takeHeld().sorted().ifEmpty { null }))

    DisposableEffect(Unit) {
        onDispose { input?.hideKeyboard() }
    }

    Row(
        modifier
            .fillMaxWidth()
            .background(AltroveColors.Panel)
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        AndroidView(
            modifier = Modifier.size(1.dp),
            factory = { context ->
                RemoteKeyboardView(context, onKeys = { keys -> keys.forEach(send) }, takeHeld = ::takeHeld).also { view ->
                    input = view
                    view.post { view.showKeyboard() }
                }
            },
        )
        BarKey("⌨") { input?.showKeyboard() }
        BarKey("Esc") { press(KeyNames.ESCAPE) }
        BarKey("Tab") { press(KeyNames.TAB) }
        BarToggle("Ctrl", ctrl) { ctrl = !ctrl }
        BarToggle("Alt", alt) { alt = !alt }
        BarKey("Win") { press(KeyNames.WIN) }
        BarKey("←") { press(KeyNames.LEFT) }
        BarKey("↑") { press(KeyNames.UP) }
        BarKey("↓") { press(KeyNames.DOWN) }
        BarKey("→") { press(KeyNames.RIGHT) }
        BarKey("Canc") { press(KeyNames.DELETE) }
        BarKey("Chiudi") { onClose() }
    }
}

@Composable
private fun BarKey(label: String, onClick: () -> Unit) = BarToggle(label, false, onClick)

@Composable
private fun BarToggle(label: String, on: Boolean, onClick: () -> Unit) {
    FilterChip(
        selected = on,
        onClick = onClick,
        label = { Text(label) },
        colors = FilterChipDefaults.filterChipColors(
            selectedContainerColor = AltroveColors.Accent,
            selectedLabelColor = AltroveColors.Background,
            labelColor = AltroveColors.Text,
        ),
    )
}
