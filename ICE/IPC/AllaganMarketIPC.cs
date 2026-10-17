using ECommons.EzIpcManager;

#nullable disable
namespace ICE.IPC
{
    /// <summary>
    /// Allagan Market 国服维护版（blackappleD/AllaganMarket-CN，InternalName "AllaganMarket-bld"）的自动压价 IPC，
    /// 注册在 "AllaganMarket.AutoUndercut.*" 下；国际服原版没有这些 IPC。
    /// Start 只在雇员列表（RetainerList）已打开时有效，打开传唤铃需要调用方自己完成。
    /// </summary>
    public class AllaganMarketIPC
    {
        public const string Name = "AllaganMarket-bld";
        public const string RepoCN = "https://raw.githubusercontent.com/blackappleD/DalamudPlugins/main/repo.json";

        public AllaganMarketIPC() => EzIPC.Init(this, "AllaganMarket.AutoUndercut", SafeWrapper.AnyException);

        public bool Installed => Utils.HasPlugin(Name);

        /// <summary>对勾选了「自动压价」的雇员执行压价；返回 false 表示没有启动（其他自动化在运行 / 没有勾选的雇员）。</summary>
        [EzIPC] public Func<bool> Start;
        [EzIPC] public Action Cancel;
        [EzIPC] public Func<bool> IsRunning;
        /// <summary>压价、批量上架/撤回、模特补货任一在运行。</summary>
        [EzIPC] public Func<bool> IsBusy;
        /// <summary>当前角色名下勾选了「自动压价」的雇员数量。</summary>
        [EzIPC] public Func<int> EnabledRetainerCount;
    }
}
