package com.remotecontrol.android

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.core.content.ContextCompat
import com.remotecontrol.android.ui.HomeScreen

class MainActivity : ComponentActivity() {

    private val vm: RemoteSession by viewModels()

    private val TAG = "RemoteControl"

    // 请求必要的运行时权限
    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        Log.d(TAG, "Permission results: $permissions")
        // 无论权限是否全部授予，都尝试启动发现（文件传输功能可能受限）
        startDiscoverySafe()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 先尝试请求运行时权限
        val neededPermissions = getNeededPermissions()
        val notGranted = neededPermissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }

        if (notGranted.isNotEmpty()) {
            permissionLauncher.launch(notGranted.toTypedArray())
        } else {
            startDiscoverySafe()
        }

        setContent {
            AppTheme {
                Surface {
                    HomeScreen(vm = vm, onNeedMediaProjection = { _, _, _ -> })
                }
            }
        }
    }

    private fun startDiscoverySafe() {
        try {
            vm.startDiscovery()
            Log.d(TAG, "Device discovery started successfully")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start discovery", e)
        }
    }

    private fun getNeededPermissions(): List<String> {
        val perms = mutableListOf<String>()
        // Android 13+ 需要请求媒体权限
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            perms.add(Manifest.permission.READ_MEDIA_IMAGES)
            perms.add(Manifest.permission.READ_MEDIA_VIDEO)
            perms.add(Manifest.permission.READ_MEDIA_AUDIO)
        } else {
            perms.add(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        return perms
    }

    override fun onDestroy() {
        try { vm.stopDiscovery() } catch (_: Exception) { }
        super.onDestroy()
    }
}

@Composable
fun AppTheme(content: @Composable () -> Unit) {
    val dark = darkColorScheme(
        primary = Color(0xFF8CD3FF),
        onPrimary = Color(0xFF002030),
        primaryContainer = Color(0xFF0E3B52),
        onPrimaryContainer = Color(0xFFC4E7FF),
        secondary = Color(0xFFFFB7A6),
        surface = Color(0xFF121212),
        surfaceVariant = Color(0xFF1F1F1F),
        onSurface = Color(0xFFE5E5E5),
        background = Color(0xFF121212)
    )
    MaterialTheme(colorScheme = dark, content = content)
}
