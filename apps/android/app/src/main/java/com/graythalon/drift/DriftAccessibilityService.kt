package com.graythalon.drift

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.accessibilityservice.GestureDescription
import android.content.Context
import android.graphics.Path
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.PointF
import android.os.Build
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.inputmethod.InputMethodManager
import org.json.JSONObject
import kotlin.math.max
import kotlin.math.min

class DriftAccessibilityService : AccessibilityService() {
    private var sharing = false
    private var pointerX = 200f
    private var pointerY = 300f
    private var lastSourceX = 0f
    private var lastSourceY = 0f
    private var sourceInitialized = false
    private var buttonDown = false
    private val sourceButtonsDown = mutableSetOf<String>()
    private val sourceKeysDown = mutableSetOf<Int>()
    private var edgeHandoffPending = false
    private var dragStroke: GestureDescription.StrokeDescription? = null
    private var dragPosition = PointF()
    private var dragDispatching = false
    private val dragQueue = java.util.ArrayDeque<DragPoint>()
    private var pointerOverlay: PointerOverlay? = null
    private lateinit var windowManager: WindowManager

    companion object {
        @Volatile var instance: DriftAccessibilityService? = null
        private val supportedAndroidKeyCodes = setOf(4, 21, 19, 22, 20, 66, 61, 67, 112, 74, 70, 55, 69, 56, 76, 68, 71, 73, 72, 75, 59, 60, 113, 114, 57, 58, 115, 117, 118, 62, 122, 123, 92, 93, 120)
        fun isEnabled(context: Context): Boolean {
            val expected = "${context.packageName}/${DriftAccessibilityService::class.java.name}"
            return android.provider.Settings.Secure.getString(context.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)?.split(":")?.any { it.equals(expected, true) } == true
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        windowManager = getSystemService(WINDOW_SERVICE) as WindowManager
        pointerOverlay = PointerOverlay(this)
        val overlayParams = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT
        ).apply { gravity = Gravity.TOP or Gravity.START }
        windowManager.addView(pointerOverlay, overlayParams)
        val info = serviceInfo ?: AccessibilityServiceInfo()
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_REQUEST_FILTER_KEY_EVENTS
        if (Build.VERSION.SDK_INT >= 34) info.flags = info.flags or AccessibilityServiceInfo.FLAG_SEND_MOTION_EVENTS
        serviceInfo = info
        setSharing(DriftRuntime.sharing)
    }

    fun setSharing(enabled: Boolean) {
        if (!enabled && buttonDown) finishDrag()
        if (!enabled) {
            sourceKeysDown.forEach { DriftRuntime.send(JSONObject().put("kind", "key").put("platformCode", "android").put("keyCode", it).put("down", false)) }
            sourceKeysDown.clear()
            sourceButtonsDown.forEach { DriftRuntime.send(JSONObject().put("kind", "pointer").put("action", "button").put("button", it).put("down", false)) }
            sourceButtonsDown.clear()
            edgeHandoffPending = false
        }
        sharing = enabled
        if (!enabled) { buttonDown = false; pointerOverlay?.moveTo(pointerX, pointerY, false) }
        if (Build.VERSION.SDK_INT >= 34) {
            val info = serviceInfo ?: AccessibilityServiceInfo()
            info.setMotionEventSources(if (enabled) InputDevice.SOURCE_MOUSE or InputDevice.SOURCE_TOUCHPAD else 0)
            serviceInfo = info
        }
    }

    fun refreshFocus() {
        pointerOverlay?.moveTo(pointerX, pointerY, DriftRuntime.hostSharing && DriftRuntime.focusedTarget == DriftRuntime.peerId)
    }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    override fun onKeyEvent(event: KeyEvent): Boolean {
        if (!sharing || DriftRuntime.client == null) return false
        if (!DriftRuntime.hostKeyboardSharing) { sourceKeysDown.clear(); return false }
        if (DriftRuntime.focusedTarget == DriftRuntime.peerId) {
            if (event.action == KeyEvent.ACTION_UP) sourceKeysDown.remove(event.keyCode)
            return false
        }
        if (!isSupportedAndroidKey(event.keyCode)) return false
        if (event.action == KeyEvent.ACTION_DOWN) sourceKeysDown.add(event.keyCode) else sourceKeysDown.remove(event.keyCode)
        DriftRuntime.send(JSONObject().put("kind", "key").put("platformCode", "android").put("keyCode", event.keyCode).put("down", event.action == KeyEvent.ACTION_DOWN).put("repeat", event.repeatCount))
        return true
    }

