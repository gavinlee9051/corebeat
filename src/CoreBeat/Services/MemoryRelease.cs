using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>
/// 真实内存回收：遍历系统进程，把工作集收缩到最小值（EmptyWorkingSet 式，即
/// SetProcessWorkingSetSize(-1,-1)），将可交还的物理页交还给系统。
/// 诚实边界：这是"收缩工作集"而非伪造数据——真正可用的常规手段，无法清理内核页池等。
/// </summary>
public static class MemoryRelease
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>收缩全部可访问进程的工作集（真实释放物理内存）；无权限/系统进程静默跳过。</summary>
    public static void ReleaseAll()
    {
        try
        {
            int selfId = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    using (p)
                    {
                        if (p.Id == selfId)
                        {
                            SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
                            continue;
                        }
                        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                        if (h != IntPtr.Zero)
                        {
                            try { SetProcessWorkingSetSize(h, (IntPtr)(-1), (IntPtr)(-1)); }
                            finally { CloseHandle(h); }
                        }
                    }
                }
                catch { /* 系统/受保护进程等：跳过 */ }
            }
        }
        catch { /* 尽力而为 */ }
    }
}
