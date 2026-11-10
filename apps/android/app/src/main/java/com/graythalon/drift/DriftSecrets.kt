package com.graythalon.drift

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

object DriftSecrets {
    private const val alias = "drift.pairing.key"
    private const val prefs = "drift.secure"
    private const val valueName = "pairing.secret"

    fun save(context: Context, secret: ByteArray) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(secret)
        val sealed = cipher.iv + encrypted
        context.getSharedPreferences(prefs, Context.MODE_PRIVATE).edit().putString(valueName, Base64.encodeToString(sealed, Base64.NO_WRAP)).apply()
    }

    fun load(context: Context): ByteArray? {
        val stored = context.getSharedPreferences(prefs, Context.MODE_PRIVATE).getString(valueName, null) ?: return null
        return try {
            val sealed = Base64.decode(stored, Base64.DEFAULT)
            if (sealed.size < 29) return null
            val iv = sealed.copyOfRange(0, 12)
            val ciphertext = sealed.copyOfRange(12, sealed.size)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
            cipher.doFinal(ciphertext)
        } catch (_: Exception) { null }
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = store.getKey(alias, null) as? SecretKey
        if (existing != null) return existing
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setRandomizedEncryptionRequired(true)
            .build())
        return generator.generateKey()
    }
}