    override fun onMotionEvent(event: MotionEvent) {
        super.onMotionEvent(event)
        if (!sharing || DriftRuntime.client == null) return
        val source = event.source
        val isPointer = (source and (InputDevice.SOURCE_MOUSE or InputDevice.SOURCE_TOUCHPAD)) != 0
        if (!isPointer) return
        if (event.actionMasked == MotionEvent.ACTION_BUTTON_PRESS) sourceButtonsDown.add(buttonName(event.actionButton))
        if (event.actionMasked == MotionEvent.ACTION_BUTTON_RELEASE) sourceButtonsDown.remove(buttonName(event.actionButton))
        if (!DriftRuntime.hostMouseSharing) return
        if (DriftRuntime.focusedTarget == DriftRuntime.peerId) {
            pointerX = event.x.coerceIn(1f, resources.displayMetrics.widthPixels - 1f)
            pointerY = event.y.coerceIn(1f, resources.displayMetrics.heightPixels - 1f)
            pointerOverlay?.moveTo(pointerX, pointerY, true)
            if (!edgeHandoffPending && sourceButtonsDown.isEmpty() && (event.actionMasked == MotionEvent.ACTION_MOVE || event.actionMasked == MotionEvent.ACTION_HOVER_MOVE)) {
                edgeAt(pointerX, pointerY)?.let { requestEdge(it); return }
            }
            return
        }
        if (!sourceInitialized) { lastSourceX = event.x; lastSourceY = event.y; sourceInitialized = true }
        val dx = event.x - lastSourceX
        val dy = event.y - lastSourceY
        lastSourceX = event.x; lastSourceY = event.y
        when (event.actionMasked) {
            MotionEvent.ACTION_MOVE, MotionEvent.ACTION_HOVER_MOVE -> if (dx != 0f || dy != 0f) DriftRuntime.send(JSONObject().put("kind", "pointer").put("action", "move").put("dx", dx.toInt()).put("dy", dy.toInt()))
            MotionEvent.ACTION_BUTTON_PRESS -> DriftRuntime.send(JSONObject().put("kind", "pointer").put("action", "button").put("button", buttonName(event.actionButton)).put("down", true))
            MotionEvent.ACTION_BUTTON_RELEASE -> DriftRuntime.send(JSONObject().put("kind", "pointer").put("action", "button").put("button", buttonName(event.actionButton)).put("down", false))
            MotionEvent.ACTION_SCROLL -> DriftRuntime.send(JSONObject().put("kind", "pointer").put("action", "wheel").put("wheel", -event.getAxisValue(MotionEvent.AXIS_VSCROLL).toInt()))
        }
    }

    fun inject(message: JSONObject) {
        if (message.optString("kind") == "focus") {
            if (DriftRuntime.focusedTarget == DriftRuntime.peerId && message.optString("target") != DriftRuntime.peerId && buttonDown) finishDrag()
            DriftRuntime.focusedTarget = message.optString("target", "windows")
            edgeHandoffPending = false
            pointerX = (message.optDouble("x", 0.5) * resources.displayMetrics.widthPixels).toFloat().coerceIn(2f, resources.displayMetrics.widthPixels - 2f)
            pointerY = (message.optDouble("y", 0.5) * resources.displayMetrics.heightPixels).toFloat().coerceIn(2f, resources.displayMetrics.heightPixels - 2f)
            refreshFocus()
            return
        }
        if (!DriftRuntime.hostSharing) return
        when (message.optString("kind")) {
            "pointer" -> injectPointer(message)
            "key" -> injectKey(message)
            "text" -> DriftImeService.commit(message.optString("text", ""))
        }
    }

