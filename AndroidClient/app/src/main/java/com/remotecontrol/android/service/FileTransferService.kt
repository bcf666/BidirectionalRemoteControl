package com.remotecontrol.android.service

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.MediaStore
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import com.remotecontrol.android.protocol.FileAck
import com.remotecontrol.android.protocol.FileDone
import com.remotecontrol.android.protocol.FileMeta
import com.remotecontrol.android.protocol.MessageType
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.charset.Charset

/** 视频帧解码：JPEG 字节 -> Compose 可渲染的 ImageBitmap。回调在 Dispatchers.Main.immediate 外部可自行切。 */
object VideoCodec {
    fun decodeJpeg(bytes: ByteArray, opts: BitmapFactory.Options? = null): ImageBitmap? {
        val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts) ?: return null
        return bmp.asImageBitmap()
    }
}

/**
 * 文件传输服务：
 * - 发送：SendFile(uri/file) -> FILE_META -> wait FILE_ACK -> chunks -> FILE_DONE
 * - 接收：收到 FILE_META 上层确认后 StartReceiving -> 写入文件
 */
class FileTransferService(
    private val ctx: Context,
    private val net: NetworkTransport,
    private val scope: CoroutineScope
) {
    companion object {
        const val CHUNK = 65536
    }

    // fileId -> receiving state
    private data class Rec(val out: FileOutputStream, val path: String)
    private val receiving = hashMapOf<String, Rec>()

    suspend fun sendFromUri(uri: Uri, progress: (Double)->Unit = {}) {
        val (stream, size, name) = openUri(uri)
        val fileId = java.util.UUID.randomUUID().toString()
        val meta = FileMeta(fileId, name, size, CHUNK)
        net.sendJson(MessageType.FILE_META, meta.toJson())
        // 简化处理：等待 ~500ms 直接开始（正式版本应订阅 FILE_ACK）
        kotlinx.coroutines.delay(500)
        val buf = ByteArray(CHUNK)
        var sent = 0L
        while (true) {
            val n = stream.read(buf)
            if (n <= 0) break
            val chunk = buildChunkPayload(fileId, sent, buf.copyOf(n))
            net.sendBytes(MessageType.FILE_CHUNK, chunk)
            sent += n
            if (size > 0) progress(sent.toDouble() / size.toDouble())
        }
        runCatching { stream.close() }
        net.sendJson(MessageType.FILE_DONE, FileDone(fileId).toJson())
        progress(1.0)
    }

    suspend fun sendFile(file: File, progress: (Double)->Unit = {}) {
        val fileId = java.util.UUID.randomUUID().toString()
        val meta = FileMeta(fileId, file.name, file.length(), CHUNK)
        net.sendJson(MessageType.FILE_META, meta.toJson())
        kotlinx.coroutines.delay(500)
        val stream = file.inputStream().buffered()
        val buf = ByteArray(CHUNK)
        var sent = 0L
        while (true) {
            val n = stream.read(buf)
            if (n <= 0) break
            val chunk = buildChunkPayload(fileId, sent, buf.copyOf(n))
            net.sendBytes(MessageType.FILE_CHUNK, chunk)
            sent += n; progress(if (file.length() > 0) sent.toDouble() / file.length() else 1.0)
        }
        runCatching { stream.close() }
        net.sendJson(MessageType.FILE_DONE, FileDone(fileId).toJson())
    }

    private fun openUri(uri: Uri): Triple<InputStream, Long, String> {
        val cr = ctx.contentResolver
        var name = "unnamed"
        runCatching {
            val cursor = cr.query(uri, null, null, null, null, null)
            cursor?.use { c ->
                if (c.moveToFirst()) {
                    val idx = c.getColumnIndex(MediaStore.MediaColumns.DISPLAY_NAME)
                    if (idx >= 0) name = c.getString(idx)
                }
            }
        }
        val size = runCatching {
            cr.openFileDescriptor(uri, "r")?.use { it.statSize } ?: 0L
        }.getOrDefault(0L)
        return Triple(cr.openInputStream(uri)!!, size, name)
    }

    private fun buildChunkPayload(fileId: String, offset: Long, data: ByteArray): ByteArray {
        val header = ByteArray(40)
        val idBytes = fileId.padEnd(36).take(36).toByteArray(Charset.forName("UTF-8"))
        idBytes.copyInto(header, 0)
        val off = offset.coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        header[36] = ((off shr 24) and 0xFF).toByte()
        header[37] = ((off shr 16) and 0xFF).toByte()
        header[38] = ((off shr  8) and 0xFF).toByte()
        header[39] = ( off         and 0xFF).toByte()
        val out = ByteArray(header.size + data.size)
        header.copyInto(out, 0); data.copyInto(out, header.size)
        return out
    }

    // --- 接收 ---
    suspend fun startReceiving(meta: FileMeta, saveDir: File): String {
        saveDir.mkdirs()
        var dst = File(saveDir, meta.name)
        var i = 1
        while (dst.exists()) {
            dst = File(saveDir, "${meta.name.substringBeforeLast('.')}_$i.${meta.name.substringAfterLast('.', "")}")
            i++
        }
        val fos = FileOutputStream(dst)
        receiving[meta.fileId] = Rec(fos, dst.absolutePath)
        net.sendJson(MessageType.FILE_ACK, FileAck(meta.fileId, true, dst.absolutePath).toJson())
        return dst.absolutePath
    }

    fun writeChunk(raw: ByteArray) {
        if (raw.size < 40) return
        val fileId = String(raw, 0, 36, Charset.forName("UTF-8")).trim()
        val rec = receiving[fileId] ?: return
        val offset = ((raw[36].toInt() and 0xFF) shl 24) or
                     ((raw[37].toInt() and 0xFF) shl 16) or
                     ((raw[38].toInt() and 0xFF) shl 8) or
                     (raw[39].toInt() and 0xFF)
        scope.launch(Dispatchers.IO) {
            synchronized(rec) {
                runCatching {
                    rec.out.channel.position(offset.toLong())
                    rec.out.write(raw, 40, raw.size - 40)
                }
            }
        }
    }

    fun finishFile(fileId: String): String? {
        val r = receiving.remove(fileId) ?: return null
        runCatching { r.out.flush(); r.out.fd.sync(); r.out.close() }
        return r.path
    }
}
