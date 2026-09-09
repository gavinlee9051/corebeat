using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>
/// CPU 总占用（与任务管理器同口径）：NtQuerySystemInformation 差分系统整体
/// Idle/Kernel/User 时间，无需管理员权限。
/// </summary>
public sealed class CpuUsageSampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SPPI
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    private const int SystemProcessorPerformanceInformation = 8;

    private long _prevIdle;
    private long _prevTotal;
    private bool _havePrev;

    /// <returns>整体 CPU 占用百分比 0~100（占总容量，任务管理器口径）；首次采样无差分返回 null。</returns>
    public double? SamplePercent()
    {
        try
        {
            int size = Marshal.SizeOf<SPPI>();
            int allocCount = Math.Max(Environment.ProcessorCount, 64);
            IntPtr buf = Marshal.AllocHGlobal(size * allocCount);
            try
            {
                int retLen;
                int status = NtQuerySystemInformation(
                    SystemProcessorPerformanceInformation, buf, size * allocCount, out retLen);
                if (status != 0 || retLen <= 0) return null;

                int count = Math.Max(1, retLen / size);   // 以系统实际返回为准（防 CPU 亲和/掩码影响）

                long idle = 0, kernelUser = 0;
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<SPPI>(IntPtr.Add(buf, i * size));
                    idle += info.IdleTime;
                    // KernelTime 已包含 IdleTime（微软口径），故总忙碌时间 = Kernel + User，
                    // 占用率 = (总 - Idle) / 总，与任务管理器一致。
                    kernelUser += info.KernelTime + info.UserTime;
                }

                if (!_havePrev)
                {
                    _prevIdle = idle;
                    _prevTotal = kernelUser;
                    _havePrev = true;
                    return null;
                }

                long dIdle = idle - _prevIdle;
                long dTotal = kernelUser - _prevTotal;
                _prevIdle = idle;
                _prevTotal = kernelUser;

                if (dTotal <= 0 || dIdle < 0 || dIdle > dTotal) return null;
                double busy = 1.0 - (double)dIdle / dTotal;
                return Math.Clamp(busy * 100.0, 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>复位（避免换挡/长时间暂停后误差分）。</summary>
    public void Reset()
    {
        _havePrev = false;
    }
}
