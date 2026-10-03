using ECommons.EzIpcManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Generic;

#nullable disable
namespace ICE.IPC
{
    public class GearsetterIPC
    {
        public const string Name = "Gearsetter";
        public const string Repo = "https://puni.sh/api/repository/vera";
        public GearsetterIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
        public bool Installed => Utils.HasPlugin(Name);

        /// <summary>
        /// 返回指定套装的推荐装备：物品 ID、所在背包/兵装库及格子、目标装备栏位。
        /// SourceInventory 为 null 表示已穿戴或找不到。
        /// </summary>
        [EzIPC] public Func<byte, List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)>> GetRecommendationsForGearset;
    }
}
