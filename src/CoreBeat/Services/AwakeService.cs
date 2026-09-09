using System.Runtime.InteropServices;

namespace CoreBeat.Services;

/// <summary>防睡眠档位（与 Windows 方案 1.3 节一致）。</summary>
public enum AwakeLevel : byte
{
    Off = 0,
    PreventSleep = 1,       // ① 仅防睡
    PreventDisplay = 2,     // ② 防睡 + 防熄屏
    AntiLock = 3,           // ③ 加强防锁（叠加极低频鼠标微动）
}

/// <summary>防睡眠状态快照（供托盘与前端展示）。</summary>
public sealed record AwakeState(AwakeLevel Level, string Label, int? RemainSec);

/// <summary>
/// 防睡眠 / 防自动锁屏服务（Win32）。
/// 诚实边界：只能拦截“闲置触发”的睡眠/熄屏/锁屏；Win+L 等手动锁屏无法拦截。
/// 退出或关停时调用 <see cref="Restore"/> 还原系统设置。
/// </summary>
public sealed class AwakeService : IDisposable
{
    // ES_*：SetThreadExecutionState
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_CONTINUOUS = 0x80000000;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // 鼠标微动（防自动锁屏）：legacy mouse_event，标量参数最稳，兼容 x86/x64
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private AwakeLevel _level = AwakeLevel.Off;
    private int? _remainSec;          // 倒计时剩余（null=不限时）
    private long _lastNudgeTicks;

    /// <summary>状态变化（档位/剩余秒整分变化）时触发。</summary>
    public event Action<AwakeState>? Changed;

    public AwakeService()
    {
        _timer = new System.Threading.Timer(Tick, null, 500, 1000);
    }

    public AwakeState State
    {
        get
        {
            lock (_gate)
            {
                return new AwakeState(_level, LevelLabel(_level), _remainSec);
            }
        }
    }

    public void SetLevel(AwakeLevel level)
    {
        lock (_gate)
        {
            _level = level;
            if (level == AwakeLevel.Off) _remainSec = null;
        }
        Apply();
    }

    /// <summary>设定倒计时（分钟）；不限时传 null。档位为 Off 时此调用会被忽略。</summary>
    public void SetTimeoutMinutes(int? minutes)
    {
        lock (_gate)
        {
            if (_level == AwakeLevel.Off) return;
            _remainSec = minutes.HasValue ? minutes.Value * 60 : null;
        }
        RaiseChanged();
    }

    private void Tick(object? _)
    {
        bool raise = false;
        lock (_gate)
        {
            if (_level != AwakeLevel.Off && _remainSec is { } remain)
            {
                _remainSec = remain - 1;
                if (_remainSec <= 0)
                {
                    _remainSec = null;
                    _level = AwakeLevel.Off;   // 到期自动还原
                    raise = true;
                }
                else if (remain % 60 == 0)
                {
                    raise = true;              // 整分更新一次（省 UI 开销）
                }
            }
        }
        if (raise) RaiseChanged();

        if (State.Level == AwakeLevel.AntiLock) MaybeNudge();
    }

    private void MaybeNudge()
    {
        long now = Environment.TickCount64;
        if (now - _lastNudgeTicks < 45_000) return;   // 45s 一次
        _lastNudgeTicks = now;
        try
        {
            // 2px 微动再移回：极低频、肉眼几乎无感，足以刷新系统“闲置”计时
            MoveRelative(2, 0);
            Thread.Sleep(120);
            MoveRelative(-2, 0);
        }
        catch { /* 失败无害 */ }
    }

    private static void MoveRelative(int dx, int dy)
    {
        mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, UIntPtr.Zero);
    }

    private void Apply()
    {
        var state = State;
        uint flags = ES_CONTINUOUS;
        if (state.Level >= AwakeLevel.PreventSleep) flags |= ES_SYSTEM_REQUIRED;
        if (state.Level >= AwakeLevel.PreventDisplay) flags |= ES_DISPLAY_REQUIRED;
        SetThreadExecutionState(flags);
        App.Log($"防睡眠档位 -> {state.Label}");
        RaiseChanged();
    }

    /// <summary>还原系统默认（退出/关停时调用）。</summary>
    public void Restore()
    {
        lock (_gate) { _level = AwakeLevel.Off; _remainSec = null; }
        SetThreadExecutionState(ES_CONTINUOUS);
        App.Log("防睡眠已还原（系统默认）");
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(State);

    private static string LevelLabel(AwakeLevel l) => l switch
    {
        AwakeLevel.Off => "已关闭",
        AwakeLevel.PreventSleep => "仅防睡",
        AwakeLevel.PreventDisplay => "防睡 + 防熄屏",
        AwakeLevel.AntiLock => "加强防锁",
        _ => "未知",
    };

    public void Dispose()
    {
        _timer.Dispose();
        Restore();
    }
}
