package com.altrove.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.altrove.app.BuildConfig
import com.altrove.app.update.UpdateController
import com.altrove.app.update.UpdateState

/** A strip at the bottom of the waiting screen when a newer Altrove is on GitHub. Nothing otherwise. */
@Composable
fun UpdateBanner(updates: UpdateController, modifier: Modifier = Modifier) {
    val state by updates.state.collectAsState()
    if (state is UpdateState.None) return
    Row(
        modifier
            .widthIn(max = 640.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(AltroveColors.Panel2)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            when (val s = state) {
                is UpdateState.Available -> Text("È disponibile Altrove ${s.version} (hai la ${BuildConfig.VERSION_NAME}).", color = AltroveColors.Text, fontSize = 15.sp)
                is UpdateState.Downloading -> {
                    Text("Scarico Altrove ${s.version}…", color = AltroveColors.Text, fontSize = 15.sp)
                    LinearProgressIndicator(progress = { s.progress })
                }
                is UpdateState.NeedsPermission -> Text(
                    "Per installare l'aggiornamento consenti ad Altrove di installare app, poi torna qui.",
                    color = AltroveColors.Text,
                    fontSize = 15.sp,
                )
                is UpdateState.Ready -> Text("Altrove ${s.version} è pronto: conferma l'installazione.", color = AltroveColors.Text, fontSize = 15.sp)
                is UpdateState.Failed -> Text(s.message, color = AltroveColors.Danger, fontSize = 15.sp)
                UpdateState.None -> Unit
            }
        }
        when (state) {
            is UpdateState.Available -> Button(onClick = updates::download) { Text("Aggiorna") }
            is UpdateState.NeedsPermission, is UpdateState.Ready -> Button(onClick = updates::install) { Text("Installa") }
            else -> Unit
        }
    }
}
