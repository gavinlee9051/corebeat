using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>进程榜条目。</summary>
public sealed record ProcInfo(int Pid, string Name, float CpuPct, float MemMb, string? IconB64, bool IsSelf,
    string? Path = null, string? StartUtc = null, int Threads = 0);

/// <summary>
/// 进程采样：1s 差分 CPU、物理内存；应用图标（SHGetFileInfo，base64 PNG，按路径缓存）。
/// </summary>
public sealed class ProcessMonitor
{
    /// <summary>进程 CPU 按“占总容量 %”显示（与任务管理器一致：1 满核 = 100/核数 %）。</summary>
    private static readonly int Cores = Math.Max(1, Environment.ProcessorCount);

    private readonly object _gate = new();
    private Dictionary<int, (long CpuMs, long WallMs)> _prev = new();
    private readonly Dictionary<string, string?> _iconCache = new();
    private readonly HashSet<int> _announcedPids = new(); // 已发给前端（图标不必重复）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>返回全部进程（按 CPU 降序），供前端可滚动列表展示。</summary>
    public IReadOnlyList<ProcInfo> Sample(int _ = 0)
    {
        try
        {
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return Array.Empty<ProcInfo>(); }

            var nowMs = Environment.TickCount64;
            var cur = new Dictionary<int, (long CpuMs, string Name, long MemBytes, string? Path, string? StartUtc, int Threads)>();
            int selfPid = Environment.ProcessId;

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id <= 0) continue;
                    long cpuMs;
                    try { cpuMs = (long)p.TotalProcessorTime.TotalMilliseconds; }
                    catch { continue; }

                    long mem;
                    try { mem = p.WorkingSet64; } catch { mem = 0; }

                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 系统进程等无权限 */ }

                    string? startUtc = null;
                    try { startUtc = p.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                    int threads = 0;
                    try { threads = p.Threads.Count; } catch { }

                    cur[p.Id] = (cpuMs, SafeName(p.ProcessName, p.Id), mem, path, startUtc, threads);
                }
                catch { /* 单个进程失败不影响整体 */ }
            }

            var list = new List<ProcInfo>();
            foreach (var (pid, (cpuMs, name, mem, path, startUtc, threads)) in cur)
            {
                if (!_prev.TryGetValue(pid, out var prev)) continue;      // 新进程首轮无差分
                double dtMs = Math.Max(1, nowMs - prev.WallMs);
                // 单位对齐：占总容量 %（TM 的进程列口径），除以逻辑核数
                float cpu = (float)Math.Clamp((cpuMs - prev.CpuMs) / dtMs * 100.0 / Cores, 0, 100);
                list.Add(new ProcInfo(pid, name, cpu, mem / 1024f / 1024f, null, pid == selfPid, path, startUtc, threads));
            }

            // 保存本轮快照供下轮差分（只需要 CPU 时间与采样时刻）
            _prev = cur.ToDictionary(kv => kv.Key, kv => (kv.Value.CpuMs, nowMs));

            var ordered = list
                .OrderByDescending(x => x.CpuPct)
                .ThenByDescending(x => x.MemMb)
                .ToList();

            // 图标只补给 CPU 榜前 40：提权后能读到全部路径，若给每个进程都抓 base64 图标，
            // 单条注入消息会过大而静默失败（表现为提权重启后“运行进程”加载不出来）。
            for (int i = 0; i < ordered.Count && i < 40; i++)
            {
                var row = ordered[i];
                if (_announcedPids.Contains(row.Pid)) continue;
                row = row with { IconB64 = GetIconB64(row.Path) };
                ordered[i] = row;
                _announcedPids.Add(row.Pid);
                if (_announcedPids.Count > 300) _announcedPids.Clear();
            }

            return ordered;
        }
        catch
        {
            return Array.Empty<ProcInfo>();
        }
    }

    private static string SafeName(string raw, int pid) =>
        string.IsNullOrWhiteSpace(raw) ? $"PID {pid}" : raw;

    /// <summary>取 exe 的应用图标（32px PNG base64）；缓存避免重复调用 shell32。</summary>
    private string? GetIconB64(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        path = path.ToLowerInvariant();

        lock (_gate)
        {
            if (_iconCache.TryGetValue(path, out var hit)) return hit;
            if (_iconCache.Count > 96) _iconCache.Clear();

            string? b64 = FetchIcon(path);
            _iconCache[path] = b64;
            return b64;
        }
    }

    private static string? FetchIcon(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                using var icon = Icon.FromHandle(info.hIcon);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