    private fun injectPointer(message: JSONObject) {
        val action = message.optString("action")
        if (action == "move") {
            val width = resources.displayMetrics.widthPixels.toFloat()
            val height = resources.displayMetrics.heightPixels.toFloat()
            val nextX = (pointerX + message.optInt("dx")).coerceIn(1f, width - 1f)
            val nextY = (pointerY + message.optInt("dy")).coerceIn(1f, height - 1f)
            pointerX = nextX; pointerY = nextY
            pointerOverlay?.moveTo(pointerX, pointerY, true)
            if (buttonDown) enqueueDrag(pointerX, pointerY, finish = false)
            if (!buttonDown && DriftRuntime.focusedTarget == DriftRuntime.peerId) edgeAt(nextX, nextY)?.let(::requestEdge)
        } else if (action == "button") {
            val down = message.optBoolean("down")
            val button = message.optString("button")
            if (button == "left") {
                if (down && !buttonDown) startDrag()
                if (!down && buttonDown) finishDrag()
            } else if (down) tap(pointerX, pointerY, button == "right")
            buttonDown = button == "left" && down
        } else if (action == "wheel") {
            val nextY = (pointerY + message.optInt("wheel") * 24f).coerceIn(1f, resources.displayMetrics.heightPixels - 1f)
            val path = Path().apply { moveTo(pointerX, pointerY); lineTo(pointerX, nextY) }
            dispatchGesture(GestureDescription.Builder().addStroke(GestureDescription.StrokeDescription(path, 0, 180)).build(), null, null)
            pointerY = nextY
        }
    }

    private fun tap(x: Float, y: Float, longPress: Boolean = false) {
        val path = Path().apply { moveTo(x, y) }
        dispatchGesture(GestureDescription.Builder().addStroke(GestureDescription.StrokeDescription(path, 0, if (longPress) 700 else 80)).build(), null, null)
    }

