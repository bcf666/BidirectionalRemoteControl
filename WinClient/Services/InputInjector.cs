using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinClient.Models;

namespace WinClient.Services;

/// <summary>
/// Win32 SendInput 封装：键鼠输入注入（模拟本地 PC 输入）
/// 坐标使用归一化 0~1，需要 SetTargetScreenSize 后内部换算为物理像素
/// </summary>
public class InputInjector
{
    #region Win32

    private const int INPUT_MOUSE    = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL     = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;
    private const uint MOUSEEVENTF_XDOWN      = 0x0080;
    private const uint MOUSEEVENTF_XUP        = 0x0100;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    mi;
        [FieldOffset(0)] public KEYBDINPUT    ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int  dx;
        public int  dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    #endregion

    // 目标"屏幕"尺寸（可能是远程的发送端逻辑尺寸），
    // 为保持兼容，输入注入前内部直接用 SystemParameters.PrimaryScreenWidth/Height 做绝对坐标
    // —— 所以这里取本地主显示器物理像素。
    private (double w, double h) GetScreenSize()
    {
        var w = SystemParameters.PrimaryScreenWidth;
        var h = SystemParameters.PrimaryScreenHeight;
        // 当 DPI 感知开启时，WPF 的 PrimaryScreenWidth 是 DPI 缩放后的逻辑像素，
        // 我们转成物理像素（SendInput 需要物理像素）
        var dpiX = 1.0; var dpiY = 1.0;
        using var src = new System.Windows.Interop.HwndSource(new HwndSourceParameters());
        if (src.CompositionTarget != null)
        {
            dpiX = src.CompositionTarget.TransformToDevice.M11;
            dpiY = src.CompositionTarget.TransformToDevice.M22;
        }
        return (w * dpiX, h * dpiY);
    }

    private (int px, int py) NormalizedToPixel(double nx, double ny)
    {
        var (w, h) = GetScreenSize();
        // clamp
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        // MOUSEEVENTF_ABSOLUTE 需要 0~65535 的映射
        var absX = (int)Math.Round(nx * 65535);
        var absY = (int)Math.Round(ny * 65535);
        return (absX, absY);
    }

    public void Dispatch(InputEvent ev)
    {
        switch (ev.Type)
        {
            case nameof(InputEventType.MOUSE_MOVE):
                if (ev.X.HasValue && ev.Y.HasValue) MouseMove(ev.X.Value, ev.Y.Value);
                break;
            case nameof(InputEventType.MOUSE_DOWN):
                if (ev.X.HasValue && ev.Y.HasValue) MouseMove(ev.X.Value, ev.Y.Value);
                MouseDown(ParseButton(ev.Button));
                break;
            case nameof(InputEventType.MOUSE_UP):
                if (ev.X.HasValue && ev.Y.HasValue) MouseMove(ev.X.Value, ev.Y.Value);
                MouseUp(ParseButton(ev.Button));
                break;
            case nameof(InputEventType.MOUSE_WHEEL):
                if (ev.X.HasValue && ev.Y.HasValue) MouseMove(ev.X.Value, ev.Y.Value);
                MouseWheel(ev.Delta ?? 0, ev.Axis == "H");
                break;
            case nameof(InputEventType.KEY_DOWN):
                if (ev.Vk.HasValue) KeyDown((ushort)ev.Vk.Value);
                else if (!string.IsNullOrWhiteSpace(ev.Key)) KeyDownByName(ev.Key!, true);
                break;
            case nameof(InputEventType.KEY_UP):
                if (ev.Vk.HasValue) KeyUp((ushort)ev.Vk.Value);
                else if (!string.IsNullOrWhiteSpace(ev.Key)) KeyDownByName(ev.Key!, false);
                break;
            case nameof(InputEventType.KEY_TEXT):
                if (!string.IsNullOrEmpty(ev.Text)) SendUnicodeText(ev.Text);
                break;
            // TOUCH_*：PC 端作为被控端时可以忽略 TOUCH，把它当作鼠标即可
            case nameof(InputEventType.TOUCH_DOWN):
                if (ev.X.HasValue && ev.Y.HasValue) { MouseMove(ev.X.Value, ev.Y.Value); MouseDown(MouseButton.LEFT); }
                break;
            case nameof(InputEventType.TOUCH_MOVE):
                if (ev.X.HasValue && ev.Y.HasValue) MouseMove(ev.X.Value, ev.Y.Value);
                break;
            case nameof(InputEventType.TOUCH_UP):
                if (ev.X.HasValue && ev.Y.HasValue) { MouseMove(ev.X.Value, ev.Y.Value); MouseUp(MouseButton.LEFT); }
                break;
        }
    }

