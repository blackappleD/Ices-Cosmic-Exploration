using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 参考 AutoDuty 的 AutoEquipHelper：通过 Gearsetter 的推荐结果逐件穿戴最优装备。
    /// </summary>
    internal static unsafe class Task_Gearsetter
    {
        private const string Tag = "[Gearsetter]";
        private const string Throttle = "GearsetterEquip";
        // Gearsetter 推荐两枚相同戒指时会返回同一个格子，需要穿完后重新获取推荐再穿另一枚
        private const int MaxPasses = 3;

        private static List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> Recommendations;
        private static int Index;
        private static int Pass;
        private static bool Skip;
        // 换下来的武器/副手会进兵装库，记录下来等装备成功后再挪进背包
        private static (InventoryType Container, uint ItemId, int AttemptsLeft)? PendingWeaponMove;

        private static readonly InventoryType[] BagInventories =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        public static void Enqueue()
        {
            P.TaskManager.Enqueue(() => Setup(), "Gearsetter: syncing current gearset");
            P.TaskManager.EnqueueDelay(500);
            P.TaskManager.Enqueue(() => EquipRecommended(), "Gearsetter: equipping recommended gear", new(timeLimitMS: 30000, abortOnTimeout: false));
            P.TaskManager.Enqueue(() => SaveGearset(), "Gearsetter: saving gearset");
        }

        private static bool? Setup()
        {
            // 等待换职完成，否则 CurrentGearsetIndex 可能还是上一个职业的套装
            if (Player.IsBusy)
                return false;

            Recommendations = null;
            Index = 0;
            Pass = 0;
            PendingWeaponMove = null;

            var gearsets = RaptureGearsetModule.Instance();
            var index = gearsets->CurrentGearsetIndex;
            Skip = !gearsets->IsValidGearset(index) || (Job)gearsets->GetGearset(index)->ClassJob != Player.Job;
            if (Skip)
            {
                IceLogging.Warning($"Current gearset {index} does not belong to {Player.Job}, skipping Gearsetter", Tag);
                return true;
            }

            // 先把身上的装备写入套装，Gearsetter 才会基于当前穿戴给出推荐
            gearsets->UpdateGearset(index);
            return true;
        }

        private static bool? EquipRecommended()
        {
            if (Skip)
                return true;

            if (!EzThrottler.Throttle(Throttle, 100))
                return false;

            if (PendingWeaponMove != null)
            {
                MovePendingWeapon();
                return false;
            }

            var gearsets = RaptureGearsetModule.Instance();

            if (Recommendations == null)
            {
                Recommendations = P.Gearsetter.GetRecommendationsForGearset((byte)gearsets->CurrentGearsetIndex);
                Index = 0;

                if (Recommendations == null || Recommendations.Count == 0)
                {
                    IceLogging.Info("Gearsetter has no more recommendations", Tag);
                    return true;
                }

                IceLogging.Info($"Gearsetter recommends {Recommendations.Count} item(s) (pass {Pass + 1})", Tag);
                return false;
            }

            if (Index >= Recommendations.Count)
            {
                Pass++;
                if (Pass >= MaxPasses)
                {
                    IceLogging.Warning("Reached max Gearsetter passes, some items may not be equipped", Tag);
                    return true;
                }

                // 同步套装后重新获取推荐，用来处理相同戒指或上一轮没穿上的装备
                gearsets->UpdateGearset(gearsets->CurrentGearsetIndex);
                Recommendations = null;
                EzThrottler.Throttle(Throttle, 500, true);
                return false;
            }

            var (itemId, sourceInventory, sourceSlot, targetSlot) = Recommendations[Index];
            var inventory = InventoryManager.Instance();
            var equipped = inventory->GetInventoryContainer(InventoryType.EquippedItems);

            if (equipped->Items[(int)targetSlot].ItemId == itemId)
            {
                IceLogging.Debug($"Equipped item {itemId} to {targetSlot}", Tag);
                Index++;
                return false;
            }

            if (sourceInventory == null || sourceSlot == null
                || inventory->GetInventoryContainer(sourceInventory.Value)->Items[(int)sourceSlot.Value].ItemId != itemId)
            {
                IceLogging.Debug($"Item {itemId} not found at {sourceInventory}/{sourceSlot}, retrying next pass", Tag);
                Index++;
                return false;
            }

            bool isWeapon = targetSlot is RaptureGearsetModule.GearsetItemIndex.MainHand or RaptureGearsetModule.GearsetItemIndex.OffHand;
            var oldItemId = equipped->Items[(int)targetSlot].ItemId;

            // 非武器栏位：先把旧装备挪进背包，再穿新装备，避免旧装备回到兵装库
            if (C.GearsetterOldToInventory && !isWeapon && oldItemId != 0)
            {
                var (bag, bagSlot) = GetFirstEmptyBagSlot();
                if (bagSlot < 0)
                {
                    IceLogging.Debug("Inventory is full, replaced item stays in armoury", Tag);
                }
                else
                {
                    IceLogging.Info($"Moving replaced item {oldItemId} from {targetSlot} to {bag} (slot {bagSlot})", Tag);
                    inventory->MoveItemSlot(InventoryType.EquippedItems, (ushort)targetSlot, bag, (ushort)bagSlot, true);
                    EzThrottler.Throttle(Throttle, 500, true);
                    return false;
                }
            }

            IceLogging.Info($"Equipping item {itemId} to {targetSlot} from {sourceInventory} (slot {sourceSlot})", Tag);
            inventory->MoveItemSlot(sourceInventory.Value, sourceSlot.Value, InventoryType.EquippedItems, (ushort)targetSlot, true);

            // 主手不能为空，无法提前挪走，换下来的武器/副手会进兵装库，之后再从兵装库挪进背包
            if (C.GearsetterOldToInventory && C.GearsetterOldWeaponsToInventory && isWeapon && oldItemId != 0 && oldItemId != itemId)
            {
                var armory = targetSlot == RaptureGearsetModule.GearsetItemIndex.MainHand ? InventoryType.ArmoryMainHand : InventoryType.ArmoryOffHand;
                PendingWeaponMove = (armory, oldItemId, 10);
            }
            // 等服务器确认装备变化后再检查
            EzThrottler.Throttle(Throttle, 500, true);
            return false;
        }

        private static void MovePendingWeapon()
        {
            var (container, oldItemId, attemptsLeft) = PendingWeaponMove!.Value;
            var cont = InventoryManager.Instance()->GetInventoryContainer(container);

            for (int i = 0; i < cont->Size; i++)
            {
                if (cont->Items[i].ItemId != oldItemId)
                    continue;

                PendingWeaponMove = null;
                var (bag, bagSlot) = GetFirstEmptyBagSlot();
                if (bagSlot < 0)
                {
                    IceLogging.Debug("Inventory is full, replaced weapon stays in armoury", Tag);
                }
                else
                {
                    IceLogging.Info($"Moving replaced weapon {oldItemId} from {container} (slot {i}) to {bag} (slot {bagSlot})", Tag);
                    InventoryManager.Instance()->MoveItemSlot(container, (ushort)i, bag, (ushort)bagSlot, true);
                    EzThrottler.Throttle(Throttle, 500, true);
                }
                return;
            }

            // 装备结果可能要过几帧才可见，多试几次
            PendingWeaponMove = --attemptsLeft > 0 ? (container, oldItemId, attemptsLeft) : null;
            if (PendingWeaponMove == null)
                IceLogging.Debug($"Replaced weapon {oldItemId} not found in {container}, giving up", Tag);
        }

        private static (InventoryType Inventory, short Slot) GetFirstEmptyBagSlot()
        {
            foreach (var type in BagInventories)
            {
                var cont = InventoryManager.Instance()->GetInventoryContainer(type);
                if (cont == null)
                    continue;

                for (short i = 0; i < cont->Size; i++)
                {
                    if (cont->Items[i].ItemId == 0)
                        return (type, i);
                }
            }

            return (InventoryType.Inventory1, -1);
        }

        private static bool? SaveGearset()
        {
            if (!Skip)
                RaptureGearsetModule.Instance()->UpdateGearset(RaptureGearsetModule.Instance()->CurrentGearsetIndex);

            Recommendations = null;
            PendingWeaponMove = null;
            return true;
        }
    }
}
