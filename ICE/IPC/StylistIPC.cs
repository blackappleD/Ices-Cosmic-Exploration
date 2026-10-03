using ECommons.EzIpcManager;

#nullable disable
namespace ICE.IPC
{
    public class StylistIPC
    {
        public const string Name = "Stylist";
        public StylistIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
        public bool Installed => Utils.HasPlugin(Name);

        /// <summary>
        /// 更新当前套装为最优装备。shouldEquip = true 时无论套装是否有变化都会重新穿戴。
        /// 参数：moveItemsFromInventory（null = 沿用 Stylist 设置）, shouldEquip
        /// </summary>
        [EzIPC] public Action<bool?, bool?> UpdateCurrentGearsetEx;
        [EzIPC] public Func<bool> IsBusy;
    }
}
