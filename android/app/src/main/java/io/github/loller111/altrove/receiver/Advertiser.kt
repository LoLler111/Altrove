package io.github.loller111.altrove.receiver

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import io.github.loller111.altrove.protocol.AltroveProtocol

/** Announces this receiver as _altrove._tcp so the Windows app lists it by itself. */
class Advertiser(context: Context) {
    private val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var registration: NsdManager.RegistrationListener? = null

    fun start(name: String, id: String, port: Int) {
        stop()
        val info = NsdServiceInfo().apply {
            serviceName = name
            serviceType = AltroveProtocol.SERVICE_TYPE
            setPort(port)
            setAttribute("id", id)
            setAttribute("name", name)
            setAttribute("protocol", AltroveProtocol.VERSION.toString())
        }
        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {
                Log.i(TAG, "Announced as ${info.serviceName}")
            }

            override fun onRegistrationFailed(info: NsdServiceInfo, error: Int) {
                Log.w(TAG, "Announcement failed: $error")
            }
            override fun onServiceUnregistered(info: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(info: NsdServiceInfo, error: Int) = Unit
        }
        registration = listener
        try {
            nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "Cannot announce", e)
            registration = null
        }
    }

    fun stop() {
        registration?.let {
            try {
                nsd.unregisterService(it)
            } catch (_: IllegalArgumentException) {
            }
        }
        registration = null
    }

    private companion object {
        const val TAG = "AltroveNsd"
    }
}
