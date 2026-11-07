package com.altrove.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.altrove.app.protocol.Configure
import com.altrove.app.protocol.Features
import com.altrove.app.protocol.StreamModes
import com.altrove.app.receiver.InputMode
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
    val inputMode by receiver.inputMode.collectAsState()
    val stats by receiver.stats.collectAsState()
    val tvName by receiver.tvName.collectAsState()
    val useTv by receiver.useTv.collectAsState()
    val context = LocalContext.current

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
        // Two lines kept for the numbers, so the controls below never move when they change.
        Text(
            "${state.width}×${state.height} · ${modeLabel(settings?.mode ?: StreamModes.MIRROR)}" +
                (settings?.scale?.let { " · scala $it%" } ?: "") + "\n" + stats.ifEmpty { "…" },
            color = AltroveColors.TextDim,
            fontSize = 13.sp,
            minLines = 2,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(4.dp))
        Button(onClick = onClose, modifier = Modifier.fillMaxWidth().focusRequester(firstFocus)) { Text("Torna allo schermo") }

        if (!CastSettings.isTvDevice(context)) {
            val name = tvName
            if (name != null) {
                Section("TV «$name»")
                Chips(listOf(true, false), useTv, true, { if (it) "Sulla TV" else "Su questo schermo" }, receiver::setUseTv)
                Note(
                    if (useTv) "Il PC è a tutto schermo sulla TV; qui restano pannello, tocco e trackpad."
                    else "La TV mostra lo stesso schermo di questo dispositivo.",
                )
            } else {
                OutlinedButton(onClick = { CastSettings.open(context) }, modifier = Modifier.fillMaxWidth()) { Text("Proietta su TV") }
                Note(CAST_HINT)
            }
        }

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
            val resolutions = current.choices.resolutions
            if (resolutions != null) {
                Dropdown(resolutions.map { it.maxHeight }, current.maxHeight, enabled, { h ->
                    val choice = resolutions.first { it.maxHeight == h }
                    "${choice.width}×${choice.height}" + if (choice.recommended) " (Consigliata)" else ""
                }) { receiver.configure(Configure(maxHeight = it)) }
            } else {
                // A 0.2 PC: its own short list.
                Chips(current.choices.maxHeights, current.maxHeight, enabled, { if (it == 0) "Automatica" else "${it}p" }) {
                    receiver.configure(Configure(maxHeight = it))
                }
            }
            val scales = current.choices.scales.orEmpty()
            if (current.mode == StreamModes.EXTEND && scales.size > 1 && Features.SCALE in features) {
                Section("SCALA DEL TESTO E DELLE FINESTRE")
                Dropdown(scales, current.scale, enabled, { "$it%" }) { receiver.configure(Configure(scale = it)) }
                Note("Come la scala di Windows: più alta ingrandisce testi e finestre su questo schermo.")
            }
            Section("FLUIDITÀ")
            Chips(current.choices.fps, current.fps, enabled, { "$it fps" }) { receiver.configure(Configure(fps = it)) }
            Section("QUALITÀ")
            Chips(current.choices.bitratesKbps, current.bitrateKbps, enabled, { "${it / 1000} Mbit/s" }) {
                receiver.configure(Configure(bitrateKbps = it))
            }
            Note("Se l'immagine scatta lontano dal router, prova 30 fps o una qualità più bassa.")
            if (Features.MIC in features) {
                Section("MICROFONO DEL TABLET")
                val mic = current.mic
                if (mic != null) {
                    Chips(listOf(true, false), mic, enabled, { if (it) "Acceso" else "Spento" }) { receiver.configure(Configure(mic = it)) }
                    Note("Sul PC diventa «Microfono Altrove»: sceglilo come microfono in Teams, Discord, Zoom.")
                } else {
                    Note("Si attiva una volta da Altrove sul PC: «Usa il microfono del ricevitore».")
                }
            }
            current.audio?.let { on ->
                Section("AUDIO DEL PC")
                Chips(listOf(true, false), on, enabled, { if (it) "Acceso" else "Spento" }) { receiver.configure(Configure(audio = it)) }
                Note("Si sente qui quello che suona il PC. Il volume è quello di questo dispositivo.")
            }
        }

        Section("USARE IL PC DA QUI")
        if (Features.TOUCH in features) {
            if (settings?.touch == false) Note("Sul PC tocco e trackpad sono spenti: si riaccendono da Altrove sul PC.", AltroveColors.Danger)
            val modes = buildList {
                add(InputMode.TOUCH)
                if (Features.MOUSE in features) add(InputMode.TRACKPAD)
                add(InputMode.OFF)
            }
            Chips(modes, inputMode, true, ::inputLabel, receiver::setInputMode)
            Note(
                when (inputMode) {
                    InputMode.TOUCH -> "Si clicca dove si tocca; tenere premuto è il clic destro; due dita scorrono."
                    InputMode.TRACKPAD -> if (Features.MOUSE in features) {
                        "Il dito muove il puntatore. Tocco: clic; doppio tocco: doppio clic; tocco con un secondo dito (anche tenendo giù il primo): clic destro; due dita insieme: scorrere; doppio tocco tenendo giù: trascinare."
                    } else {
                        "Aggiorna Altrove sul PC per usare il trackpad."
                    }
                    InputMode.OFF -> "Il PC non riceve niente: un tocco sullo schermo apre questo pannello."
                } + if (inputMode != InputMode.OFF) " Per tornare qui: linguetta a sinistra o tasto Indietro." else "",
            )
        } else {
            Note("Il PC non accetta tocco né trackpad: si attiva in Altrove sul PC, prima di trasmettere.")
        }

        Spacer(Modifier.height(12.dp))
        OutlinedButton(
            onClick = receiver::disconnect,
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = AltroveColors.Danger),
        ) { Text("Interrompi la trasmissione") }
    }
}

private fun inputLabel(mode: InputMode) = when (mode) {
    InputMode.TOUCH -> "Tocco"
    InputMode.TRACKPAD -> "Trackpad"
    InputMode.OFF -> "Spento"
}

/** A drop-down list: the chosen value on a button that opens the others. Works with touch and with a TV remote. */
@Composable
private fun <T> Dropdown(values: List<T>, selected: T?, enabled: Boolean, label: (T) -> String, onPick: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { open = true }, enabled = enabled, modifier = Modifier.fillMaxWidth()) {
            Text(selected?.let(label) ?: "Scegli…", modifier = Modifier.weight(1f), color = AltroveColors.Text)
            Text("▾", color = AltroveColors.TextDim)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            values.forEach { value ->
                DropdownMenuItem(
                    text = {
                        Text(
                            label(value),
                            fontWeight = if (value == selected) FontWeight.SemiBold else FontWeight.Normal,
                            color = if (value == selected) AltroveColors.Accent else AltroveColors.Text,
                        )
                    },
                    onClick = {
                        open = false
                        if (value != selected) onPick(value)
                    },
                )
            }
        }
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
