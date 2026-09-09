using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace CoreBeat.Services;

/// <summary>
/// 网络测速（真实吞吐，非伪造）：
/// 固定可靠节点自动选最快（Cloudflare 主 / OVH·Hetzner 备）；
/// 延迟/抖动/丢包用 ICMP(失败退 HTTPS 往返)，下行多流、上行单流，均按"字节/秒"计算。
/// </summary>
public static class SpeedTest
{
    private sealed class Node { public string Name = ""; public string Base = ""; public string PingHost = ""; public string DownUrl = ""; public bool HasUp; }
    private static readonly Node[] Nodes =
    {
        new() { Name = "阿里云", PingHost = "223.5.5.5", DownUrl = "https://mirrors.aliyun.com/ubuntu/ls-lR.gz", HasUp = false },
        new() { Name = "清华", PingHost = "119.29.29.29", DownUrl = "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/ls-lR.gz", HasUp = false },
        new() { Name = "腾讯云", PingHost = "mirrors.tencent.com", DownUrl = "https://mirrors.tencent.com/ubuntu/ls-lR.gz", HasUp = false },
        new() { Name = "中科大", PingHost = "mirrors.ustc.edu.cn", DownUrl = "https://mirrors.ustc.edu.cn/ubuntu/ls-lR.gz", HasUp = false },
        new() { Name = "Cloudflare", PingHost = "1.1.1.1", DownUrl = "https://speed.cloudflare.com/__down?bytes=400000000", HasUp = true },
    };

    /// <summary>运行测速；emit(phase, value, text) 在 UI 线程由调用方回调（phase: node/ping/down/up/done）。</summary>
    public static async Task RunAsync(Action<string, double, string> emit)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // 并发探测各节点首字节时延，选最快可达（2.5s 截止，避免逐个串行等超时）
            var pickCts = new CancellationTokenSource(TimeSpan.FromSeconds(2500));
            var pick = await Task.WhenAll(Nodes.Select(async (n) =>
            {
                var sw = Stopwatch.StartNew();
                try { using var r = await client.GetAsync(n.DownUrl, HttpCompletionOption.ResponseHeadersRead, pickCts.Token); r.EnsureSuccessStatusCode(); return (n, sw.ElapsedMilliseconds); }
                catch { return (n, long.MaxValue); }
            }));
            var ok = pick.Where(x => x.Item2 != long.MaxValue).OrderBy(x => x.Item2).ToList();
            Node node = ok.Count > 0 ? ok[0].n : Nodes[0];
            emit("node", 0, node.Name);
            double down = await MeasureTransfer(client, node.DownUrl, true, emit);
            if (down <= 0)
            {
                foreach (var alt in Nodes)
                {
                    if (alt == node) continue;
                    down = await MeasureTransfer(client, alt.DownUrl, true, emit);
                    if (down > 0) { node = alt; emit("node", 0, node.Name); break; }
                }
            }

            // 延迟/抖动/丢包（ICMP 15 次）
            double lat = 0, jitter = 0, loss = 0; int okCnt = 0;
            var latSamples = new List<double>();
            using (var ping = new Ping())
            {
                for (int i = 0; i < 15; i++)
                {
                    try
                    {
                        var rep = await ping.SendPingAsync(node.PingHost, 1200);
                        if (rep.Status == IPStatus.Success) { latSamples.Add(rep.RoundtripTime); okCnt++; }
                    }
                    catch { }
                    if (i % 5 == 4) emit("ping", okCnt > 0 ? latSamples[^1] : 0, $"{okCnt}/15");
                    await Task.Delay(80);
                }
            }
            loss = (15 - okCnt) * 100.0 / 15;
            if (latSamples.Count > 0)
            {
                latSamples.Sort();
                lat = latSamples[latSamples.Count / 2];
                double mean = 0; foreach (var s in latSamples) mean += s; mean /= latSamples.Count;
                double dev = 0; foreach (var s in latSamples) dev += (s - mean) * (s - mean);
                jitter = Math.Sqrt(dev / latSamples.Count);
            }
            emit("ping", lat, $"{lat:0} ms · 抖动 {jitter:0.0} ms · 丢包 {loss:0}%");

