using System.Text.Json;

namespace CoreBeat.Services;

/// <summary>
/// 1s 采样推送器：聚合传感器/内存/网络/进程/防睡状态 → JSON（t:"tick"）交给回调。
/// 回调在采样线程触发，调用方负责封送到 UI 线程。
/// 前端协议：{ t:"tick", cpu, mem{...}, uptimeSec, temps[{label,v,pct}], net{...}|null,
///             procs[{pid,name,cpu,memMB,icon}], awake{level,label,remainSec} }
/// </summary>
public sealed class MetricsPusher : IDisposable
{
    private readonly SensorService _sensor = new();
    private readonly PdhCpuSampler _cpuPdh = new();      // 任务管理器同源（PDH % Processor Utility）
    private readonly PdhDiskSampler _diskPdh = new();    // 磁盘读写（PhysicalDisk Bytes/sec）
    private readonly CpuUsageSampler _cpuKernel = new(); // 内核差分（对照）
    private readonly ProcessMonitor _procs = new();
    private readonly NetworkRateSampler _net = new();
    private readonly HistoryStore _history = new(App.LogDirectory);   // 24h 趋势本地留存
    private readonly Action<string> _emitJson;
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _fastTimer;
    private double? _fastCpu;          // 最近一次 4Hz 快速采样的 CPU（供 1s tick 复用）
    private long _ticks;

    public MetricsPusher(Action<string> emitJson)
    {
        _emitJson = emitJson;
        // 首推延迟 1.5s（等 WebView2 就绪 + 传感器首刷），之后每秒一次（全量）
        _timer = new System.Threading.Timer(_ => Tick(), null, 1500, 1000);
        // 4Hz 快速通道：只推 CPU，让数字像任务管理器一样跟手（显示瞬时尖峰）
        _fastTimer = new System.Threading.Timer(_ => FastTick(), null, 600, 250);
    }

    private void FastTick()
    {
        try
        {
            double? c = _cpuPdh.SamplePercent();
            if (c is null) return;
            _fastCpu = c;
            _emitJson(JsonSerializer.Serialize(new { t = "fast", cpu = Math.Round(c.Value, 1) }));
        }
        catch { /* 忽略 */ }
    }

