package com.graythalon.drift

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONObject
import java.util.UUID

class MainActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var host: EditText
    private lateinit var pairingCode: EditText
    private lateinit var shareSwitch: CheckBox
    private var connectButton: Button? = null
    private val peerId by lazy { getPreferences(MODE_PRIVATE).getString("peerId", null) ?: UUID.randomUUID().toString().also { getPreferences(MODE_PRIVATE).edit().putString("peerId", it).apply() } }
    companion object { @Volatile var instance: MainActivity? = null }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        instance = this
        DriftRuntime.peerId = peerId
        window.statusBarColor = 0xFF0C1017.toInt()
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(28, 30, 28, 24); setBackgroundColor(0xFF0C1017.toInt()) }
        val scroll = ScrollView(this).apply { addView(root) }
        setContentView(scroll)
        root.addView(label("DRIFT  /  ANDROID CLIENT", 0xFFC8F36A.toInt(), 12f, true))
        root.addView(label("One pointer. Every screen.", 0xFFF3F5F8.toInt(), 25f, true).apply { setPadding(0, 6, 0, 20) })
        root.addView(label("CONNECT TO YOUR WINDOWS HUB", 0xFF929BAA.toInt(), 11f, true))
        host = EditText(this).apply { hint = "PC address, e.g. 192.168.1.25"; setSingleLine(true); setText(getPreferences(MODE_PRIVATE).getString("host", "")); setPadding(12, 10, 12, 10) }
        root.addView(host, marginParams())
        pairingCode = EditText(this).apply {
            hint = "Pairing key"; setSingleLine(true); inputType = 0x00000081; setPadding(12, 10, 12, 10)
            DriftSecrets.load(this@MainActivity)?.let { setText(android.util.Base64.encodeToString(it, android.util.Base64.URL_SAFE or android.util.Base64.NO_PADDING or android.util.Base64.NO_WRAP)) }
        }
        root.addView(pairingCode, marginParams())
        connectButton = Button(this).apply { text = "Connect"; setOnClickListener { connect() } }
        root.addView(connectButton, marginParams())
        status = label(DriftRuntime.status, 0xFFBAC2D0.toInt(), 12f, false)
        root.addView(status, marginParams())
        val card = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(16, 18, 16, 16); setBackgroundColor(0xFF151B25.toInt()) }
        val serviceButton = Button(this).apply { text = "Enable Drift Accessibility Service"; setOnClickListener { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) } }
        card.addView(label("INPUT ON THIS DEVICE", 0xFFC8F36A.toInt(), 11f, true))
        card.addView(label("Allow the accessibility service to send pointer gestures and forward hardware keys. You control this permission in Android Settings.", 0xFFADB6C4.toInt(), 12f, false).apply { setPadding(0, 10, 0, 8) })
        card.addView(serviceButton)
        shareSwitch = CheckBox(this).apply {
            text = "Send this device’s keyboard and mouse to other screens"
            setTextColor(0xFFF3F5F8.toInt())
            isEnabled = DriftRuntime.hostSharing
            setOnCheckedChangeListener { _, enabled ->
                if (enabled && !DriftRuntime.hostSharing) {
                    isChecked = false
                    status.text = "Start sharing on your Windows PC first."
                    return@setOnCheckedChangeListener
                }
                DriftRuntime.sharing = enabled
                DriftAccessibilityService.instance?.setSharing(enabled)
                if (enabled && !DriftAccessibilityService.isEnabled(this@MainActivity)) {
                    isChecked = false
                    DriftRuntime.sharing = false
                    status.text = "Enable the Accessibility Service first."
                }
            }
        }
        root.addView(TextView(this).apply { text = ""; height = 20 })
        root.addView(card)
        root.addView(shareSwitch, marginParams())
        root.addView(label("INPUT METHOD", 0xFF929BAA.toInt(), 11f, true).apply { setPadding(0, 18, 0, 2) })
        root.addView(label("To receive keyboard input in Android apps, enable Drift Keyboard in Settings → System → Keyboard, then select it while a text field is focused. Android limits system-wide key injection; secure screens and some apps may block forwarded actions.", 0xFFADB6C4.toInt(), 11f, false))
        root.addView(Button(this).apply { text = "Set up Drift Keyboard"; setOnClickListener { startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)) } }, marginParams())
        root.addView(label("Encrypted local connection · Input sharing is off until you turn it on", 0xFF737D8D.toInt(), 10f, false).apply { setPadding(0, 20, 0, 0) })
    }

    private fun connect() {
        if (DriftRuntime.client != null) {
            DriftRuntime.client?.close(); DriftRuntime.client = null
            DriftRuntime.hostSharing = false
            DriftRuntime.sharing = false
            DriftAccessibilityService.instance?.setSharing(false)
            shareSwitch.isChecked = false
            shareSwitch.isEnabled = false
            DriftRuntime.updateStatus("Not connected")
            connectButton?.text = "Connect"
            return
        }
        val ip = host.text.toString().trim()
        val code = pairingCode.text.toString().trim()
        val secret = try { android.util.Base64.decode(code, android.util.Base64.URL_SAFE or android.util.Base64.NO_PADDING or android.util.Base64.NO_WRAP) } catch (_: Exception) { byteArrayOf() }
        if (ip.isBlank() || secret.size != 32) { status.text = "Enter the PC address and 43-character pairing key."; return }
        getPreferences(MODE_PRIVATE).edit().putString("host", ip).apply()
        DriftSecrets.save(this, secret)
        connectButton?.text = "Disconnect"
        val client = SecurePeerClient(ip, secret, peerId, android.os.Build.MODEL) { message ->
            when (message.optString("kind")) {
                "sharing" -> DriftRuntime.main.post {
                    DriftRuntime.hostSharing = message.optBoolean("enabled")
                    DriftRuntime.hostMouseSharing = message.optBoolean("mouse", true)
                    DriftRuntime.hostKeyboardSharing = message.optBoolean("keyboard", true)
                    DriftAccessibilityService.instance?.refreshFocus()
                    if (!DriftRuntime.hostSharing) { DriftRuntime.sharing = false; DriftAccessibilityService.instance?.setSharing(false); shareSwitch.isChecked = false }
                    shareSwitch.isEnabled = DriftRuntime.hostSharing
                }
                "focus" -> { DriftRuntime.focusedTarget = message.optString("target", "windows"); DriftRuntime.inject(message) }
                "pointer", "key", "text" -> DriftRuntime.inject(message)
            }
        }
        DriftRuntime.client?.close()
        DriftRuntime.client = client
        client.connect()
        status.text = "Connecting securely…"
    }

    fun refreshStatus(value: String) { status.text = value; connectButton?.text = if (value == "Connected" || value.startsWith("Connecting")) "Disconnect" else "Connect" }
    fun onDisconnected(value: String) {
        status.text = value
        connectButton?.text = "Connect"
        shareSwitch.isChecked = false
        shareSwitch.isEnabled = false
    }
    override fun onDestroy() { if (instance === this) instance = null; super.onDestroy() }
    private fun label(value: String, color: Int, size: Float, bold: Boolean) = TextView(this).apply { text = value; setTextColor(color); textSize = size; if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD); gravity = Gravity.START }
    private fun marginParams() = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = 10 }
}
