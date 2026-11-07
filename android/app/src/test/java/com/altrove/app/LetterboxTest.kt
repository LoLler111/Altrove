package com.altrove.app

import com.altrove.app.receiver.Letterbox
import org.junit.Assert.assertEquals
import org.junit.Test

class LetterboxTest {
    @Test
    fun samePictureFillsTheScreen() {
        assertEquals(1920 to 1080, Letterbox.fit(1920, 1080, 1920, 1080))
    }

    @Test
    fun widerScreenGetsBarsLeftAndRight() {
        // A 16:10 tablet picture on a 21:9 TV.
        assertEquals(1728 to 1080, Letterbox.fit(1920, 1200, 2520, 1080))
    }

    @Test
    fun tallerScreenGetsBarsAboveAndBelow() {
        // A 16:9 PC on a 16:10 TV.
        assertEquals(1920 to 1080, Letterbox.fit(2560, 1440, 1920, 1200))
    }

    @Test
    fun smallerPictureIsScaledUp() {
        assertEquals(1920 to 1080, Letterbox.fit(1280, 720, 1920, 1080))
    }

    @Test
    fun emptySizesDoNotDivideByZero() {
        assertEquals(1920 to 1080, Letterbox.fit(0, 0, 1920, 1080))
        assertEquals(0 to 0, Letterbox.fit(1920, 1080, 0, 0))
    }
}
