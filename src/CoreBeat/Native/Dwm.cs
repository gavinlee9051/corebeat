using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CoreBeat.Native;

/// <summary>
/// Win11/Win10 DWM 观感辅助：窗口圆角、沉浸式深色标题栏、系统亚克力/Mica 背景。
/// 所有调用都做防御：接口缺失（如 Win10 部分特性）时静默降级，不影响功能。
/// </summary>
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE = 19; // Win10 1809+
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;        // Win11 22H2+
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;       // Win11 22H2+
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;            // Win11 22H2+

    private const int DWMWCP_ROUND = 2;                          // 圆角
    private const int DWMSBT_MAINWINDOW = 2;                     // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;                // Acrylic

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>对窗口应用 DWM 外观；应在 SourceInitialized 之后调用。</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        SetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE, 1); // 兼容旧版
        SetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // 亚克力/Mica 背景：Win11 22H2+ 生效，失败自动忽略（窗口仍是深色背景）
        SetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            int v = value;
            int hr = DwmSetWindowAttribute(hwnd, attribute, ref v, sizeof(int));
            if (hr != 0)
            {
                // HRESULT 非 0 表示特性不可用（如 Win10），静默忽略
            }
        }
        catch
        {
            // 同上：任何 DWM 失败都不影响主流程
        }
    }
}
