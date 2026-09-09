using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CoreBeat.Native;

/// <summary>
/// 无边框窗口拖拽：向窗口发送 WM_NCLBUTTONDOWN/HTCAPTION，
/// 由系统接管“按住标题栏拖动”逻辑（透明窗口/任意客户区都可拖，比 DragMove 更稳）。
/// </summary>
public static class WindowDrag
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>仅当左键仍按住时触发系统级窗口拖动。</summary>
    public static bool Try(Window window)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return false;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        // WebView2 等子窗口按下时持有鼠标捕获，需先释放，系统拖拽循环才能接管
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        return true;
    }
}
