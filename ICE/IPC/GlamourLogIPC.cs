using ECommons.EzIpcManager;

#nullable disable
namespace ICE.IPC
{
    /// <summary>
    /// Glamour Log 的 IPC（国际服 / blackappleD 国服维护版 InternalName 均为 "GlamourLog"）。
    /// EntrustAll 只在幻化衣柜（MiragePrismPrismBox + MiragePrismPrismBoxCrystallize）或时装衣柜（Cabinet）界面已打开时生效，
    /// 打开界面需要调用方自己完成。
    /// </summary>
    public class GlamourLogIPC
    {
        public const string Name = "GlamourLog";
        public const string Repo = "https://puni.sh/api/repository/croizat";
        public const string RepoCN = "https://raw.githubusercontent.com/blackappleD/DalamudPlugins/main/repo.json";

        public GlamourLogIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);

        public bool Installed => Utils.HasPlugin(Name);

        [EzIPC] public Func<bool> EntrustAll;
        [EzIPC] public Func<bool> IsBusy;
        [EzIPC] public Func<uint, bool> IsItemOwned;
        [EzIPC] public Func<uint, bool> IsItemInArmoire;
    }
}