    #region 键鼠实现

    private static MouseButton ParseButton(string? s)
        => Enum.TryParse<MouseButton>(s, true, out var b) ? b : MouseButton.LEFT;

    private void MouseMove(double nx, double ny)
    {
        var (x, y) = NormalizedToPixel(nx, ny);
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = x, dy = y,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private void MouseButtonFlags(MouseButton btn, bool down, out uint flags, out uint data)
    {
        flags = 0; data = 0;
        switch (btn)
        {
            case MouseButton.LEFT:   flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case MouseButton.RIGHT:  flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case MouseButton.MIDDLE: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MouseButton.X1:
            case MouseButton.X2:
                flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                data  = btn == MouseButton.X1 ? 1u : 2u;
                break;
        }
    }
    private void MouseDown(MouseButton b)
    {
        MouseButtonFlags(b, true, out var f, out var d);
        var inp = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = f, mouseData = d } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }
    private void MouseUp(MouseButton b)
    {
        MouseButtonFlags(b, false, out var f, out var d);
        var inp = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = f, mouseData = d } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }
    private void MouseWheel(int delta, bool horizontal)
    {
        var flags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = (uint)(delta) } }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private void KeyDown(ushort vk) => KeyPress(vk, true);
    private void KeyUp(ushort vk)   => KeyPress(vk, false);
    private void KeyPress(ushort vk, bool down)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP }
            }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        {"Enter", 0x0D}, {"Return", 0x0D}, {"Escape", 0x1B}, {"Esc", 0x1B},
        {"Tab", 0x09}, {"Space", 0x20}, {"Backspace", 0x08},
        {"Ctrl", 0x11}, {"Control", 0x11}, {"Shift", 0x10}, {"Alt", 0x12}, {"Win", 0x5B},
        {"Left", 0x25}, {"ArrowLeft", 0x25}, {"Up", 0x26}, {"ArrowUp", 0x26},
        {"Right", 0x27}, {"ArrowRight", 0x27}, {"Down", 0x28}, {"ArrowDown", 0x28},
        {"Home", 0x24}, {"End", 0x23}, {"PageUp", 0x21}, {"PageDown", 0x22},
        {"Delete", 0x2E}, {"Del", 0x2E}, {"Insert", 0x2D}, {"Ins", 0x2D},
        {"F1",0x70},{"F2",0x71},{"F3",0x72},{"F4",0x73},{"F5",0x74},{"F6",0x75},
        {"F7",0x76},{"F8",0x77},{"F9",0x78},{"F10",0x79},{"F11",0x7A},{"F12",0x7B}
    };

    private void KeyDownByName(string key, bool down)
    {
        if (NamedKeys.TryGetValue(key, out var vk))
        {
            KeyPress(vk, down);
            return;
        }
        if (key.Length == 1)
        {
            // 走 Unicode 模拟（支持中文）
            SendUnicodeText(key);
        }
    }

    /// <summary>用 KEYEVENTF_UNICODE 发送任意文本（含中文），不依赖键盘布局。</summary>
    private void SendUnicodeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (inputs.Count == 0) return;
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    #endregion
}
