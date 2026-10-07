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
        [EzIPC] public Action AbortAllTasks;

        // 军票上缴：注册在 "AutoRetainer.GC.*" 下（不是 PluginState），需要人已站在军队补给处
        [EzIPC("AutoRetainer.GC.EnqueueInitiation", false)] public Action EnqueueGCInitiation;
    }
}
