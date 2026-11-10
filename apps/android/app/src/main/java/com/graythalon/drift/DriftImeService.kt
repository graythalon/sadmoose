package com.graythalon.drift

import android.inputmethodservice.InputMethodService
import android.view.KeyEvent
import android.view.View
import android.widget.TextView

class DriftImeService : InputMethodService() {
    companion object {
        @Volatile var current: DriftImeService? = null
        fun commit(text: String) { current?.currentInputConnection?.commitText(text, 1) }
        fun sendKey(event: KeyEvent) { current?.currentInputConnection?.sendKeyEvent(event) }
    }
    override fun onCreate() { super.onCreate(); current = this }
    override fun onCreateInputView(): View = TextView(this).apply {
        text = "Drift Keyboard is active. Text from your paired device will be entered here."
        textSize = 14f; setPadding(20, 16, 20, 16); setBackgroundColor(0xFF171D29.toInt()); setTextColor(0xFFE9EDF5.toInt())
    }
    override fun onDestroy() { if (current === this) current = null; super.onDestroy() }
}
