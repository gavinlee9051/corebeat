using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CoreBeat.Services;

/// <summary>
/// 垃圾清理引擎：分三级目标 —— 安全（用户临时）/ 管理员（系统临时、更新缓存）/ 谨慎（回收站、缩略图缓存）。
/// 诚实边界：只清"明确无用"的临时/缓存；占用中或需更高权限的文件逐个跳过，绝不影响运行中的程序。
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
}

public static class JunkCleaner
{
    public static bool IsAdmin()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private static string UserTemp => Path.GetTempPath();
    private static string WinTemp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    private static string UpdateCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
    private static string ThumbDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");

    /// <summary>扫描全部类别（不删任何东西）。</summary>
    public static List<CleanItem> Scan()
    {
        var items = new List<CleanItem>();

        items.Add(ScanDir("userTemp", "用户临时文件", CleanTier.Safe, UserTemp,
            "当前用户 %TEMP%（未占用即清，含近期）", 0));

        bool admin = IsAdmin();
        items.Add(ScanDir("winTemp", "系统临时文件", CleanTier.Admin, WinTemp,
            admin ? "C:\\Windows\\Temp（未占用即清，含近期）" : "需要管理员权限", 0));

        items.Add(ScanDir("updateCache", "系统更新缓存", CleanTier.Admin, UpdateCache,
            admin ? "Windows 更新下载缓存（占用中会自动跳过）" : "需要管理员权限", -1));

        // 本软件旧日志（保证"总有些可清"的安全项）
        items.Add(ScanDir("appLog", "CoreBeat 旧日志", CleanTier.Safe, App.LogDirectory,
            "本软件 3 天前的日志（安全）", 72));

        // 回收站
        var rb = new CleanItem { Key = "recycle", Name = "回收站", Tier = CleanTier.Cautious, Note = "清空回收站（谨慎）" };
        QueryRecycleBin(out long rbSize, out long rbItems);
        rb.Size = rbSize; rb.Files = (int)Math.Min(int.MaxValue, rbItems);
        items.Add(rb);

        // 缩略图缓存（安全，默认可选）
        items.Add(ScanFiles("thumb", "缩略图缓存", CleanTier.Safe, ThumbDir, "thumbcache_*.db",
            "Explorer 重建无碍；使用中可能跳过"));

        // 浏览器相关：按浏览器细分为独立项（缓存 = 安全默认可选 / 历史 = 谨慎）
        foreach (var (key, label, root) in BrowserRoots())
        {
            items.Add(ScanCacheIn(root, key + "Cache", label + " 缓存", CleanTier.Safe, label + " Cache/GPU/Code 缓存，不清登录态"));
            items.Add(ScanNamesIn(root, key + "History", label + " 历史记录", CleanTier.Cautious,
                new[] { "History", "History-journal", "Top Sites", "Top Sites-journal", "Visited Links" }, label + " 历史记录文件，不含登录态"));
        }

        // 系统使用痕迹（谨慎，默认不勾）
        items.Add(ScanDir("systemTraces", "系统最近使用", CleanTier.Cautious,
            Environment.GetFolderPath(Environment.SpecialFolder.Recent),
            "最近访问的文档/程序记录（.lnk）", 0));

        // 补充类别，好让"可清理量"更接近真实（含未占用的缓存/临时）
        items.Add(ScanDir("dxcache", "DirectX 着色器缓存", CleanTier.Safe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"), "显卡着色器缓存，重建无碍", 0));
        items.Add(ScanDir("inetcache", "Internet 临时文件", CleanTier.Safe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"), "IE/系统网络缓存（非登录态）", 0));
        items.Add(ScanDir("werReport", "Windows 错误报告", CleanTier.Safe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"), "崩溃/错误报告存档", 0));
        items.Add(ScanDir("deliveryCache", "传递优化缓存", CleanTier.Admin,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            "Windows 传递优化下载缓存（需管理员）", -1));

        // 系统日志 / 崩溃转储
        items.Add(ScanDir("winLogs", "Windows 系统日志", CleanTier.Admin,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"),
            "Windows 系统日志（需管理员）", -1));
        items.Add(ScanDir("crashDumps", "Windows 错误转储", CleanTier.Cautious,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            "崩溃转储文件（谨慎）", 0));

        return items;
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

    /// <summary>采集某浏览器缓存类子目录下的文件（Cache/GPUCache/Code Cache/cache2 等）。</summary>
    private static CleanItem ScanCacheIn(string root, string key, string name, CleanTier tier, string note)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return it;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string d;
            try { d = pending.Pop(); } catch { break; }
            string dn = System.IO.Path.GetFileName(d).ToLowerInvariant();
            bool isCache = dn.Contains("cache") || dn.Contains("cache2");
            string[] files; string[] sub;
            try { files = Directory.GetFiles(d); sub = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in sub) pending.Push(s);
            foreach (var f in files)
            {
                if (!isCache) continue;
                try { var fi = new FileInfo(f); it.Size += fi.Length; it.Files++; it.Paths!.Add(f); }
                catch { }
            }
        }
        return it;
    }

    /// <summary>采集某浏览器指定文件名（历史/快捷书签等）。</summary>
    private static CleanItem ScanNamesIn(string root, string key, string name, CleanTier tier, string[] names, string note)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return it;
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string d;
            try { d = pending.Pop(); } catch { break; }
            string[] files; string[] sub;
            try { files = Directory.GetFiles(d); sub = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in sub) pending.Push(s);
            foreach (var f in files)
            {
                try
                {
                    if (!nameSet.Contains(System.IO.Path.GetFileName(f))) continue;
                    var fi = new FileInfo(f); it.Size += fi.Length; it.Files++; it.Paths!.Add(f);
                }
                catch { }
            }
        }
        return it;
    }

    private static CleanItem ScanDir(string key, string name, CleanTier tier, string dir, string note, double cutoffHours)
    {
        var it = new CleanItem { Key = key, Name = name, Tier = tier, Note = note, Paths = new List<string>() };
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return it;

        long now = DateTime.Now.Ticks;
        long cutoffTicks = cutoffHours > 0 ? (long)(TimeSpan.FromHours(cutoffHours).Ticks) : -1;
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            string d;
            try { d = pending.Pop(); }
            catch { break; }

            string[] files;
            string[] sub;
            try { files = Directory.GetFiles(d); sub = Directory.GetDirectories(d); }
            catch { continue; }
            foreach (var s in sub) pending.Push(s);
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (cutoffTicks > 0 && now - fi.LastWriteTime.Ticks < cutoffTicks) continue; // 新文件不碰
                    if (IsExcludedPath(f, key == "appLog")) continue; // 注定占用/不该当垃圾的排除
                    it.Size += fi.Length;
                    it.Files++;
                    it.Paths!.Add(f);
                }
                catch { }
            }
        }
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

    /// <summary>清理指定类别，返回 (释放字节, 成功数, 失败/跳过数)。</summary>
    public static (long Freed, int Ok, int Fail) Clean(CleanItem item)
    {
        InvalidateSummary();
        long freed = 0; int ok = 0, fail = 0;

        if (item.Key == "recycle")
        {
            bool done = EmptyRecycleBin();
            // 用删除前的统计近似反馈（Shell 不返回释放量）
            return done ? (item.Size, item.Files, 0) : (0, 0, 1);
        }

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
                    Interlocked.Add(ref freedL, len);
                    Interlocked.Increment(ref okC);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failC);
                    if (Interlocked.Increment(ref logGuard) <= 20)
                        App.Log($"清理跳过 {p}：{ex.GetType().Name} {ex.Message}");
                }
            });
            freed = freedL; ok = okC; fail = failC;
            // 顺手清掉删空后的临时子目录（用户/系统临时）
            if (item.Key == "userTemp") TrimEmptyDirs(UserTemp);
            else if (item.Key == "winTemp") TrimEmptyDirs(WinTemp);
        }
        return (freed, ok, fail);
    }

    /// <summary>托盘"一键"用的快速预设：用户临时 + （若提权）系统临时。单次扫描避免重复。</summary>
    public static (long Freed, int Ok, int Fail) QuickClean()
    {
        long freed = 0; int ok = 0, fail = 0;
        foreach (var it in Scan())
        {
            if (it.Key is "userTemp" or "winTemp")
            {
                var r = Clean(it);
                freed += r.Freed; ok += r.Ok; fail += r.Fail;
            }
        }
        return (freed, ok, fail);
    }

    /// <summary>只统计"安全级且当前真正可删"（30s 内缓存，避免每 45s/清理后反复整树扫描）。</summary>
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
        long t = 0; int f = 0;
        foreach (var it in Scan())
        {
            if (it.Tier != CleanTier.Safe || it.Paths == null) continue;
            foreach (var p in it.Paths)
            {
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

    /// <summary>主界面卡片"一键清理"：只清安全级（缓存/临时等），占用自动跳过。</summary>
    public static (long Freed, int Ok, int Fail) QuickCleanSafe()
    {
        long freed = 0; int ok = 0, fail = 0;
        foreach (var it in Scan())
            if (it.Tier == CleanTier.Safe) { var r = Clean(it); freed += r.Freed; ok += r.Ok; fail += r.Fail; }
        return (freed, ok, fail);
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

    /// <summary>删除已清空的临时子目录（自底向上，仅空目录；失败跳过）。</summary>
    private static void TrimEmptyDirs(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var d in Directory.GetDirectories(root))
            {
                TrimEmptyDirs(d);
                try { if (!Directory.GetFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        catch { }
    }
}
