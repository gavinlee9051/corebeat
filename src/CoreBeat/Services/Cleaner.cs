using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CoreBeat.Services;

/// <summary>
/// 垃圾清理引擎：分三级目标 —— 安全（用户临时）/ 管理员（系统临时、更新缓存）/ 谨慎（回收站、缩略图缓存）。
/// 诚实边界：只清"明确无用"的临时/缓存；占用中或需更高权限的文件逐个跳过，绝不影响运行中的程序。
///
/// 性能约束（重要）：系统更新缓存等目录可达数十万文件，因此所有遍历都必须
///   ① 跳过重解析点（junction/符号链接），避免目录成环导致的无限递归；
///   ② 受时间预算约束，超时即截断并标记 Partial（绝不允许扫描无限期挂住）；
///   ③ 限制记录的路径条数，超限只继续统计、不再累积路径（清理时按整树删，不受此限）。
/// </summary>
public enum CleanTier { Safe, Admin, Cautious }

public sealed class CleanItem
{
    public string Key = "";
    public string Name = "";
    public CleanTier Tier;
    public long Size;          // 字节
    public int Files;
    public string? Note;       // 附加说明
    public List<string>? Paths;
    public string? Root;       // 目录型类别：清理时按整树删（不受抽样上限影响）
    public long CutoffTicks;   // 只删更旧的文件；-1 = 不过滤
    public bool Partial;       // 目录过大被截断，统计为「≥」
}

public static class JunkCleaner
{
    /// <summary>单个类别扫描的时间预算（毫秒）。超时截断，保证整体扫描不会挂住。</summary>
    private const int ScanBudgetMs = 2500;
    /// <summary>单个类别最多记录的路径条数（超出只统计不记录；清理按 Root 整树处理）。</summary>
    private const int MaxPathsPerItem = 15000;
    /// <summary>单个类别清理的时间上限（毫秒）。</summary>
    private const int CleanBudgetMs = 180000;
    /// <summary>SafeSummary 可删探测的取样上限，避免逐个开句柄拖慢主界面。</summary>
    private const int MaxProbeFiles = 4000;

    public static bool IsAdmin()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private static string UserTemp => Path.GetTempPath();
    private static string WinTemp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    private static string UpdateCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
    private static string ThumbDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");

