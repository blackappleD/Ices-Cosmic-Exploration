using ECommons.GameHelpers;
using static ICE.ConfigFiles.Config;

namespace ICE.Utilities;

/// <summary>
/// 运行时长统计：只累计 SchedulerMain.State != Idle 的时间，按角色记到 C.CharacterRuntimes。
/// 状态从 Idle 变为运行时开始新的一次运行，回到 Idle 时结束并保存。
/// </summary>
internal static class RuntimeTracker
{
    private const long SaveIntervalMs = 60_000;

    private static bool _running;
    private static ulong _cid;
    private static long _lastTick;
    private static long _lastSave;

    internal static bool IsRunning => _running;

    internal static void Tick()
    {
        bool running = SchedulerMain.State != IceState.Idle;
        long now = Environment.TickCount64;

        if (running && !_running)
        {
            _running = true;
            _cid = Player.Available ? (ulong)Player.CID : 0;
            _lastTick = now;
            _lastSave = now;
            if (_cid != 0)
                Get(_cid).LastRunSeconds = 0;
            return;
        }

        if (!running)
        {
            if (_running)
            {
                Accumulate(now);
                _running = false;
                _cid = 0;
                C.Save();
            }
            return;
        }

        // 开始时角色还不可用（极少见），等角色可用后再归属
        if (_cid == 0 && Player.Available)
        {
            _cid = (ulong)Player.CID;
            Get(_cid).LastRunSeconds = 0;
        }

        Accumulate(now);

        if (now - _lastSave > SaveIntervalMs)
        {
            _lastSave = now;
            C.Save();
        }
    }

    /// <summary>插件卸载时把正在进行的运行时长写盘。</summary>
    internal static void Flush()
    {
        if (!_running)
            return;
        Accumulate(Environment.TickCount64);
        C.Save();
    }

    private static void Accumulate(long now)
    {
        double delta = (now - _lastTick) / 1000.0;
        _lastTick = now;
        if (_cid == 0 || delta <= 0)
            return;

        var rt = Get(_cid);
        rt.TotalSeconds += delta;
        rt.LastRunSeconds += delta;
    }

    internal static CharacterRuntime Get(ulong cid)
    {
        if (!C.CharacterRuntimes.TryGetValue(cid, out var rt))
            C.CharacterRuntimes[cid] = rt = new();
        return rt;
    }

    /// <summary>该角色当前是否正在运行（用于界面高亮）。</summary>
    internal static bool IsRunningFor(ulong cid) => _running && _cid == cid;

    /// <summary>当前登录角色的本次运行时长。</summary>
    internal static double CurrentCharacterRunSeconds
        => Player.Available && C.CharacterRuntimes.TryGetValue((ulong)Player.CID, out var rt) ? rt.LastRunSeconds : 0;

    internal static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