    private fun startDrag() {
        val path = Path().apply { moveTo(pointerX, pointerY); lineTo(pointerX, pointerY) }
        val stroke = GestureDescription.StrokeDescription(path, 0, 24, true)
        dragStroke = stroke
        dragPosition.set(pointerX, pointerY)
        val dispatched = dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) { dragDispatching = false; flushDrag() }
            override fun onCancelled(gestureDescription: GestureDescription?) { dragDispatching = false; dragStroke = null; buttonDown = false; dragQueue.clear() }
        }, null)
        dragDispatching = dispatched
        if (!dispatched) dragStroke = null
    }

    private fun enqueueDrag(x: Float, y: Float, finish: Boolean) {
        if (!finish && dragQueue.size >= 4) dragQueue.removeLast()
        dragQueue.addLast(DragPoint(x, y, finish))
        flushDrag()
    }

    private fun finishDrag() {
        enqueueDrag(pointerX, pointerY, finish = true)
    }

    private fun flushDrag() {
        if (dragDispatching) return
        val previous = dragStroke ?: return
        val point = dragQueue.pollFirst() ?: return
        val path = Path().apply { moveTo(dragPosition.x, dragPosition.y); lineTo(point.x, point.y) }
        val distance = kotlin.math.hypot((point.x - dragPosition.x).toDouble(), (point.y - dragPosition.y).toDouble())
        val duration = max(1L, (distance / 2.0).toLong())
        val continued = previous.continueStroke(path, 0, duration, !point.finish)
        val dispatched = dispatchGesture(GestureDescription.Builder().addStroke(continued).build(), object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) {
                dragStroke = if (point.finish) null else continued
                dragPosition.set(point.x, point.y)
                dragDispatching = false
                if (point.finish) dragQueue.clear() else flushDrag()
            }
            override fun onCancelled(gestureDescription: GestureDescription?) { dragDispatching = false; dragStroke = null; buttonDown = false; dragQueue.clear() }
        }, null)
        dragDispatching = dispatched
        if (!dispatched) { dragStroke = null; dragQueue.clear() }
    }

    private fun injectKey(message: JSONObject) {
        val keyCode = if (message.optString("platformCode") == "android") message.optInt("keyCode") else virtualKeyToAndroid(message.optInt("vk"))
        if (keyCode == 0) return
        val down = message.optBoolean("down")
        if (keyCode == KeyEvent.KEYCODE_BACK && down) { performGlobalAction(GLOBAL_ACTION_BACK); return }
        if (keyCode == KeyEvent.KEYCODE_HOME && down) { performGlobalAction(GLOBAL_ACTION_HOME); return }
        val event = KeyEvent(if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP, keyCode)
        DriftImeService.sendKey(event)
    }

    private fun virtualKeyToAndroid(vk: Int): Int = when (vk) {
        in 0x41..0x5A -> KeyEvent.KEYCODE_A + (vk - 0x41)
        in 0x30..0x39 -> KeyEvent.KEYCODE_0 + (vk - 0x30)
        in 0x70..0x7B -> KeyEvent.KEYCODE_F1 + (vk - 0x70)
        8 -> KeyEvent.KEYCODE_DEL; 9 -> KeyEvent.KEYCODE_TAB; 13 -> KeyEvent.KEYCODE_ENTER; 27 -> KeyEvent.KEYCODE_ESCAPE; 0x2E -> KeyEvent.KEYCODE_FORWARD_DEL
        0x21 -> KeyEvent.KEYCODE_PAGE_UP; 0x22 -> KeyEvent.KEYCODE_PAGE_DOWN; 0x23 -> KeyEvent.KEYCODE_MOVE_END; 0x24 -> KeyEvent.KEYCODE_MOVE_HOME
        37 -> KeyEvent.KEYCODE_DPAD_LEFT; 38 -> KeyEvent.KEYCODE_DPAD_UP; 39 -> KeyEvent.KEYCODE_DPAD_RIGHT; 40 -> KeyEvent.KEYCODE_DPAD_DOWN
        16 -> KeyEvent.KEYCODE_SHIFT_LEFT; 17 -> KeyEvent.KEYCODE_CTRL_LEFT; 18 -> KeyEvent.KEYCODE_ALT_LEFT; 0x14 -> KeyEvent.KEYCODE_CAPS_LOCK; 91 -> KeyEvent.KEYCODE_META_LEFT; 92 -> KeyEvent.KEYCODE_META_RIGHT
        0xBA -> KeyEvent.KEYCODE_SEMICOLON; 0xBB -> KeyEvent.KEYCODE_EQUALS; 0xBC -> KeyEvent.KEYCODE_COMMA; 0xBD -> KeyEvent.KEYCODE_MINUS
        0xBE -> KeyEvent.KEYCODE_PERIOD; 0xBF -> KeyEvent.KEYCODE_SLASH; 0xC0 -> KeyEvent.KEYCODE_GRAVE; 0xDB -> KeyEvent.KEYCODE_LEFT_BRACKET
        0xDC -> KeyEvent.KEYCODE_BACKSLASH; 0xDD -> KeyEvent.KEYCODE_RIGHT_BRACKET; 0xDE -> KeyEvent.KEYCODE_APOSTROPHE; else -> 0
    }

    private fun buttonName(button: Int): String = when (button) {
        MotionEvent.BUTTON_SECONDARY -> "right"
        MotionEvent.BUTTON_TERTIARY -> "middle"
        else -> "left"
    }

    private fun isSupportedAndroidKey(keyCode: Int): Boolean =
        keyCode in 7..16 || keyCode in 29..54 || keyCode in 131..142 || keyCode in supportedAndroidKeyCodes

    private fun edgeAt(x: Float, y: Float): String? {
        val width = resources.displayMetrics.widthPixels.toFloat()
        val height = resources.displayMetrics.heightPixels.toFloat()
        return when {
            x <= 2f -> "left"
            x >= width - 2f -> "right"
            y <= 2f -> "top"
            y >= height - 2f -> "bottom"
            else -> null
        }
    }

    private fun requestEdge(edge: String) {
        if (edgeHandoffPending) return
        edgeHandoffPending = true
        DriftRuntime.send(JSONObject().put("kind", "edge").put("edge", edge))
    }

    override fun onDestroy() {
        try { pointerOverlay?.let { windowManager.removeView(it) } } catch (_: Exception) { }
        pointerOverlay = null
        if (instance === this) instance = null
        super.onDestroy()
    }
}

private data class DragPoint(val x: Float, val y: Float, val finish: Boolean)

private class PointerOverlay(context: Context) : View(context) {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFC8F36A.toInt(); style = Paint.Style.FILL; setShadowLayer(8f, 0f, 2f, 0x99000000.toInt()) }
    @Volatile private var x = 0f
    @Volatile private var y = 0f
    @Volatile private var visible = false
    init { setLayerType(LAYER_TYPE_SOFTWARE, null) }
    fun moveTo(nextX: Float, nextY: Float, show: Boolean) { x = nextX; y = nextY; visible = show; postInvalidate() }
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (visible) {
            canvas.drawCircle(x, y, 8f, paint)
            paint.style = Paint.Style.STROKE; paint.strokeWidth = 2f; paint.color = 0xFFFFFFFF.toInt()
            canvas.drawCircle(x, y, 8f, paint)
            paint.style = Paint.Style.FILL; paint.color = 0xFFC8F36A.toInt()
        }
    }
}
