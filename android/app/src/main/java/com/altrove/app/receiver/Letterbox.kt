package com.altrove.app.receiver

/** The PC's picture inside a screen of another shape: as big as fits, black bars on the rest. */
object Letterbox {
    /** The size of a [srcWidth]×[srcHeight] picture scaled to fit inside [boxWidth]×[boxHeight]. */
    fun fit(srcWidth: Int, srcHeight: Int, boxWidth: Int, boxHeight: Int): Pair<Int, Int> {
        if (srcWidth <= 0 || srcHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) return boxWidth.coerceAtLeast(0) to boxHeight.coerceAtLeast(0)
        // Compare the shapes without rounding: wider box than picture means bars left and right.
        return if (boxWidth.toLong() * srcHeight > boxHeight.toLong() * srcWidth) {
            (boxHeight.toLong() * srcWidth / srcHeight).toInt() to boxHeight
        } else {
            boxWidth to (boxWidth.toLong() * srcHeight / srcWidth).toInt()
        }
    }
}
