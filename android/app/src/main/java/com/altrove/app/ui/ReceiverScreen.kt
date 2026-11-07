package com.altrove.app.ui

import android.annotation.SuppressLint
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.key
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.altrove.app.receiver.ReceiverController
import com.altrove.app.receiver.ReceiverState
import com.altrove.app.receiver.TouchMapper
import com.altrove.app.receiver.TrackpadGestures
import com.altrove.app.update.UpdateController

@Composable
fun ReceiverScreen(receiver: ReceiverController, updates: UpdateController) {
    val state by receiver.state.collectAsState()
    when (val s = state) {
        is ReceiverState.Streaming -> VideoScreen(receiver, s)
        else -> WaitingScreen(receiver, s, updates)
    }
}

@Composable
private fun WaitingScreen(receiver: ReceiverController, state: ReceiverState, updates: UpdateController) {
    val context = LocalContext.current
    val tvName by receiver.tvName.collectAsState()
    val useTv by receiver.useTv.collectAsState()
    Box(
        Modifier
            .fillMaxSize()
            .background(AltroveColors.Background)
            .safeDrawingPadding()
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            Modifier
                .widthIn(max = 640.dp)
                .verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            UpdateBanner(updates)
            Text("Altrove", fontSize = 40.sp, fontWeight = FontWeight.SemiBold, color = AltroveColors.Text)
            Text("Il tuo schermo, altrove.", fontSize = 16.sp, color = AltroveColors.TextDim)
            Spacer(Modifier.height(16.dp))
            when (state) {
                is ReceiverState.Waiting -> {
                    StatusRow(AltroveColors.Online, "Pronto a ricevere come «${state.name}»")
                    Text(
                        "Sul PC apri Altrove: questo dispositivo compare da solo nell'elenco dei ricevitori.",
                        textAlign = TextAlign.Center,
                        color = AltroveColors.TextDim,
                        fontSize = 15.sp,
                    )
                    if (state.addresses.isNotEmpty()) {
                        Spacer(Modifier.height(8.dp))
                        Text("Se non compare, aggiungi a mano:", color = AltroveColors.TextDim, fontSize = 14.sp)
                        state.addresses.forEach { address ->
                            val shown = if (state.port == com.altrove.app.protocol.AltroveProtocol.DEFAULT_PORT) address else "$address:${state.port}"
                            Text(
                                shown,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(AltroveColors.Panel2)
                                    .padding(horizontal = 16.dp, vertical = 8.dp),
                                fontFamily = FontFamily.Monospace,
                                fontSize = 22.sp,
                                color = AltroveColors.Text,
                            )
                        }
                    } else {
                        Text("Nessuna rete: collega il dispositivo al Wi-Fi di casa.", color = AltroveColors.Danger, fontSize = 15.sp)
                    }
                }
                is ReceiverState.Connected -> StatusRow(AltroveColors.Accent, "Collegato a ${state.senderName}, in attesa dello schermo…")
                is ReceiverState.Failed -> StatusRow(AltroveColors.Danger, state.message)
                is ReceiverState.Streaming -> Unit
            }
            if (!CastSettings.isTvDevice(context)) {
                Spacer(Modifier.height(16.dp))
                val name = tvName
                if (name != null) {
                    StatusRow(AltroveColors.Online, "TV collegata: «$name»")
                    Text(
                        if (useTv) "Quando il PC trasmette, lo schermo va sulla TV e qui restano pannello e trackpad."
                        else "Il PC resta su questo schermo: si cambia dal pannello durante la trasmissione.",
                        textAlign = TextAlign.Center,
                        color = AltroveColors.TextDim,
                        fontSize = 14.sp,
                    )
                } else {
                    OutlinedButton(onClick = { CastSettings.open(context) }) { Text("Proietta su TV") }
                    Text(CAST_HINT, textAlign = TextAlign.Center, color = AltroveColors.TextDim, fontSize = 13.sp)
                }
            }
        }
    }
}

/** How a Miracast TV gets linked, under every «Proietta su TV» button. */
internal const val CAST_HINT =
    "Sulla TV o sulla Fire TV attiva prima la duplicazione dello schermo (Miracast). " +
        "Su Samsung il collegamento si fa con Smart View, dalla tendina rapida."

