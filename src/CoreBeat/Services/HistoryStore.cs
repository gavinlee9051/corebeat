using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CoreBeat.Services;

/// <summary>
/// 24h 历史：按分钟聚合 CPU/内存/温度，滚动保留 1440 分钟（供绘图）；
/// 每分钟切档时将该分钟写一行本地 CSV，重启后载入续接（重启不丢）。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public sealed class Bucket
    {
        public long Minute;
        public double CpuSum, CpuN;
        public double MemSum, MemN;
        public double TempSum, TempN;
        public double CpuAvg => CpuN > 0 ? CpuSum / CpuN : 0;
        public double MemAvg => MemN > 0 ? MemSum / MemN : 0;
        public double TempAvg => TempN > 0 ? TempSum / TempN : 0;
    }

    private const int KeepMinutes = 24 * 60;
    private readonly object _gate = new();
    private readonly string _file;
    private readonly List<Bucket> _buckets = new();
    private readonly StreamWriter? _writer;
    private long _lastMinute = -1;

    public HistoryStore(string directory)
    {
        _file = Path.Combine(directory, "history24.csv");
        try
        {
            LoadFromDisk();
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(File.Open(_file, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            _writer.AutoFlush = true;
        }
        catch { /* 磁盘失败仅影响历史功能 */ }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_file)) return;
        long nowMin = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var byMin = new Dictionary<long, Bucket>();
        foreach (var line in File.ReadLines(_file))
        {
            var p = line.Split(',');
            if (p.Length < 3 || !long.TryParse(p[0], out long min)) continue;
            if (min < nowMin - KeepMinutes || min > nowMin) continue;
            var b = new Bucket { Minute = min };
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b.CpuSum);
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b.MemSum);
            double.TryParse(p.Length >= 4 ? p[3] : "0", NumberStyles.Float, CultureInfo.InvariantCulture, out b.TempSum);
            b.CpuN = b.MemN = b.TempN = 1;
            byMin[min] = b;   // 同分钟保留末次
        }
        _buckets.AddRange(byMin.Values.OrderBy(b => b.Minute));
        Trim();
        if (_buckets.Count > 0) _lastMinute = _buckets[^1].Minute;
    }

    private void Trim()
    {
        long oldest = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 - KeepMinutes;
        _buckets.RemoveAll(b => b.Minute < oldest);
        if (_buckets.Count > KeepMinutes)
            _buckets.RemoveRange(0, _buckets.Count - KeepMinutes);
    }

    /// <summary>每秒调用：累计到当前分钟桶；切分钟时把上一分钟落盘。</summary>
    public void Push(double cpuPct, double memPct, double? temp)
    {
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            Bucket b;
            if (_lastMinute != minute)
            {
                // 上一分钟封口并落盘
                if (_lastMinute >= 0 && _buckets.Count > 0)
                {
                    var closed = _buckets[^1];
                    PersistLine(closed);
                }
                b = new Bucket { Minute = minute };
                _buckets.Add(b);
                _lastMinute = minute;
                Trim();
            }
            else
            {
                b = _buckets.Count > 0 ? _buckets[^1] : new Bucket { Minute = minute };
                if (_buckets.Count == 0) _buckets.Add(b);
            }
            b.CpuSum += cpuPct; b.CpuN++;
            b.MemSum += memPct; b.MemN++;
            if (temp is { } tv) { b.TempSum += tv; b.TempN++; }
        }
    }

    private void PersistLine(Bucket b)
    {
        if (_writer is null) return;
        try
        {
            _writer.WriteLine(string.Join(',',
                b.Minute.ToString(CultureInfo.InvariantCulture),
                b.CpuAvg.ToString("0.0", CultureInfo.InvariantCulture),
                b.MemAvg.ToString("0.0", CultureInfo.InvariantCulture),
                b.TempAvg.ToString("0.0", CultureInfo.InvariantCulture)));
        }
        catch { }
    }

    /// <summary>供前端绘图：近 24h 每分钟点（CPU/内存平均，温度若有）。</summary>
    public string HistoryJson()
    {
        lock (_gate)
        {
            var pts = _buckets.Select(b => new
            {
                m = b.Minute,
                cpu = Math.Round(b.CpuAvg, 1),
                mem = Math.Round(b.MemAvg, 1),
                temp = b.TempN > 0 ? (double?)Math.Round(b.TempAvg, 1) : null,
            }).ToArray();
            return JsonSerializer.Serialize(new { t = "history", pts });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { if (_buckets.Count > 0) PersistLine(_buckets[^1]); } catch { }
            try { _writer?.Dispose(); } catch { }
        }
    }
}
