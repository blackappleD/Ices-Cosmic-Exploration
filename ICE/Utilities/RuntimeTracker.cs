using ECommons.GameHelpers;
using System.Collections.Generic;
using static ICE.ConfigFiles.Config;

namespace ICE.Utilities;

/// <summary>
/// 运行时长统计：只累计 SchedulerMain.State != Idle 的时间，按角色记录。
/// 总运行时长存到 C.CharacterRuntimes；本次运行时长只放在内存里，
/// 停止插件、离开宇宙探索地图都不会清零，只有卸载插件 / 关闭游戏才清零。
/// </summary>
internal static class RuntimeTracker
{
    private const long SaveIntervalMs = 60_000;

    private static readonly Dictionary<ulong, double> _sessionSeconds = new();

    private static bool _running;
    private static ulong _cid;
    private static long _lastTick;
    private static long _lastSave;

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
            _cid = (ulong)Player.CID;

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

        Get(_cid).TotalSeconds += delta;
        _sessionSeconds[_cid] = SessionSeconds(_cid) + delta;
    }

    internal static CharacterRuntime Get(ulong cid)
    {
        if (!C.CharacterRuntimes.TryGetValue(cid, out var rt))
            C.CharacterRuntimes[cid] = rt = new();
        return rt;
    }

    internal static double SessionSeconds(ulong cid)
        => _sessionSeconds.TryGetValue(cid, out var s) ? s : 0;

    internal static double TotalSeconds(ulong cid)
        => C.CharacterRuntimes.TryGetValue(cid, out var rt) ? rt.TotalSeconds : 0;

    internal static void Reset(ulong cid)
    {
        _sessionSeconds.Remove(cid);
        if (C.CharacterRuntimes.Remove(cid))
            C.Save();
    }

    /// <summary>该角色当前是否正在运行（用于界面高亮）。</summary>
    internal static bool IsRunningFor(ulong cid) => _running && _cid == cid;

    /// <summary>当前登录角色的本次运行时长。</summary>
    internal static double CurrentCharacterSessionSeconds
        => Player.Available ? SessionSeconds((ulong)Player.CID) : 0;

    internal static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
