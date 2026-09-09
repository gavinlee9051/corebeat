using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace CoreBeat.Services;

/// <summary>一次传感器快照（读不到的项目为 null，前端显示 “—”）。</summary>
public sealed record SensorSnapshot(
    float? CpuPct,
    float? CpuFreqMhz,
    float? CpuTemp,
    float? GpuPct,
    float? GpuTemp,
    float? NvmeTemp,
    string? GpuName = null,
    float?[]? CoreLoads = null)
{
    public static readonly SensorSnapshot Empty = new(null, null, null, null, null, null);

    /// <summary>温度仪表盘的展示行（按优先级 CPU→NVMe→GPU）。</summary>
    public IEnumerable<(string Label, float Value)> TempRows()
    {
        if (CpuTemp is { } cpu) yield return ("CPU 温度", cpu);
        if (NvmeTemp is { } nv) yield return ("NVMe", nv);
        if (GpuTemp is { } gpu) yield return ("GPU", gpu);
    }
}

/// <summary>
/// LibreHardwareMonitorLib 传感器封装。
/// 设计约定：任何读取失败都静默降级（返回 Empty），绝不让采样链路崩溃。
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsStorageEnabled = true,
        IsMotherboardEnabled = true,   // 部分主板经 SuperIO 暴露 “CPU” 温度，作为 CPU 温度兜底
        IsMemoryEnabled = false,       // 内存改由 GlobalMemoryStatusEx 读取
        IsControllerEnabled = true,
        IsNetworkEnabled = false,
        IsPsuEnabled = false,
        IsBatteryEnabled = false,
    };

    public SensorService()
    {
        try { _computer.Open(); } catch { /* 打开失败→后续全部降级 */ }
    }

    /// <summary>刷新硬件状态（每次采样前调用）。</summary>
    public void Refresh()
    {
        try { _computer.Accept(new UpdateVisitor()); } catch { /* 忽略 */ }
    }

    public SensorSnapshot Read()
    {
        float? cpuPct = null, cpuFreq = null, gpuPct = null, gpuTemp = null, nvmeTemp = null;
        string? gpuName = null;
        var cpuTemps = new List<(string Name, float Value)>();     // CPU 硬件下的温度
        var mbCpuTemps = new List<(string Name, float Value)>();   // 主板暴露的 CPU 温度兜底
        var coreThreads = new Dictionary<int, List<float>>();      // 逐核采集（超线程归入物理核）

        try
        {
            void Walk(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.Value is not { } v) continue;

                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            if (s.SensorType == SensorType.Load && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                                cpuPct = v;
                            else if (s.SensorType == SensorType.Clock && cpuFreq is null)
                                cpuFreq = v / 1000f;                // 任一核时钟(MHz) → GHz
                            else if (s.SensorType == SensorType.Load && TryParseCore(s.Name, out int ci))
                            {
                                if (!coreThreads.TryGetValue(ci, out var list))
                                    coreThreads[ci] = list = new List<float>();
                                list.Add(v);
                            }
                            else if (s.SensorType == SensorType.Temperature)
                                cpuTemps.Add((s.Name, v));
                            break;

                        case HardwareType.Motherboard:
                            // 部分主板（SuperIO）把 CPU 温度命名为 “CPU”/“CPU Package”/“Aux”
                            if (s.SensorType == SensorType.Temperature &&
                                (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)))
                                mbCpuTemps.Add((s.Name, v));
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                            gpuName ??= hw.Name;
                            if (s.SensorType == SensorType.Load && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                                gpuPct = v;
                            else if (s.SensorType == SensorType.Temperature && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                                gpuTemp = v;
                            break;

                        case HardwareType.Storage:
                            // 只要“实时温度”传感器；跳过阈值/上下限（如 Warning/Critical/Max/Min/Limit/Threshold）
                            if (s.SensorType == SensorType.Temperature && nvmeTemp is null &&
                                !s.Name.Contains("Airflow", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Maximum", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Minimum", StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Trip", StringComparison.OrdinalIgnoreCase))
                                nvmeTemp = v;
                            break;
                    }
                }

                foreach (var sub in hw.SubHardware) Walk(sub);
            }

            foreach (var hw in _computer.Hardware) Walk(hw);
        }
        catch
        {
            return SensorSnapshot.Empty;
        }

        // CPU 温度：优先 CPU 硬件里的 “Package/Tctl/Tdie”，否则取 CPU 各核心最高；
        // CPU 硬件无温度时，用主板 SuperIO 暴露的 “CPU” 类温度兜底（部分平台如此）
        float? cpuTemp = null;
        var candidates = cpuTemps.Count > 0 ? cpuTemps : mbCpuTemps;
        if (candidates.Count > 0)
        {
            var package = candidates.FirstOrDefault(t =>
                t.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase));
            cpuTemp = package.Value > 0
                ? package.Value
                : candidates.Max(t => t.Value);
        }

        return new SensorSnapshot(cpuPct, cpuFreq, cpuTemp, gpuPct, gpuTemp, nvmeTemp, gpuName,
            coreThreads.Count > 0
                ? Enumerable.Range(0, coreThreads.Keys.Max())
                    .Select(i => coreThreads.TryGetValue(i + 1, out var l) && l.Count > 0 ? (float?)l.Max() : null)
                    .ToArray()
                : null);
    }

    /// <summary>从 "CPU Core #2 Thread #1" 这类名称提取物理核序号。</summary>
    private static bool TryParseCore(string name, out int coreIndex)
    {
        coreIndex = -1;
        var m = Regex.Match(name, @"Core #(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out coreIndex)) return false;
        return true;
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { /* 忽略 */ }
    }

    /// <summary>枚举硬件与传感器名称/数值（诊断用）：排查 CPU/NVMe 温度是否只是“名字不匹配”。</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            void Walk(IHardware hw, int depth)
            {
                sb.AppendLine($"{new string(' ', depth * 2)}HW[{hw.HardwareType}] {hw.Name}");
                foreach (var s in hw.Sensors.Where(s => s.Value is not null &&
                         (s.SensorType is SensorType.Temperature or SensorType.Load or SensorType.Clock)))
                {
                    sb.AppendLine($"{new string(' ', (depth + 1) * 2)}- {s.SensorType}: {s.Name} = {s.Value:0.##}");
                }
                foreach (var sub in hw.SubHardware) Walk(sub, depth + 1);
            }
            foreach (var hw in _computer.Hardware) Walk(hw, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("枚举失败: " + ex.Message);
        }
        return sb.ToString();
    }

    /// <summary>LHM 示例中的标准访问器：递归刷新所有硬件。</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