    /// <summary>
    /// 扫描类别（不删任何东西）。各类别并行执行，整体受单项时间预算约束。
    /// tierFilter 可按层级过滤（如 SafeSummary 只要 Safe，避免白扫数 GB 管理员级目录）；
    /// keyFilter 可按类别键过滤（如托盘"一键"只要 userTemp/winTemp）。
    /// </summary>
    public static List<CleanItem> Scan(Func<CleanTier, bool>? tierFilter = null, string[]? keyFilter = null)
    {
        bool admin = IsAdmin();
        var jobs = new List<(string Key, CleanTier Tier, Func<CleanItem> Run)>
        {
            ("userTemp", CleanTier.Safe, () => ScanDir("userTemp", "用户临时文件", CleanTier.Safe, UserTemp,
                "当前用户 %TEMP%（未占用即清，含近期）", 0)),
            ("winTemp", CleanTier.Admin, () => ScanDir("winTemp", "系统临时文件", CleanTier.Admin, WinTemp,
                "C:\\Windows\\Temp（未占用即清，含近期）", 0, adminOnly: true)),
            ("updateCache", CleanTier.Admin, () => ScanDir("updateCache", "系统更新缓存", CleanTier.Admin, UpdateCache,
                "Windows 更新下载缓存（占用中会自动跳过）", -1, adminOnly: true)),
            // 本软件旧日志（保证"总有些可清"的安全项）
            ("appLog", CleanTier.Safe, () => ScanDir("appLog", "CoreBeat 旧日志", CleanTier.Safe, App.LogDirectory,
                "本软件 3 天前的日志（安全）", 72)),
            ("recycle", CleanTier.Cautious, () => ScanRecycle()),
            ("thumb", CleanTier.Safe, () => ScanFiles("thumb", "缩略图缓存", CleanTier.Safe, ThumbDir, "thumbcache_*.db",
                "Explorer 重建无碍；使用中可能跳过")),
            ("systemTraces", CleanTier.Cautious, () => ScanDir("systemTraces", "系统最近使用", CleanTier.Cautious,
                Environment.GetFolderPath(Environment.SpecialFolder.Recent),
                "最近访问的文档/程序记录（.lnk）", 0)),
            ("dxcache", CleanTier.Safe, () => ScanDir("dxcache", "DirectX 着色器缓存", CleanTier.Safe,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"),
                "显卡着色器缓存，重建无碍", 0)),
            ("inetcache", CleanTier.Safe, () => ScanDir("inetcache", "Internet 临时文件", CleanTier.Safe,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"),
                "IE/系统网络缓存（非登录态）", 0)),
            ("werReport", CleanTier.Safe, () => ScanDir("werReport", "Windows 错误报告", CleanTier.Safe,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"),
                "崩溃/错误报告存档", 0)),
            ("deliveryCache", CleanTier.Admin, () => ScanDir("deliveryCache", "传递优化缓存", CleanTier.Admin,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                "Windows 传递优化下载缓存（需管理员）", -1, adminOnly: true)),
            ("winLogs", CleanTier.Admin, () => ScanDir("winLogs", "Windows 系统日志", CleanTier.Admin,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"),
                "Windows 系统日志（需管理员）", -1, adminOnly: true)),
            ("crashDumps", CleanTier.Cautious, () => ScanDir("crashDumps", "Windows 错误转储", CleanTier.Cautious,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                "崩溃转储文件（谨慎）", 0)),
        };

        // 浏览器相关：按浏览器细分为独立项（缓存 = 安全默认可选 / 历史 = 谨慎）
        foreach (var (bkey, label, root) in BrowserRoots())
        {
            string k = bkey, l = label, r = root;
            jobs.Add((k + "Cache", CleanTier.Safe,
                () => ScanCacheIn(r, k + "Cache", l + " 缓存", CleanTier.Safe, l + " Cache/GPU/Code 缓存，不清登录态")));
            jobs.Add((k + "History", CleanTier.Cautious,
                () => ScanNamesIn(r, k + "History", l + " 历史记录", CleanTier.Cautious,
                    new[] { "History", "History-journal", "Top Sites", "Top Sites-journal", "Visited Links" },
                    l + " 历史记录文件，不含登录态")));
        }

        // 先过滤再执行：不需要的类别根本不扫（关键性能优化）
        var run = new List<Func<CleanItem>>();
        foreach (var j in jobs)
        {
            if (tierFilter != null && !tierFilter(j.Tier)) continue;
            if (keyFilter != null && Array.IndexOf(keyFilter, j.Key) < 0) continue;
            run.Add(j.Run);
        }

        var results = new CleanItem?[run.Count];
        System.Threading.Tasks.Parallel.For(0, run.Count,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, i =>
        {
            try { results[i] = run[i](); }
            catch { results[i] = null; } // 单类别失败不影响其它类别
        });

        var items = new List<CleanItem>();
        foreach (var it in results) if (it != null) items.Add(it);
        return items;
    }

    private static CleanItem ScanRecycle()
    {
        var rb = new CleanItem { Key = "recycle", Name = "回收站", Tier = CleanTier.Cautious, Note = "清空回收站（谨慎）" };
        QueryRecycleBin(out long rbSize, out long rbItems);
        rb.Size = rbSize; rb.Files = (int)Math.Min(int.MaxValue, rbItems);
        return rb;
    }