    private void Tick()
    {
        _ticks++;
        if (_ticks == 1) App.Log("CPU PDH 计数器: " + _cpuPdh.CounterPath);
        SensorSnapshot s = SensorSnapshot.Empty;
        try { _sensor.Refresh(); s = _sensor.Read(); } catch { /* 整轮降级 */ }

        // CPU 总占用：快速通道(4Hz)最新值为主，其次内核差分，再 LHM 兜底
        double? cpuUsage = _fastCpu;
        double? cpuKernel = null;
        try { cpuKernel = _cpuKernel.SamplePercent(); } catch { }
        cpuUsage ??= cpuKernel ?? s.CpuPct;

        MemoryInfo? mem = null;
        try { mem = SystemMetrics.ReadMemory(); } catch { }
        int uptimeSec = 0;
        try { uptimeSec = (int)SystemMetrics.SystemUptime().TotalSeconds; } catch { }

        (float DownMbps, float UpMbps)? netRate = _net.Sample();
        (float ReadMBs, float WriteMBs)? diskRate = _diskPdh.Sample();

        IReadOnlyList<ProcInfo> procs;
        try { procs = _procs.Sample(10); } catch { procs = Array.Empty<ProcInfo>(); }

        AwakeState awake;
        try { awake = App.Awake.State; } catch { awake = new AwakeState(AwakeLevel.Off, "已关闭", null); }

        // 24h 历史：记录 CPU(Utility)/内存%/主温度
        try
        {
            var rows = s.TempRows().ToList();
            double? t = rows.Count > 0 ? rows[0].Value : null;
            _history.Push(cpuUsage ?? 0, mem?.Pct ?? 0, t);
        }
        catch { /* 历史失败不影响主流程 */ }

        var payload = new
        {
            t = "tick",
            mode = "real",
            cpu = cpuUsage.HasValue ? (float?)Math.Round(cpuUsage.Value, 1) : null,
            freqGhz = s.CpuFreqMhz.HasValue ? (float?)Math.Round(s.CpuFreqMhz.Value, 2) : null,
            cores = s.CoreLoads,           // 逐物理核占用 %（数组，0~100 或 null）
            mem = mem is null
                ? null
                : new { usedGB = Math.Round(mem.UsedGB, 1), totalGB = Math.Round(mem.TotalGB, 1), pct = Math.Round(mem.Pct, 1) },
            uptimeSec,
            temps = s.TempRows()
                .Select(r => new { label = r.Label, v = Math.Round(r.Value, 1), pct = (float)Math.Clamp(r.Value, 0f, 100f) })
                .ToArray(),
            gpu = s.GpuPct is { } gp
                ? new { name = s.GpuName ?? "GPU", pct = Math.Round(gp, 1) }
                : null,
            disk = diskRate is { } dr
                ? new { readMB = Math.Round(dr.ReadMBs, 2), writeMB = Math.Round(dr.WriteMBs, 2) }
                : null,
            net = netRate is { } n
                ? new { downMbps = Math.Round(n.DownMbps, 2), upMbps = Math.Round(n.UpMbps, 2) }
                : null,
            procs = procs
                .Select(p => new
                {
                    pid = p.Pid,
                    name = p.Name,
                    cpu = Math.Round(p.CpuPct, 1),
                    memMB = Math.Round(p.MemMb, 1),
                    icon = p.IconB64,
                    isSelf = p.IsSelf,
                })
                .ToArray(),
            awake = new { level = (int)awake.Level, label = awake.Label, remainSec = awake.RemainSec },
        };

        try { _emitJson(JsonSerializer.Serialize(payload)); } catch { /* 忽略 */ }

        // 低频诊断日志（每 10s）：便于 M2 验证传感器真实读数/降级情况
        if (_ticks % 10 == 0)
        {
            try
            {
                string temps = string.Join(", ", s.TempRows().Select(r => $"{r.Label} {r.Value:0.#}°C"));
                string cores = s.CoreLoads is { Length: > 0 } cl
                    ? string.Join(",", cl.Select(c => c.HasValue ? c.Value.ToString("0") : "-"))
                    : "无";
                App.Log($"采样#{_ticks}: CPU={cpuUsage:0.#}%(PDH) kernel={cpuKernel:0.#}% GPU={s.GpuPct:0.#}% " +
                        $"内存={mem?.UsedGB:0.#}G " +
                        $"温度[{ (temps.Length == 0 ? "不可读" : temps) }] " +
                        $"磁盘={(diskRate is { } ds ? $"读{ds.ReadMBs:0.0}/写{ds.WriteMBs:0.0}MB/s" : "无")} " +
                        $"网络={(netRate is { } nr ? $"{nr.DownMbps:0.00}/{nr.UpMbps:0.00}Mbps" : "无")} " +
                        $"进程榜=({procs.Count}条, CPU合计≈{procs.Sum(p => p.CpuPct):0.0}%) 逐核=[{cores}] 推送数={_pushes}");
            }
            catch { /* 日志失败忽略 */ }
        }

        // 硬件传感器全名枚举（每 30s）：排查 CPU/NVMe 温度是否存在但命名不匹配
        if (_ticks % 30 == 0)
        {
            try
            {
                App.Log("传感器枚举:\n" + _sensor.Describe());
            }
            catch { /* 忽略 */ }
        }
    }

    private long _pushes;
    internal void NotifyPushed() => Interlocked.Increment(ref _pushes);

    /// <summary>24h 历史 JSON（t:"history"），供主界面回看。</summary>
    public string HistoryJson() => _history.HistoryJson();

    public void Dispose()
    {
        _timer.Dispose();
        _fastTimer.Dispose();
        _sensor.Dispose();
        _cpuPdh.Dispose();
        _diskPdh.Dispose();
        _history.Dispose();
    }
}
