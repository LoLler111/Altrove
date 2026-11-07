package com.altrove.app.receiver

import android.content.SharedPreferences
import com.altrove.app.protocol.PairRequest
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Random

/**
 * The PCs this receiver trusts, and the PIN shown to a new one (docs/PROTOCOLLO.md, "Abbinamento").
 * Only a hash of each token is kept: the token itself lives on the PC.
 */
class Pairing(private val store: Store, private val random: Random = SecureRandom()) {
    /** Where the trusted PCs are kept: SharedPreferences in the app, a map in the tests. */
    interface Store {
        fun all(): Map<String, String>
        fun put(senderId: String, value: String)
        fun clear()
    }

    sealed interface Answer {
        /** Trusted: tell the PC [token] (the same it sent, or a new one after the PIN). */
        data class Accepted(val token: String) : Answer

        /** Show [pin] and ask the PC for it; [wrong] after a wrong attempt. */
        data class ShowPin(val pin: String, val wrong: Boolean) : Answer

        /** Too many wrong PINs. */
        data object Refused : Answer
    }

    private class Challenge(val senderId: String, val pin: String) {
        var wrongAttempts = 0
    }

    private var challenge: Challenge? = null

    /** The names of the trusted PCs, for the screen. */
    val trustedNames: List<String> get() = store.all().values.map { it.substringAfter('\t') }.sorted()

    @Synchronized
    fun onPair(senderId: String, senderName: String, request: PairRequest): Answer {
        request.token?.let { token ->
            val saved = store.all()[senderId]?.substringBefore('\t')
            if (saved != null && MessageDigest.isEqual(saved.toByteArray(), hash(token).toByteArray())) return Answer.Accepted(token)
        }
        val current = challenge?.takeIf { it.senderId == senderId }
        if (request.pin != null && current != null) {
            if (MessageDigest.isEqual(request.pin.toByteArray(), current.pin.toByteArray())) {
                challenge = null
                val token = newToken()
                store.put(senderId, hash(token) + "\t" + senderName.replace('\t', ' '))
                return Answer.Accepted(token)
            }
            current.wrongAttempts++
            if (current.wrongAttempts >= MAX_WRONG_ATTEMPTS) {
                challenge = null
                return Answer.Refused
            }
            return Answer.ShowPin(current.pin, wrong = true)
        }
        val fresh = Challenge(senderId, "%06d".format(random.nextInt(1_000_000)))
        challenge = fresh
        return Answer.ShowPin(fresh.pin, wrong = false)
    }

    /** The PC gave up, went away or was refused on this screen: its PIN is no longer valid. */
    @Synchronized
    fun cancel(senderId: String) {
        if (challenge?.senderId == senderId) challenge = null
    }

    @Synchronized
    fun forgetAll() {
        challenge = null
        store.clear()
    }

    private fun newToken(): String = ByteArray(16).also(random::nextBytes).joinToString("") { "%02x".format(it) }

    companion object {
        const val MAX_WRONG_ATTEMPTS = 3

        fun hash(token: String): String =
            MessageDigest.getInstance("SHA-256").digest(token.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    /** The app's store: one entry per trusted PC, "hash\tname". */
    class PreferencesStore(private val prefs: SharedPreferences) : Store {
        override fun all(): Map<String, String> = prefs.all.mapNotNull { (k, v) -> (v as? String)?.let { k to it } }.toMap()
        override fun put(senderId: String, value: String) = prefs.edit().putString(senderId, value).apply()
        override fun clear() = prefs.edit().clear().apply()
    }
}
