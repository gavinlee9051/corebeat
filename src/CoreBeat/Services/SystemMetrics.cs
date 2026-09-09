using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>内存快照。</summary>
public sealed record MemoryInfo(float UsedGB, float TotalGB, float Pct);

/// <summary>系统级基础指标：内存 / 运行时长 / 网络实时速率。</summary>
public sealed class SystemMetrics
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>物理内存占用。</summary>
    public static MemoryInfo? ReadMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0) return null;
        float used = (ms.ullTotalPhys - ms.ullAvailPhys) / 1024f / 1024f / 1024f;
        float total = ms.ullTotalPhys / 1024f / 1024f / 1024f;
        return new MemoryInfo(used, total, used / total * 100f);
    }

    /// <summary>系统运行时长（自上次开机）。</summary>
    public static TimeSpan SystemUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>
/// 网络实时速率采样：每轮轮询全部活动网卡，取累计增量最大的网卡做 1s 差分。
/// </summary>
public sealed class NetworkRateSampler
{
    private sealed class Counter { public long Recv; public long Sent; public DateTime At; }

    private readonly Dictionary<string, Counter> _counters = new();

    /// <returns>(下行 Mbps, 上行 Mbps)；无活动网卡返回 null。</returns>
    public (float DownMbps, float UpMbps)? Sample()
    {
        try
        {
            var now = DateTime.UtcNow;
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up
                            && i.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && i.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && i.Speed > 0)
                .ToList();
            if (interfaces.Count == 0) return null;

            float bestDown = 0, bestUp = 0;

            foreach (var ni in interfaces)
            {
                IPv4InterfaceStatistics stats;
                try { stats = ni.GetIPv4Statistics(); }
                catch { continue; }

                long recv = stats.BytesReceived, sent = stats.BytesSent;
                string key = ni.Id + "|" + ni.Name;

                if (!_counters.TryGetValue(key, out var c))
                {
                    _counters[key] = new Counter { Recv = recv, Sent = sent, At = now };
                    continue;   // 首轮只建档
                }

                double dt = (now - c.At).TotalSeconds;
                if (dt < 0.5) continue;

                double downBps = Math.Max(0, recv - c.Recv) / dt;
                double upBps = Math.Max(0, sent - c.Sent) / dt;

                c.Recv = recv; c.Sent = sent; c.At = now;

                if (downBps > 0 || upBps > 0)
                {
                    float d = (float)(downBps / 125_000.0);
                    float u = (float)(upBps / 125_000.0);
                    // 只保留“当前活跃”的那张卡（增量最大）
                    if (d + u > bestDown + bestUp) { bestDown = d; bestUp = u; }
                }
            }

            if (_counters.Count > interfaces.Count * 4) _counters.Clear();
            return (bestDown, bestUp);
        }
        catch
        {
            return null;
        }
    }
}