    private static (string Key, string Label, string Root)[] BrowserRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            ("chrome", "Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("edge", "Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("brave", "Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        };
    }

    // ---------- 遍历内核 ----------

    /// <summary>
    /// 目录枚举选项：在枚举层面跳过重解析点（junction/符号链接），
    /// 既避免目录成环导致无限递归，又省掉逐条 File.GetAttributes 的系统调用开销；
    /// IgnoreInaccessible 让无权限目录直接跳过而不是抛异常。
    /// 注意 AttributesToSkip 必须显式只设 ReparsePoint——默认值会连 Hidden/System 一起跳过。
    /// </summary>
    private static readonly EnumerationOptions DirOpts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };
    private static readonly EnumerationOptions FileOpts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// 受预算约束的深度遍历。onFile(所在目录, 文件路径) 处理每个文件。
    /// 用流式枚举（Enumerate*）而非 GetFiles：既不一次性物化超大目录（内存尖峰），
    /// 又能在条目之间检查时间预算（GetFiles 在巨量目录上会长时间不可中断）。
    /// </summary>
    private static void Walk(string root, Action<string, string> onFile, out bool truncated)
    {
        truncated = false;
        var sw = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (sw.ElapsedMilliseconds > ScanBudgetMs) { truncated = true; break; }
            string d;
            try { d = pending.Pop(); } catch { break; }

            // 子目录入栈（枚举层面已跳过重解析点；同样受预算约束）
            try
            {
                foreach (var s in Directory.EnumerateDirectories(d, "*", DirOpts))
                {
                    if (sw.ElapsedMilliseconds > ScanBudgetMs) { truncated = true; break; }
                    pending.Push(s);
                }
            }
            catch { }
            if (truncated) break;

            // 文件逐个处理，条目之间检查预算
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", FileOpts))
                {
                    if (sw.ElapsedMilliseconds > ScanBudgetMs) { truncated = true; break; }
                    try { onFile(d, f); } catch { }
                }
            }
            catch { }
        }
    }

    private static CleanItem ScanDir(string key, string name, CleanTier tier, string dir, string note, double cutoffHours, bool adminOnly = false)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>(), CutoffTicks = -1 };
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return it;

        // 需管理员却未提权：直接跳过扫描（清了也会失败，扫描纯属浪费）
        if (adminOnly && !IsAdmin()) { it.Note = note + "（需管理员，已跳过）"; return it; }

        it.Root = dir;
        long now = DateTime.Now.Ticks;
        if (cutoffHours > 0) it.CutoffTicks = (long)TimeSpan.FromHours(cutoffHours).Ticks;
        bool allowApp = key == "appLog";

        Walk(dir, (d, f) =>
        {
            var fi = new FileInfo(f);
            if (it.CutoffTicks > 0 && now - fi.LastWriteTime.Ticks < it.CutoffTicks) return; // 新文件不碰
            if (IsExcludedPath(f, allowApp)) return;
            it.Size += fi.Length;
            it.Files++;
            if (it.Paths!.Count < MaxPathsPerItem) it.Paths.Add(f);
            else it.Partial = true;
        }, out bool truncated);
        if (truncated) it.Partial = true;
        return it;
    }

    /// <summary>采集某浏览器缓存类子目录下的文件（Cache/GPUCache/Code Cache/cache2 等）。</summary>
    private static CleanItem ScanCacheIn(string root, string key, string name, CleanTier tier, string note)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return it;

        Walk(root, (d, f) =>
        {
            if (!IsCacheDirName(Path.GetFileName(d))) return;
            if (IsExcludedPath(f, false)) return;
            var fi = new FileInfo(f);
            it.Size += fi.Length;
            it.Files++;
            if (it.Paths!.Count < MaxPathsPerItem) it.Paths.Add(f);
            else it.Partial = true;
        }, out bool truncated);
        if (truncated) it.Partial = true;
        return it;
    }

    private static bool IsCacheDirName(string dirName)
    {
        var n = dirName.ToLowerInvariant();
        return n.Contains("cache") || n.Contains("cache2");
    }

    /// <summary>采集某浏览器指定文件名（历史/快捷书签等）。</summary>
    private static CleanItem ScanNamesIn(string root, string key, string name, CleanTier tier, string[] names, string note)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return it;
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        Walk(root, (d, f) =>
        {
            if (!nameSet.Contains(Path.GetFileName(f))) return;
            var fi = new FileInfo(f);
            it.Size += fi.Length;
            it.Files++;
            if (it.Paths!.Count < MaxPathsPerItem) it.Paths.Add(f);
            else it.Partial = true;
        }, out bool truncated);
        if (truncated) it.Partial = true;
        return it;
    }

    private static CleanItem ScanFiles(string key, string name, CleanTier tier, string dir, string pattern, string note)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return it;
        try
        {
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                try
                {
                    if (IsExcludedPath(f, false)) continue;
                    var fi = new FileInfo(f); it.Size += fi.Length; it.Files++; it.Paths!.Add(f);
                }
                catch { }
            }
        }
        catch { }
        return it;
    }

    /// <summary>注定清不掉/不应视为垃圾的路径（编译器加载器、应用自身 WebView2 数据等）。</summary>
    private static bool IsExcludedPath(string p, bool allowAppData)
    {
        if (p.IndexOf(@"\VBCSCompiler\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (p.IndexOf(@"\AnalyzerAssemblyLoader\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (p.IndexOf(@"\WebView2\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (!allowAppData && p.StartsWith(App.LogDirectory, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---------- 清理 ----------

    /// <summary>清理指定类别，返回 (释放字节, 成功数, 失败/跳过数)。</summary>
    public static (long Freed, int Ok, int Fail) Clean(CleanItem item)
    {
        InvalidateSummary();

        if (item.Key == "recycle")
        {
            bool done = EmptyRecycleBin();
            // 用删除前的统计近似反馈（Shell 不返回释放量）
            return done ? (item.Size, item.Files, 0) : (0, 0, 1);
        }

        // 目录型：按整树删（不受扫描抽样上限影响，确保真正清空）
        if (!string.IsNullOrEmpty(item.Root) && Directory.Exists(item.Root))
            return PurgeTree(item.Root, item.CutoffTicks, item.Key == "appLog");

        if (item.Paths != null)
        {
            long freedL = 0; int okC = 0, failC = 0; int logGuard = 0;
            System.Threading.Tasks.Parallel.ForEach(item.Paths, p =>
            {
                try
                {
                    var fi = new FileInfo(p);
                    long len = fi.Length;
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal; // 清只读再删
                    fi.Delete();
                    System.Threading.Interlocked.Add(ref freedL, len);
                    System.Threading.Interlocked.Increment(ref okC);
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Increment(ref failC);
                    if (System.Threading.Interlocked.Increment(ref logGuard) <= 20)
                        App.Log($"清理跳过 {p}：{ex.GetType().Name} {ex.Message}");
                }
            });
            return (freedL, okC, failC);
        }
        return (0, 0, 0);
    }

    /// <summary>
    /// 整树清理：逐目录并行删文件（占用/无权限逐个跳过），最后自底向上删空目录。
    /// 相比"逐条路径删"更彻底，且不受扫描阶段的路径条数上限影响。
    /// </summary>
    private static (long Freed, int Ok, int Fail) PurgeTree(string root, long cutoffTicks, bool allowApp)
    {
        long freed = 0; int ok = 0, fail = 0;
        long now = DateTime.Now.Ticks;
        var sw = Stopwatch.StartNew();
        var stack = new Stack<string>();
        stack.Push(root);
        var seenDirs = new List<string>();

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > CleanBudgetMs) break;
            string d;
            try { d = stack.Pop(); } catch { break; }
            seenDirs.Add(d);

            // 子目录入栈（枚举层面已跳过重解析点）
            try
            {
                foreach (var s in Directory.EnumerateDirectories(d, "*", DirOpts))
                {
                    if (sw.ElapsedMilliseconds > CleanBudgetMs) break;
                    stack.Push(s);
                }
            }
            catch { }

            // 逐条枚举文件并就地删除（小批量并行，避免物化整个目录文件表）
            var batch = new List<string>(256);
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", FileOpts))
                {
                    if (sw.ElapsedMilliseconds > CleanBudgetMs) break;
                    batch.Add(f);
                    if (batch.Count >= 256)
                    {
                        DeleteBatch(batch, cutoffTicks, now, allowApp, ref freed, ref ok, ref fail);
                        batch.Clear();
                    }
                }
            }
            catch { }
            if (batch.Count > 0) DeleteBatch(batch, cutoffTicks, now, allowApp, ref freed, ref ok, ref fail);
        }

        // 自底向上删空目录（root 本身保留）
        for (int i = seenDirs.Count - 1; i >= 0; i--)
        {
            var d = seenDirs[i];
            if (string.Equals(d, root, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        return (freed, ok, fail);
    }

    /// <summary>小批量并行删除（占用/无权限逐个跳过），结果累加回调用方计数器。</summary>
    private static void DeleteBatch(List<string> batch, long cutoffTicks, long now, bool allowApp,
        ref long freed, ref int ok, ref int fail)
    {
        long f = 0; int o = 0, x = 0;
        System.Threading.Tasks.Parallel.ForEach(batch, p =>
        {
            try
            {
                var fi = new FileInfo(p);
                if (cutoffTicks > 0 && now - fi.LastWriteTime.Ticks < cutoffTicks) return; // 新文件不碰
                if (IsExcludedPath(p, allowApp)) return;
                long len = fi.Length;
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal;
                fi.Delete();
                System.Threading.Interlocked.Add(ref f, len);
                System.Threading.Interlocked.Increment(ref o);
            }
            catch { System.Threading.Interlocked.Increment(ref x); }
        });
        freed += f; ok += o; fail += x;
    }

    /// <summary>托盘"一键"用的快速预设：用户临时 + （若提权）系统临时。只扫这两类，不碰其它大目录。</summary>
    public static (long Freed, int Ok, int Fail) QuickClean()
    {
        long freed = 0; int ok = 0, fail = 0;
        foreach (var it in Scan(keyFilter: new[] { "userTemp", "winTemp" }))
        {
            var r = Clean(it);
            freed += r.Freed; ok += r.Ok; fail += r.Fail;
        }
        return (freed, ok, fail);
    }

    /// <summary>主界面卡片"一键清理"：只清安全级（缓存/临时等），占用自动跳过。</summary>
    public static (long Freed, int Ok, int Fail) QuickCleanSafe()
    {
        long freed = 0; int ok = 0, fail = 0;
        foreach (var it in Scan(t => t == CleanTier.Safe))
        {
            var r = Clean(it); freed += r.Freed; ok += r.Ok; fail += r.Fail;
        }
        return (freed, ok, fail);
    }

    // ---------- 可释放量汇总（30s 缓存，避免反复整树扫描） ----------

    private static readonly object SumLock = new();
    private static long _sumAtTicks;
    private static (long Total, int Files) _sumCache;

    public static (long Total, int Files) SafeSummary()
    {
        lock (SumLock)
        {
            if (_sumAtTicks != 0 && Environment.TickCount64 - _sumAtTicks < 30_000) return _sumCache;
        }
        var r = ComputeSafeSummary();
        lock (SumLock) { _sumCache = r; _sumAtTicks = Environment.TickCount64; }
        return r;
    }
    private static void InvalidateSummary() { lock (SumLock) { _sumAtTicks = 0; } }

    private static (long Total, int Files) ComputeSafeSummary()
    {
        long t = 0; int f = 0, probed = 0;
        // 只扫 Safe 级：主界面周期性刷新时不会去扫数 GB 的管理员级目录（更新缓存等）
        foreach (var it in Scan(tier => tier == CleanTier.Safe))
        {
            if (it.Paths == null) continue;
            foreach (var p in it.Paths)
            {
                if (probed >= MaxProbeFiles) return (t, f); // 取样上限：避免逐个开句柄拖慢主界面
                probed++;
                if (!CanFree(p)) continue;
                try { t += new FileInfo(p).Length; f++; } catch { }
            }
        }
        return (t, f);
    }

    /// <summary>可删探测：以会与占用冲突的方式打开；被占用则抛异常（返回 false）。</summary>
    private static bool CanFree(string p)
    {
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch { return false; }
    }

    // ---------- 回收站 ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
    [DllImport("shell32.dll")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    private static void QueryRecycleBin(out long size, out long items)
    {
        size = 0; items = 0;
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0) { size = info.i64Size; items = info.i64NumItems; }
        }
        catch { }
    }

    private static bool EmptyRecycleBin()
    {
        try { return SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0; }
        catch { return false; }
    }
}