            // 上行：多端点自动探测；若用户配置了自定义上传地址则优先
            double up = 0;
            var buf = new byte[4 * 1024 * 1024]; new Random(7).NextBytes(buf);
            var upEndpoints = new List<string>();
            string custom = CoreBeat.App.NetTestUploadUrl;
            if (!string.IsNullOrWhiteSpace(custom)) upEndpoints.Add(custom);
            upEndpoints.AddRange(new[] { "https://speed.cloudflare.com/__up", "https://httpbin.org/post", "https://postman-echo.com/post", "https://httpbingo.org/post", "https://httpbin.io/post" });
            foreach (var ep in upEndpoints)
            {
                try
                {
                    using var probe = await client.PostAsync(ep, new ByteArrayContent(new byte[65536]));
                    probe.EnsureSuccessStatusCode();
                    up = await MeasureUpload(client, ep, buf, emit);
                    if (up > 0) break;
                }
                catch { }
            }

            emit("done", down, $"{down:0.0}|{up:0.0}|{lat:0.0}|{jitter:0.00}|{loss:0}|{node.Name}");
        }
        catch (Exception ex)
        {
            emit("done", 0, "error|" + ex.Message);
        }
    }

    private static async Task<double> MeasureTransfer(HttpClient client, string url, bool isDown, Action<string, double, string> emit)
    {
        var bytes = new long[4]; var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task>();
        for (int s = 0; s < 4; s++)
        {
            int sidx = s;
            tasks.Add(Task.Run(async () =>
            {
                var buf = new byte[65536];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        resp.EnsureSuccessStatusCode();
                        await using var str = await resp.Content.ReadAsStreamAsync();
                        while (!cts.IsCancellationRequested)
                        {
                            int n = await str.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                            if (n <= 0) break;
                            Interlocked.Add(ref bytes[sidx], n);
                        }
                    }
                    catch { break; } // 出错（网络波动/文件末尾）则停止该流
                }
            }));
        }
        // 每 400ms 上报一次实时速率
        long Total() { long t = 0; for (int i = 0; i < bytes.Length; i++) t += Interlocked.Read(ref bytes[i]); return t; }
        long last = Total(); var lastT = sw.ElapsedMilliseconds;
        while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < 5500)
        {
            await Task.Delay(380);
            long cur = Total();
            long dt = sw.ElapsedMilliseconds - lastT; if (dt < 1) continue;
            double mbps = (cur - last) * 8.0 / 1000 / dt;
            emit(isDown ? "down" : "up", mbps, mbps.ToString("0.0"));
            last = cur; lastT = sw.ElapsedMilliseconds;
        }
        cts.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        return Total() * 8.0 / 1000 / Math.Max(1, sw.ElapsedMilliseconds);
    }

    private static async Task<double> MeasureUpload(HttpClient client, string url, byte[] buf, Action<string, double, string> emit)
    {
        var sw = Stopwatch.StartNew();
        long sent = 0; var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = new PushStreamContent();
        var task = client.PostAsync(url, body, cts.Token);
        body.SetWriter(async (stream) => { while (!cts.IsCancellationRequested) { await stream.WriteAsync(buf); Interlocked.Add(ref sent, buf.Length); } });
        long last = 0; var lastT = sw.ElapsedMilliseconds;
        while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(300);
            long cur = Interlocked.Read(ref sent);
            long dt = sw.ElapsedMilliseconds - lastT; if (dt < 1) continue;
            double mbps = (cur - last) * 8.0 / 1000 / dt;
            emit("up", mbps, mbps.ToString("0.0"));
            last = cur; lastT = sw.ElapsedMilliseconds;
        }
        cts.Cancel();
        try { await task; } catch { }
        long fin = Interlocked.Read(ref sent);
        return fin * 8.0 / 1000 / Math.Max(1, sw.ElapsedMilliseconds);
    }

    /// <summary>HttpContent 包装：允许回填写入回调（上传测速用）。</summary>
    private sealed class PushStreamContent : HttpContent
    {
        private Func<Stream, Task>? _writer;
        public void SetWriter(Func<Stream, Task> w) => _writer = w;
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        { if (_writer != null) await _writer(stream); }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }
}
