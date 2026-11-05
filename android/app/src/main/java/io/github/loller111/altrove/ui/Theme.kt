package io.github.loller111.altrove.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// Same palette as the Windows app (src/Altrove.App/Themes/Dark.xaml).
object AltroveColors {
    val Background = Color(0xFF15161B)
    val Panel = Color(0xFF1E2027)
    val Panel2 = Color(0xFF282B34)
    val Line = Color(0xFF343846)
    val Text = Color(0xFFECEDF2)
    val TextDim = Color(0xFF9A9DAB)
    val Accent = Color(0xFF7FA7F5)
    val Danger = Color(0xFFF0716A)
    val Online = Color(0xFF5BC97F)
}

@Composable
fun AltroveTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = AltroveColors.Accent,
            onPrimary = Color(0xFF10131A),
            background = AltroveColors.Background,
            onBackground = AltroveColors.Text,
            surface = AltroveColors.Panel,
            onSurface = AltroveColors.Text,
            surfaceVariant = AltroveColors.Panel2,
            onSurfaceVariant = AltroveColors.TextDim,
            outline = AltroveColors.Line,
            error = AltroveColors.Danger,
        ),
        content = content,
    )
}
