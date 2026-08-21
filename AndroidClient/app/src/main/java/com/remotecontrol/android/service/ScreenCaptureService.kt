package com.remotecontrol.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.IBinder
import android.util.DisplayMetrics
import android.view.WindowManager
import androidx.core.app.NotificationCompat
import com.remotecontrol.android.protocol.MessageType
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.ByteArrayOutputStream

/**
 * MediaProjection 前台服务：抓取屏幕 -> JPEG -> 通过 NetworkTransport 发 VIDEO 包
 * 由外部在拿到 MediaProjection permission resultCode/data 后启动
 */
class ScreenCaptureService : Service() {

    companion object {
        const val EXTRA_RESULT_CODE = "rc.resultCode"
        const val EXTRA_RESULT_DATA = "rc.resultData"
        const val EXTRA_WIDTH = "rc.w"
        const val EXTRA_HEIGHT = "rc.h"
        const val EXTRA_FPS = "rc.fps"
        const val EXTRA_QUALITY = "rc.quality"
        const val CHANNEL_ID = "RemoteControlCapture"

        // 外部设置一个传输器回调：sendJpeg(byteArray)
        @Volatile var sendJpeg: (suspend (ByteArray) -> Unit)? = null
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var mediaProjection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
        startForeground(1001, buildNotification("屏幕共享准备中…"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val resultCode = intent?.getIntExtra(EXTRA_RESULT_CODE, 0) ?: 0
        val data: Intent? = intent?.getParcelableExtra(EXTRA_RESULT_DATA)
        val w = intent?.getIntExtra(EXTRA_WIDTH, 1280) ?: 1280
        val h = intent?.getIntExtra(EXTRA_HEIGHT, 720) ?: 720
        val fps = intent?.getIntExtra(EXTRA_FPS, 20) ?: 20
        val q = intent?.getIntExtra(EXTRA_QUALITY, 80) ?: 80

        val mpMgr = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val mp = mpMgr.getMediaProjection(resultCode, data!!)
        startCapture(mp, w, h, fps, q)
        return START_NOT_STICKY
    }

    private fun startCapture(mp: MediaProjection, width: Int, height: Int, fps: Int, quality: Int) {
        mediaProjection = mp
        reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2)

        virtualDisplay = mp.createVirtualDisplay(
            "RemoteControl-VD",
            width, height, getDisplayDensityDpi(),
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            reader!!.surface, null, null
        )

        job = scope.launch {
            val frameMs = (1000 / fps.coerceAtLeast(1)).toLong()
            var lastImage: Image? = null
            while (true) {
                val img = reader?.acquireLatestImage()
                if (img != null) {
                    lastImage?.close(); lastImage = img
                    val jpeg = imageToJpeg(img, quality)
                    if (jpeg != null) runCatching { sendJpeg?.invoke(jpeg) }
                }
                delay(frameMs)
            }
        }
    }

    private fun getDisplayDensityDpi(): Int {
        val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val dm = DisplayMetrics()
        @Suppress("DEPRECATION")
        wm.defaultDisplay.getRealMetrics(dm)
        return dm.densityDpi
    }

    /** RGBA_8888 Image -> Bitmap -> JPEG bytes */
    private fun imageToJpeg(img: Image, quality: Int): ByteArray? {
        val planes = img.planes
        val buffer = planes[0].buffer
        val pixelStride = planes[0].pixelStride
        val rowStride = planes[0].rowStride
        val rowPadding = rowStride - pixelStride * img.width
        val w = img.width; val h = img.height
        val bitmap = Bitmap.createBitmap(w + rowPadding / pixelStride, h, Bitmap.Config.ARGB_8888)
        bitmap.copyPixelsFromBuffer(buffer)
        val cropped = Bitmap.createBitmap(bitmap, 0, 0, w, h)
        bitmap.recycle()
        val os = ByteArrayOutputStream(64 * 1024)
        val ok = cropped.compress(Bitmap.CompressFormat.JPEG, quality, os)
        cropped.recycle()
        return if (ok) os.toByteArray() else null
    }

    override fun onDestroy() {
        runCatching { job?.cancel() }
        runCatching { virtualDisplay?.release() }
        runCatching { reader?.close() }
        runCatching { mediaProjection?.stop() }
        scope.cancel()
        super.onDestroy()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val ch = NotificationChannel(CHANNEL_ID, "远程屏幕共享", NotificationManager.IMPORTANCE_LOW)
        ch.setShowBadge(false); nm.createNotificationChannel(ch)
    }
    private fun buildNotification(text: String): Notification =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("双向远程控制").setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setOngoing(true).build()
}
