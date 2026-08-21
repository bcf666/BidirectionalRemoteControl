package com.remotecontrol.android.service

import android.graphics.Path
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.view.KeyEvent
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import com.remotecontrol.android.protocol.InputEvent

/**
 * 无障碍注入服务：把收到的 InputEvent（TOUCH_*、KEY_*、TEXT） 转化为真实输入。
 * - 触摸：dispatchGesture (Path / StrokeDescription)
 * - 文本：对当前聚焦的节点 ACTION_SET_TEXT
 * - 物理按键：映射到全局操作
 */
class InjectionDispatcher(private val service: AccessibilityService) {

    private val handler = Handler(Looper.getMainLooper())

    /** 记录每个 pointerId 最近一次路径起点，便于 MOVE 连续构建 */
    private val pointers = hashMapOf<Int, android.graphics.Path>()

    fun dispatch(ev: InputEvent, screenWidth: Int, screenHeight: Int) {
        val x = ev.x?.let { (it.coerceIn(0f, 1f) * screenWidth) } ?: 0f
        val y = ev.y?.let { (it.coerceIn(0f, 1f) * screenHeight) } ?: 0f
        val pid = ev.pointerId ?: 0

        when (ev.type) {
            "TOUCH_DOWN" -> {
                val path = Path().apply { moveTo(x, y) }
                pointers[pid] = path
                dispatchGesture(path, 0L, 20L)
            }
            "TOUCH_MOVE" -> {
                val prev = pointers[pid]
                if (prev != null) {
                    val p = Path().apply { moveTo(prev.lastX(), prev.lastY()); lineTo(x, y) }
                    dispatchGesture(p, 0L, 20L)
                    pointers[pid] = Path().apply { moveTo(x, y) }
                } else {
                    val p = Path().apply { moveTo(x, y) }
                    pointers[pid] = p
                    dispatchGesture(p, 0L, 20L)
                }
            }
            "TOUCH_UP" -> {
                val startP = pointers[pid]?.let { p ->
                    val pts = approximate(p)
                    if (pts.isNotEmpty()) pts.last() else x to y
                } ?: (x to y)
                pointers.remove(pid)
                val path = Path().apply { moveTo(startP.first, startP.second); lineTo(x, y) }
                dispatchGesture(path, 0L, 20L)
            }
            "MOUSE_MOVE" -> {
                val path = Path().apply { moveTo(x, y); lineTo(x, y) }
                dispatchGesture(path, 0L, 10L)
            }
            "MOUSE_DOWN" -> {
                val path = Path().apply { moveTo(x, y) }
                dispatchGesture(path, 0L, 30L)
            }
            "MOUSE_UP" -> {
                val path = Path().apply { moveTo(x, y); lineTo(x + 0.1f, y + 0.1f) }
                dispatchGesture(path, 0L, 20L)
            }
            "MOUSE_WHEEL" -> {
                val delta = (ev.delta ?: 0).coerceIn(-500, 500).toFloat()
                val steps = 8
                val dy = (-delta / 8f).coerceIn(-50f, 50f)
                val path1 = Path().apply { moveTo(x, y); for (i in 1..steps) lineTo(x, y + dy * i) }
                val path2 = Path().apply { moveTo(x + 60, y); for (i in 1..steps) lineTo(x + 60, y + dy * i) }
                dispatchTwoFingerGesture(path1, path2, 0L, (steps * 10).toLong())
            }
            "KEY_DOWN", "KEY_UP" -> {
                val vk = ev.vk ?: return
                sendKey(vk, down = (ev.type == "KEY_DOWN"))
            }
            "KEY_TEXT" -> {
                val text = ev.text ?: return
                commitText(text)
            }
        }
    }

    /** 对 Path 进行采样近似，返回若干 (x,y) 点对 */
    private fun approximate(p: Path): List<Pair<Float, Float>> {
        val approx = p.approximate(0.5f)
        val out = mutableListOf<Pair<Float, Float>>()
        var i = 0
        while (i + 1 < approx.size) {
            out.add(approx[i] to approx[i + 1])
            i += 3
        }
        return out.ifEmpty { listOf(0f to 0f) }
    }

    private fun Path.lastX(): Float = approximate(this).lastOrNull()?.first ?: 0f
    private fun Path.lastY(): Float = approximate(this).lastOrNull()?.second ?: 0f

    private fun dispatchGesture(path: Path, startTime: Long, duration: Long) {
        val stroke = GestureDescription.StrokeDescription(path, startTime, duration.coerceAtLeast(1L))
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        handler.post {
            runCatching {
                service.dispatchGesture(gesture, null, null)
            }
        }
    }

    private fun dispatchTwoFingerGesture(p1: Path, p2: Path, startTime: Long, duration: Long) {
        val b = GestureDescription.Builder()
        b.addStroke(GestureDescription.StrokeDescription(p1, startTime, duration))
        b.addStroke(GestureDescription.StrokeDescription(p2, startTime, duration))
        handler.post { runCatching { service.dispatchGesture(b.build(), null, null) } }
    }

    private fun sendKey(winVk: Int, down: Boolean) {
        val globalAction = when (winVk) {
            KeyEvent.KEYCODE_BACK -> AccessibilityService.GLOBAL_ACTION_BACK
            KeyEvent.KEYCODE_HOME -> AccessibilityService.GLOBAL_ACTION_HOME
            KeyEvent.KEYCODE_POWER -> AccessibilityService.GLOBAL_ACTION_POWER_DIALOG
            KeyEvent.KEYCODE_NOTIFICATION -> AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS
            KeyEvent.KEYCODE_RECENT_APPS -> AccessibilityService.GLOBAL_ACTION_RECENTS
            // Quick Settings = swipe down from top, approximate with GLOBAL_ACTION_NOTIFICATIONS
            87 -> AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS
            else -> -1
        }
        if (down && globalAction > 0) {
            service.performGlobalAction(globalAction)
        }
    }

    private fun commitText(text: String) {
        if (TextUtils.isEmpty(text)) return
        val focused = service.rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        if (focused != null) {
            val args = Bundle().apply {
                putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
            }
            focused.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
        }
    }
}

/**
 * 公开的无障碍服务入口：在 Android 系统中开启后，
 * 外部可通过 AccessibilityInjectionService.instance?.dispatcher 获取分发器进行注入。
 */
class AccessibilityInjectionService : AccessibilityService() {

    lateinit var dispatcher: InjectionDispatcher
        private set

    override fun onServiceConnected() {
        super.onServiceConnected()
        dispatcher = InjectionDispatcher(this)
        instance = this
    }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}
    override fun onDestroy() { instance = null; super.onDestroy() }

    companion object {
        @Volatile var instance: AccessibilityInjectionService? = null
        val isEnabled get() = instance != null
    }
}
