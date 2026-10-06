using ECommons.EzIpcManager;

#nullable disable
namespace ICE.IPC
{
    /// <summary>
    /// AutoRetainer（国际服 / AutoRetainer-CN 国服版）的 IPC。
    /// 两个版本的 InternalName 都是 "AutoRetainer"，IPC 均注册在 "AutoRetainer.PluginState.*" 下。
    /// </summary>
    public class AutoRetainerIPC
    {
        public const string Name = "AutoRetainer";
        public const string Repo = "https://love.puni.sh/ment.json";
        public const string RepoCN = "https://raw.githubusercontent.com/endfish/DalamudPlugins/main/repo.json";

        public AutoRetainerIPC() => EzIPC.Init(this, $"{Name}.PluginState", SafeWrapper.AnyException);

        public bool Installed => Utils.HasPlugin(Name);

        [EzIPC] public Func<bool> IsBusy;
        [EzIPC] public Func<bool> AreAnyRetainersAvailableForCurrentChara;
        [EzIPC] public Func<bool> GetMultiModeStatus;
        [EzIPC] public Action AbortAllTasks;
        [EzIPC] public Action DisableAllFunctions;

        /// <summary>
        /// 只对当前角色执行一轮多角色模式（前往雇员铃 → 收取/派遣探险），完成后自动关闭多角色模式。
        /// 参数为 AutoRetainer 的 MultiModeType?（Retainers=0, Submersibles=1, Everything=2），Dalamud IPC 会经 JSON 转换枚举。
        /// </summary>
        [EzIPC] public Action<int?> EnableSingleMultiMode;

        public const int MultiMode_Retainers = 0;
    }
}
