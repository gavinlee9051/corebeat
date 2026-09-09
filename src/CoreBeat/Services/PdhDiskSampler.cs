using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>
/// 磁盘实时读写速率（PDH PhysicalDisk 计数）：与任务管理器性能页同源。
/// </summary>
public sealed class PdhDiskSampler : IDisposable
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
        public uint CStatus;
        public double DoubleValue;
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_CSTATUS_VALID_DATA = 0;

    private IntPtr _query = IntPtr.Zero;
    private IntPtr _readCounter = IntPtr.Zero;
    private IntPtr _writeCounter = IntPtr.Zero;
    private readonly object _gate = new();

    public PdhDiskSampler()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            const string rd = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
            const string wr = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
            if (PdhAddEnglishCounter(_query, rd, IntPtr.Zero, out _readCounter) != 0 ||
                PdhAddEnglishCounter(_query, wr, IntPtr.Zero, out _writeCounter) != 0)
            {
                _readCounter = IntPtr.Zero;
                _writeCounter = IntPtr.Zero;
                return;
            }
            PdhCollectQueryData(_query);   // 基线
        }
        catch { /* 初始化失败 → 返回 null */ }
    }

    /// <returns>(读 MB/s, 写 MB/s)；无磁盘或首次调用返回 null。</returns>
    public (float ReadMBs, float WriteMBs)? Sample()
    {
        if (_query == IntPtr.Zero || _readCounter == IntPtr.Zero) return null;
        lock (_gate)
        {
            try
            {
                if (PdhCollectQueryData(_query) != 0) return null;
                if (PdhGetFormattedCounterValue(_readCounter, PDH_FMT_DOUBLE, out _, out var rv) != 0 ||
                    rv.CStatus != PDH_CSTATUS_VALID_DATA) return null;
                if (PdhGetFormattedCounterValue(_writeCounter, PDH_FMT_DOUBLE, out _, out var wv) != 0 ||
                    wv.CStatus != PDH_CSTATUS_VALID_DATA) return null;

                return ((float)(rv.DoubleValue / 1024.0 / 1024.0),
                        (float)(wv.DoubleValue / 1024.0 / 1024.0));
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
        _readCounter = IntPtr.Zero;
        _writeCounter = IntPtr.Zero;
    }
}
