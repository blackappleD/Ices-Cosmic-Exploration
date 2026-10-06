using ECommons.EzIpcManager;

namespace ICE.IPC
{
    public class AutoRetainerIPC
    {
        public const string Name = "AutoRetainer";
        public const string NameCN = "AutoRetainCN";
        public const string Repo = "https://love.puni.sh/ment.json";

        public AutoRetainerIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);

        public bool Installed => Utils.HasPlugin(Name) || Utils.HasPlugin(NameCN);

        /// <summary>
        /// 检查是否有雇员需要续约
        /// </summary>
        [EzIPC("IPC.AreAnyRetainersAvailableForCurrentChara")]
        public Func<bool> AreAnyRetainersAvailable;

        /// <summary>
        /// 获取当前角色可用的雇员数量
        /// </summary>
        [EzIPC("IPC.GetAvailableRetainerCount")]
        public Func<int> GetAvailableRetainerCount;

        /// <summary>
        /// 执行雇员续约任务
        /// </summary>
        [EzIPC("IPC.ProcessRetainers")]
        public Action ProcessRetainers;

        /// <summary>
        /// 检查 AutoRetainer 是否正在工作
        /// </summary>
        [EzIPC("IPC.IsBusy")]
        public Func<bool> IsBusy;

        /// <summary>
        /// 停止 AutoRetainer 当前任务
        /// </summary>
        [EzIPC("IPC.Abort")]
        public Action Abort;
    }
}
