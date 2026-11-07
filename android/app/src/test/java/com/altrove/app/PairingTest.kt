package com.altrove.app

import com.altrove.app.protocol.PairRequest
import com.altrove.app.receiver.Pairing
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Random

class PairingTest {
    private class MapStore : Pairing.Store {
        val map = HashMap<String, String>()
        override fun all(): Map<String, String> = map
        override fun put(senderId: String, value: String) {
            map[senderId] = value
        }
        override fun clear() = map.clear()
    }

    private val store = MapStore()
    private val pairing = Pairing(store, Random(42))

    private fun shownPin(answer: Pairing.Answer) = (answer as Pairing.Answer.ShowPin).pin

    @Test
    fun a_new_pc_gets_a_pin_and_then_a_token_that_opens_next_time() {
        val ask = pairing.onPair("pc", "PC-FISSO", PairRequest())
        val pin = shownPin(ask)
        assertEquals(6, pin.length)
        assertTrue(pin.all(Char::isDigit))
        assertFalse((ask as Pairing.Answer.ShowPin).wrong)

        val accepted = pairing.onPair("pc", "PC-FISSO", PairRequest(pin = pin)) as Pairing.Answer.Accepted
        assertEquals(32, accepted.token.length)
        // Only a hash is kept, with the name for the screen.
        assertFalse(store.map.getValue("pc").contains(accepted.token))
        assertEquals(listOf("PC-FISSO"), pairing.trustedNames)

        assertEquals(Pairing.Answer.Accepted(accepted.token), pairing.onPair("pc", "PC-FISSO", PairRequest(token = accepted.token)))
    }

    @Test
    fun a_wrong_or_foreign_token_asks_for_a_pin() {
        val pin = shownPin(pairing.onPair("pc", "PC", PairRequest()))
        val token = (pairing.onPair("pc", "PC", PairRequest(pin = pin)) as Pairing.Answer.Accepted).token
        assertTrue(pairing.onPair("pc", "PC", PairRequest(token = "guess")) is Pairing.Answer.ShowPin)
        // Another PC cannot use this PC's token.
        assertTrue(pairing.onPair("other", "Altro", PairRequest(token = token)) is Pairing.Answer.ShowPin)
    }

    @Test
    fun three_wrong_pins_refuse_and_the_pin_changes_next_time() {
        val pin = shownPin(pairing.onPair("pc", "PC", PairRequest()))
        val wrong = if (pin == "000000") "111111" else "000000"
        assertEquals(Pairing.Answer.ShowPin(pin, wrong = true), pairing.onPair("pc", "PC", PairRequest(pin = wrong)))
        assertEquals(Pairing.Answer.ShowPin(pin, wrong = true), pairing.onPair("pc", "PC", PairRequest(pin = wrong)))
        assertEquals(Pairing.Answer.Refused, pairing.onPair("pc", "PC", PairRequest(pin = wrong)))
        // After a refusal the old PIN is worth nothing: a fresh one is shown.
        assertTrue(pairing.onPair("pc", "PC", PairRequest(pin = pin)) is Pairing.Answer.ShowPin)
        assertTrue(store.map.isEmpty())
    }

    @Test
    fun a_pin_only_counts_for_the_pc_it_was_shown_to() {
        val pin = shownPin(pairing.onPair("pc", "PC", PairRequest()))
        assertTrue(pairing.onPair("other", "Altro", PairRequest(pin = pin)) is Pairing.Answer.ShowPin)
        pairing.cancel("other")
        assertTrue(pairing.onPair("pc", "PC", PairRequest(pin = pin)) is Pairing.Answer.ShowPin)
    }

    @Test
    fun forgetting_everyone_asks_for_pins_again() {
        val pin = shownPin(pairing.onPair("pc", "PC", PairRequest()))
        val token = (pairing.onPair("pc", "PC", PairRequest(pin = pin)) as Pairing.Answer.Accepted).token
        pairing.forgetAll()
        assertTrue(pairing.trustedNames.isEmpty())
        assertTrue(pairing.onPair("pc", "PC", PairRequest(token = token)) is Pairing.Answer.ShowPin)
    }
}
