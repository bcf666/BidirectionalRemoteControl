package com.remotecontrol.android

import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModelProvider
import com.remotecontrol.android.ui.HomeScreen

class MainActivity : ComponentActivity() {

    private val vm: RemoteSession by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 启动后立即开启设备发现
        vm.startDiscovery()

        setContent {
            AppTheme {
                Surface {
                    HomeScreen(vm = vm, onNeedMediaProjection = { intent, code, result ->
                        // Compose 里已经用了 Launcher 处理，这里只留兜底
                    })
                }
            }
        }
    }

    override fun onDestroy() {
        vm.stopDiscovery(); super.onDestroy()
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
