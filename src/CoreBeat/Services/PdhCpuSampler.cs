using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>
/// CPU 总占用（PDH 计数器，与任务管理器/Get-Counter 同一数据源）。
/// 计数器 "\Processor(_Total)\% Processor Time" —— 无需管理员，天然与 TM 一致。
/// </summary>
public sealed class PdhCpuSampler
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PdhFmtCounterValue pValue);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;      // offset 0
        public double DoubleValue; // offset 8（对齐后）
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_CSTATUS_VALID_DATA = 0;

    private readonly object _gate = new();   // fast(250ms) 与 tick(1s) 会共享同一 PDH 查询
    private IntPtr _query;
    private IntPtr _counter;
    private string _pathName = string.Empty;

    /// <summary>实际命中的计数器完全路径（诊断用）。</summary>
    public string CounterPath => _pathName;

    public PdhCpuSampler()
    {
        try
        {
            // Windows 11 任务管理器使用 % Processor Utility（Win10 才是 % Processor Time）
            // 先尝试 utility，失败则退回 time，两者都能与 TM 对齐。
            bool ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0;
            if (ok)
            {
                const string util = @"\Processor Information(_Total)\% Processor Utility";
                int rc = PdhAddEnglishCounter(_query, util, IntPtr.Zero, out _counter);
                if (rc != 0)
                {
                    rc = PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _counter);
                    _pathName = @"\Processor(_Total)\% Processor Time (fallback)";
                }
                else
                {
                    _pathName = util;
                }
                if (rc != 0) { _counter = IntPtr.Zero; ok = false; }
            }
            if (ok)
            {
                PdhCollectQueryData(_query);   // 首个基线样本
                return;
            }
        }
        catch { /* 初始化失败→返回 null */ }

        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
        _pathName = "无(初始化失败)";
    }

    /// <summary>每次采样调用一次；首次调用返回 null（PDH 需要两次采集求差分）。</summary>
    public double? SamplePercent()
    {
        if (_query == IntPtr.Zero || _counter == IntPtr.Zero) return null;
        lock (_gate)
        {
            try
            {
                int rcCollect = PdhCollectQueryData(_query);
                if (rcCollect != 0) return null;

                int rc = PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, out _, out var val);
                if (rc != 0 || val.CStatus != PDH_CSTATUS_VALID_DATA) return null;

                return Math.Clamp(val.DoubleValue, 0, 100);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        try { if (_query != IntPtr.Zero) PdhCloseQuery(_query); } catch { }
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }
}
