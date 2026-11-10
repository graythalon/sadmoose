package com.graythalon.drift

import android.util.Base64
import org.json.JSONObject
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.Executors
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class SecurePeerClient(
    private val host: String,
    private val secret: ByteArray,
    private val peerId: String,
    private val displayName: String,
    private val onMessage: (JSONObject) -> Unit
) {
    @Volatile private var socket: Socket? = null
    @Volatile private var writer: BufferedWriter? = null
    @Volatile private var manuallyClosed = false
    private var sendKey: ByteArray? = null
    private var receiveKey: ByteArray? = null
    private var sendCounter = 0L
    private var receiveCounter = 0L
    private val sendLock = Any()
    private val sendExecutor = Executors.newSingleThreadExecutor { task -> Thread(task, "drift-send") }

    fun connect() {
        manuallyClosed = false
        Thread({
            var failed = false
            try {
                val sock = Socket(host, 42857).apply { tcpNoDelay = true; soTimeout = 0 }
                socket = sock
                val out = BufferedWriter(OutputStreamWriter(sock.getOutputStream(), StandardCharsets.UTF_8))
                writer = out
                val input = BufferedReader(InputStreamReader(sock.getInputStream(), StandardCharsets.UTF_8))
                val clientNonce = ByteArray(32).also { SecureRandom().nextBytes(it) }
                val nonceText = b64url(clientNonce)
                val proof = hmac(secret, "hello\n$peerId\n$nonceText".toByteArray()).let(::b64url)
                sendLine(JSONObject().put("type", "hello").put("peerId", peerId).put("displayName", displayName).put("clientNonce", nonceText).put("proof", proof))
                val welcomeLine = input.readLine() ?: error("The host closed the connection.")
                val welcome = JSONObject(welcomeLine)
                check(welcome.optString("type") == "welcome") { "Invalid host response." }
                val serverNonceText = welcome.getString("serverNonce")
                val expected = hmac(secret, "welcome\n$peerId\n$nonceText\n$serverNonceText".toByteArray())
                check(MessageDigest.isEqual(expected, fromB64url(welcome.getString("proof")))) { "Pairing key was rejected." }
                val salt = clientNonce + fromB64url(serverNonceText)
                sendKey = hkdf(secret, salt, "drift-v1/client-to-server")
                receiveKey = hkdf(secret, salt, "drift-v1/server-to-client")
                DriftRuntime.updateStatus("Connected")
                while (true) {
                    val line = input.readLine() ?: break
                    if (line.length > 131072) error("Frame is too large.")
                    onMessage(decrypt(JSONObject(line)))
                }
            } catch (e: Exception) {
                failed = true
                if (!manuallyClosed) DriftRuntime.updateStatus(e.message?.let { "Disconnected: $it" } ?: "Disconnected")
            } finally {
                closeSocket()
                if (!manuallyClosed && !failed) DriftRuntime.updateStatus("Disconnected")
            }
        }, "drift-network").start()
    }

    fun send(message: JSONObject) {
        try {
            val copy = JSONObject(message.toString())
            sendExecutor.execute {
                synchronized(sendLock) {
                    try {
                        val key = sendKey
                        if (key != null) {
                            sendCounter++
                            val nonce = nonce(sendCounter)
                            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                            cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
                            cipher.updateAAD(nonce)
                            val sealed = cipher.doFinal(copy.toString().toByteArray(StandardCharsets.UTF_8))
                            val body = sealed.copyOf(sealed.size - 16)
                            val tag = sealed.copyOfRange(sealed.size - 16, sealed.size)
                            sendLine(JSONObject().put("counter", sendCounter).put("ciphertext", Base64.encodeToString(body, Base64.NO_WRAP)).put("tag", Base64.encodeToString(tag, Base64.NO_WRAP)))
                        }
                    } catch (e: Exception) { DriftRuntime.updateStatus("Send failed: ${e.message}") }
                }
            }
        } catch (e: Exception) { DriftRuntime.updateStatus("Send failed: ${e.message}") }
    }

    private fun decrypt(frame: JSONObject): JSONObject {
        val counter = frame.getLong("counter")
        check(counter == receiveCounter + 1) { "Invalid frame sequence." }
        val ciphertext = Base64.decode(frame.getString("ciphertext"), Base64.DEFAULT)
        val tag = Base64.decode(frame.getString("tag"), Base64.DEFAULT)
        val nonce = nonce(counter)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(receiveKey!!, "AES"), GCMParameterSpec(128, nonce))
        cipher.updateAAD(nonce)
        val clear = cipher.doFinal(ciphertext + tag)
        receiveCounter = counter
        return JSONObject(String(clear, StandardCharsets.UTF_8))
    }

    private fun sendLine(value: JSONObject) { synchronized(sendLock) { writer?.apply { write(value.toString()); write("\n"); flush() } ?: error("Not connected.") } }
    fun close() { manuallyClosed = true; closeSocket() }
    private fun closeSocket() { try { socket?.close() } catch (_: Exception) { }; socket = null; writer = null; sendKey = null; receiveKey = null; sendExecutor.shutdownNow() }

    private fun nonce(counter: Long): ByteArray = ByteArray(12).also { bytes ->
        "DRFT".toByteArray().copyInto(bytes, 0)
        ByteBuffer.wrap(bytes, 4, 8).order(ByteOrder.BIG_ENDIAN).putLong(counter)
    }
    private fun hmac(key: ByteArray, data: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); doFinal(data) }
    private fun hkdf(ikm: ByteArray, salt: ByteArray, info: String): ByteArray {
        val prk = hmac(salt, ikm)
        return hmac(prk, info.toByteArray() + byteArrayOf(1))
    }
    private fun b64url(bytes: ByteArray): String = Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
    private fun fromB64url(text: String): ByteArray = Base64.decode(text, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
}
