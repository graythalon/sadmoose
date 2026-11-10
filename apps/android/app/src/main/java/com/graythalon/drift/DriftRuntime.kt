package com.graythalon.drift

import android.os.Handler
import android.os.Looper
import org.json.JSONObject

object DriftRuntime {
    @Volatile var client: SecurePeerClient? = null
    @Volatile var peerId: String = "android"
    @Volatile var sharing: Boolean = false
    @Volatile var hostSharing: Boolean = false
    @Volatile var hostMouseSharing: Boolean = true
    @Volatile var hostKeyboardSharing: Boolean = true
    @Volatile var focusedTarget: String = "windows"
    @Volatile var status: String = "Not connected"
    val main = Handler(Looper.getMainLooper())
    fun send(message: JSONObject) { client?.send(message) }
    fun updateStatus(value: String) {
        status = value
        if (value.startsWith("Disconnected")) { client = null; hostSharing = false; sharing = false }
        main.post {
            if (value.startsWith("Disconnected")) {
                DriftAccessibilityService.instance?.setSharing(false)
                MainActivity.instance?.onDisconnected(value)
            } else MainActivity.instance?.refreshStatus(value)
        }
    }
    fun inject(message: JSONObject) { main.post { DriftAccessibilityService.instance?.inject(message) } }
}
