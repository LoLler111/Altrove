package com.altrove.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.altrove.app.protocol.Configure
import com.altrove.app.protocol.Features
import com.altrove.app.protocol.StreamModes
import com.altrove.app.receiver.ReceiverController
import com.altrove.app.receiver.ReceiverState

/**
 * The panel over the video: what is streaming, and the PC's choices (mode, screen, resolution, fluidity,
 * quality) to change it from here. Usable with touch and with a TV remote.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SettingsPanel(
    receiver: ReceiverController,
    state: ReceiverState.Streaming,
    firstFocus: FocusRequester,
    onClose: () -> Unit,
) {
    val settings by receiver.settings.collectAsState()
    val features by receiver.senderFeatures.collectAsState()
    val applying by receiver.applying.collectAsState()
    val touchEnabled by receiver.touchEnabled.collectAsState()
    val stats by receiver.stats.collectAsState()

    Column(
        Modifier
            .fillMaxHeight()
            .width(360.dp)
            .background(AltroveColors.Panel)
            .safeDrawingPadding()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text("Collegato a ${state.senderName}", color = AltroveColors.Text, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        Text(
            "${state.width}×${state.height} · ${modeLabel(settings?.mode ?: StreamModes.MIRROR)}",
            color = AltroveColors.TextDim,
            fontSize = 13.sp,
        )
        Spacer(Modifier.height(4.dp))
        Button(onClick = onClose, modifier = Modifier.fillMaxWidth().focusRequester(firstFocus)) { Text("Torna allo schermo") }

        val current = settings
        if (Features.CONFIGURE !in features) {
            Note("Per cambiare le impostazioni da qui aggiorna Altrove sul PC.")
        } else if (current == null) {
            Note("In attesa delle impostazioni del PC…")
        } else {
            val enabled = !applying
            if (applying) Note("Applico le modifiche sul PC…", AltroveColors.Accent)
            current.notice?.let { Note(it, AltroveColors.Danger) }

            Section("MODALITÀ")
            Chips(current.choices.modes, current.mode, enabled, ::modeLabel) { receiver.configure(Configure(mode = it)) }
            if (StreamModes.EXTEND !in current.choices.modes) {
                Note("Estendi si attiva la prima volta da Altrove sul PC.")
            }
            if (current.mode == StreamModes.MIRROR && current.choices.screens.size > 1) {
                Section("SCHERMO DA DUPLICARE")
                Chips(current.choices.screens.map { it.id }, current.screen, enabled, { id -> current.choices.screens.first { it.id == id }.name }) {
                    receiver.configure(Configure(screen = it))
                }
            }
            Section("RISOLUZIONE")
            Chips(current.choices.maxHeights, current.maxHeight, enabled, { if (it == 0) "Automatica" else "${it}p" }) {
                receiver.configure(Configure(maxHeight = it))
            }
            Section("FLUIDITÀ")
            Chips(current.choices.fps, current.fps, enabled, { "$it fps" }) { receiver.configure(Configure(fps = it)) }
            Section("QUALITÀ")
            Chips(current.choices.bitratesKbps, current.bitrateKbps, enabled, { "${it / 1000} Mbit/s" }) {
                receiver.configure(Configure(bitrateKbps = it))
            }
            Note("Se l'immagine scatta lontano dal router, prova 30 fps o una qualità più bassa.")
        }

        Section("TOCCO")
        if (Features.TOUCH in features) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Usa il PC con il tocco", color = AltroveColors.Text, fontSize = 15.sp, modifier = Modifier.weight(1f))
                Switch(checked = touchEnabled, onCheckedChange = receiver::setTouchEnabled)
            }
            Note(
                if (touchEnabled) "Un tocco è un clic, tenere premuto è il clic destro, due dita scorrono. Per riaprire questo pannello usa la linguetta a sinistra o il tasto Indietro."
                else "Il tocco non arriva al PC: un tocco sullo schermo apre questo pannello.",
            )
        } else {
            Note("Il PC non accetta il tocco: si attiva in Altrove sul PC, prima di trasmettere.")
        }

        Spacer(Modifier.height(12.dp))
        OutlinedButton(
            onClick = receiver::disconnect,
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = AltroveColors.Danger),
        ) { Text("Interrompi la trasmissione") }
        // Last, so its changing length never moves the controls under a finger.
        if (stats.isNotEmpty()) Note(stats)
    }
}

@Composable
private fun Section(title: String) {
    Text(title, color = AltroveColors.TextDim, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 10.dp))
}

@Composable
private fun Note(text: String, color: androidx.compose.ui.graphics.Color = AltroveColors.TextDim) {
    Text(text, color = color, fontSize = 13.sp)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun <T> Chips(values: List<T>, selected: T?, enabled: Boolean, label: (T) -> String, onPick: (T) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        values.forEach { value ->
            FilterChip(
                selected = value == selected,
                onClick = { if (value != selected) onPick(value) },
                enabled = enabled,
                label = { Text(label(value)) },
                colors = FilterChipDefaults.filterChipColors(
                    selectedContainerColor = AltroveColors.Accent,
                    selectedLabelColor = AltroveColors.Background,
                    labelColor = AltroveColors.Text,
                ),
            )
        }
    }
}

private fun modeLabel(mode: String) = when (mode) {
    StreamModes.EXTEND -> "Estendi"
    else -> "Duplica"
}