@Composable
private fun StatusRow(color: Color, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).clip(CircleShape).background(color))
        Spacer(Modifier.size(10.dp))
        Text(text, color = AltroveColors.Text, fontSize = 17.sp)
    }
}

@SuppressLint("ClickableViewAccessibility")
@Composable
private fun VideoScreen(receiver: ReceiverController, state: ReceiverState.Streaming) {
    var panelOpen by remember { mutableStateOf(false) }
    val firstFocus = remember { FocusRequester() }
    // Back (or the remote's back key) opens and closes the panel instead of leaving the app.
    BackHandler { panelOpen = !panelOpen }
    LaunchedEffect(panelOpen) {
        if (panelOpen) runCatching { firstFocus.requestFocus() }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            // The black bars are not the PC: a tap there always opens the panel.
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) { panelOpen = true },
        contentAlignment = Alignment.Center,
    ) {
        val onTv by receiver.tvShowing.collectAsState()
        val pictureShape = Modifier.aspectRatio(state.width.toFloat() / state.height, matchHeightConstraintsFirst = false)
        // While the TV shows the PC, this screen keeps an area of the same shape for the fingers.
        key(onTv) {
            AndroidView(
                modifier = pictureShape,
                factory = { context ->
                    val view = if (onTv) {
                        View(context).apply { setBackgroundColor(AltroveColors.Panel2.toArgb()) }
                    } else {
                        SurfaceView(context).apply {
                            holder.addCallback(object : SurfaceHolder.Callback {
                                override fun surfaceCreated(holder: SurfaceHolder) = receiver.attachSurface(holder.surface)
                                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                                override fun surfaceDestroyed(holder: SurfaceHolder) = receiver.detachSurface(holder.surface)
                            })
                        }
                    }
                    // On the picture: fingers go to the PC as touches or as a trackpad; with neither, a tap opens the panel.
                    val density = view.resources.displayMetrics.density
                    val trackpad = TrackpadGestures(slopPx = 8 * density, notchPx = 24 * density)
                    view.setOnTouchListener { touched, event ->
                        when {
                            receiver.sendsTouch -> TouchMapper.message(event, touched.width, touched.height)?.let(receiver::sendTouch)
                            receiver.sendsMouse -> {
                                trackpad.onEvent(event, touched.width, touched.height).forEach(receiver::sendMouse)
                                // A single tap waits to see whether a second one follows.
                                trackpad.deadline?.let { due ->
                                    touched.postDelayed({ trackpad.onTimeout(due).forEach(receiver::sendMouse) }, due - event.eventTime)
                                }
                            }
                            event.actionMasked == MotionEvent.ACTION_UP -> panelOpen = true
                        }
                        true
                    }
                    view
                },
            )
        }
        if (onTv) {
            val tvName by receiver.tvName.collectAsState()
            // Read so the text follows the panel's choice; the getters below depend on it.
            @Suppress("UNUSED_VARIABLE") val inputMode by receiver.inputMode.collectAsState()
            Text(
                "Lo schermo del PC è sulla TV" + (tvName?.let { " «$it»" } ?: "") + ".\nQui le dita fanno da " +
                    when {
                        receiver.sendsMouse -> "trackpad."
                        receiver.sendsTouch -> "tocco, come se toccassi lo schermo della TV."
                        else -> "comando: un tocco apre il pannello."
                    },
                color = AltroveColors.TextDim,
                fontSize = 16.sp,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(32.dp),
            )
        }

        // The way back to the panel while touch control takes every tap: a tab on the left edge.
        Box(
            Modifier
                .align(Alignment.CenterStart)
                .size(width = 18.dp, height = 72.dp)
                .clip(RoundedCornerShape(topEnd = 10.dp, bottomEnd = 10.dp))
                .background(Color(0x66FFFFFF))
                .clickable { panelOpen = true },
        )

        if (panelOpen) {
            Box(
                Modifier
                    .fillMaxSize()
                    .background(Color(0x66000000))
                    .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) { panelOpen = false },
            )
            Box(Modifier.align(Alignment.CenterEnd)) {
                SettingsPanel(receiver, state, firstFocus) { panelOpen = false }
            }
        }
    }
}
