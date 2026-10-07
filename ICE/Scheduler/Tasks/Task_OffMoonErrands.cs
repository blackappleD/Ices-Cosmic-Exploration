using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices.Sheets;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GrandCompany = ECommons.ExcelServices.GrandCompany;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 接取任务前需要离开星球办的事：存入时装衣柜 / 幻化衣柜（Glamour Log）、军票上缴（AutoRetainer）。
    /// 流程：去本国主城 → 旅馆房间存衣柜 → 军队补给处上缴 → 传送到最佳威兔洞 → 以太之光选择出发时的星球 → 交回 ICE。
    /// 任一步失败都会直接跳到返回星球，避免把角色留在城里。
    /// </summary>
    internal static unsafe class Task_OffMoonErrands
    {
        private const uint BestwayBurrowAetheryte = 175;
        private const string MoonTravelSheet = "transport/AetheryteBestwaysBurrow";

        private const uint DresserEventId = 721347;
        private const uint ArmoireEventId = 720978;
        // 幻化衣柜需要完成任务「华丽的投影世界」后才能使用
        private const uint DresserUnlockQuest = 68553;
        private const byte MinGcRankForExpertDelivery = 6;
        // 利姆萨：军队补给处在上层甲板，以太之光在下层甲板，需要以太网到「冒险者行会前」
        private const uint LimsaAftcastleAethernet = 41;

        private const long TravelTimeoutMs = 3 * 60 * 1000;
        private const long InnTimeoutMs = 2 * 60 * 1000;
        private const long StoreTimeoutMs = 5 * 60 * 1000;
        private const long GcTimeoutMs = 10 * 60 * 1000;
        private const long GcStartTimeoutMs = 30_000;
        private const long IdleConfirmMs = 2_000;
        // AutoRetainer 在「上缴完成」与「用军票购物」两个阶段之间可能短暂空闲
        private const long GcIdleConfirmMs = 5_000;
        private const long CloseTimeoutMs = 15_000;
        private const long CleanupTimeoutMs = 20_000;

        private static readonly string[] ErrandAddons =
        [
            "GrandCompanySupplyReward", "GrandCompanySupplyList",
            "Cabinet", "MiragePrismPrismBoxCrystallize", "MiragePrismPrismBox", "MiragePrismMiragePlate",
            "SelectIconString", "SelectString", "SelectYesno",
        ];

        private static uint _originMoon;
        private static bool _doArmoire;
        private static bool _doDresser;
        private static bool _doGc;
        private static bool _returning;
        // Fail() 已清空队列并安排返回，当前步骤需立即结束
        private static bool _aborted;

        private static long _stepStartedAt;
        private static long _idleSince;
        private static bool _started;
        private static long _closeStartedAt;

        // 军票上缴后剩余格数：之后要比它更少才再次触发
        private static int _gcFreeSlotsAfterRun = -1;
        private static int _gcFreeSlotsBeforeRun = -1;
        // 上缴后空间没有增加（没有可交物品、AR 未开启该角色的军票上缴等），暂停到剩余格数回到阈值以上
        private static bool _gcNoProgress;
        // 本次运行中存不进去的物品（如 Glamour Log 跳过的套装中物品、棱镜不足），避免反复出行
        private static readonly HashSet<uint> _glamourSkipItems = new();

        private static HashSet<uint>? _cabinetItemIds;
        private static HashSet<uint> CabinetItemIds
            => _cabinetItemIds ??= Svc.Data.GetExcelSheet<CabinetSheet>().Select(c => c.Item.RowId).Where(id => id != 0).ToHashSet();

        private static ulong Cid => (ulong)Player.CID;
        private static bool ArmoireEnabled => Player.Available && C.Resolve(Cid, ov => ov.AutoArmoire, c => c.AutoArmoire);
        private static bool DresserEnabled => Player.Available && C.Resolve(Cid, ov => ov.AutoGlamourDresser, c => c.AutoGlamourDresser);
        private static bool GcEnabled => Player.Available && C.Resolve(Cid, ov => ov.AutoGCTurnin, c => c.AutoGCTurnin);
        private static int GcSlotsLeft => C.Resolve(Cid, ov => ov.GCTurnin_SlotsLeft, c => c.GCTurnin_SlotsLeft);
        private static bool GcUseTicket => C.Resolve(Cid, ov => ov.GCTurnin_UseTicket, c => c.GCTurnin_UseTicket);

        private static uint Territory => Svc.ClientState.TerritoryType;
        private static GrandCompany Gc => (GrandCompany)PlayerState.Instance()->GrandCompany;

        private static bool InTransit
            => Player.IsCasting
               || Svc.Condition[ConditionFlag.BetweenAreas]
               || Svc.Condition[ConditionFlag.BetweenAreas51]
               || !PlayerHelper.IsScreenReady();

        private static bool InInnRoom
            => Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(Territory, out var row) && row.TerritoryIntendedUse.RowId == 2;

        // -------------------------------------------------------------------------
        // Trigger
        // -------------------------------------------------------------------------

        /// <summary>
        /// 在接取任务前调用：没有进行中的任务、人在星球上，且至少有一项需要处理。
        /// </summary>
        public static bool ShouldRun()
        {
            if (!Player.Available || CosmicHelper.CurrentLunarMission != 0)
                return false;
            if (!CosmicMoonRegistry.ByTerritoryId.ContainsKey(Territory))
                return false;
            if (Gc == GrandCompany.Unemployed)
                return false;

            bool armoire = NeedsArmoire();
            bool dresser = NeedsDresser();
            bool gc = NeedsGcTurnin();
            if (!armoire && !dresser && !gc)
                return false;

            IceLogging.Info($"Leaving the moon before grabbing a mission | Armoire: {armoire} | Glamour Dresser: {dresser} | GC Turn-in: {gc}", "[Off-Moon Errands: Check]");
            return true;
        }

        private static bool NeedsArmoire()
        {
            if (!ArmoireEnabled || !GlamourLogReady("AutoArmoire"))
                return false;
            return ArmoireCandidates().Any() && CanReachGcCity();
        }

        private static bool NeedsDresser()
        {
            if (!DresserEnabled || !GlamourLogReady("AutoGlamourDresser"))
                return false;
            if (!QuestManager.IsQuestComplete(DresserUnlockQuest))
            {
                if (EzThrottler.Throttle("Errands_DresserLocked", 10 * 60 * 1000))
                    IceLogging.Warning("Auto Glamour Dresser is enabled but the glamour dresser is not unlocked on this character", "[Off-Moon Errands: Check]");
                return false;
            }
            return DresserCandidates().Any() && CanReachGcCity();
        }

        private static bool NeedsGcTurnin()
        {
            if (!GcEnabled)
                return false;
            if (!P.AutoRetainer.Installed)
            {
                if (EzThrottler.Throttle("Errands_ARNotInstalled", 60_000))
                    IceLogging.Warning("GC Turn-in is enabled but AutoRetainer is not installed", "[Off-Moon Errands: Check]");
                return false;
            }
            if (PlayerState.Instance()->GetGrandCompanyRank() < MinGcRankForExpertDelivery)
            {
                if (EzThrottler.Throttle("Errands_GcRank", 10 * 60 * 1000))
                    IceLogging.Warning("GC Turn-in requires Grand Company rank 6 or higher (Expert Delivery)", "[Off-Moon Errands: Check]");
                return false;
            }

            int free = (int)InventoryManager.Instance()->GetEmptySlotsInBag();
            if (free > GcSlotsLeft)
            {
                _gcFreeSlotsAfterRun = -1;
                _gcNoProgress = false;
                return false;
            }
            if (_gcNoProgress)
                return false;
            if (_gcFreeSlotsAfterRun >= 0 && free >= _gcFreeSlotsAfterRun)
                return false;

            return CanReachGcCity();
        }

        /// <summary>
        /// 利姆萨不用传送券时需要 Lifestream 走以太网，没有就不出发，免得白跑一趟。
        /// </summary>
        private static bool CanReachGcCity()
        {
            if (Gc != GrandCompany.Maelstrom || P.Lifestream.Installed)
                return true;
            if (GcUseTicket && InventoryManager.Instance()->GetInventoryItemCount(GcTicketItemId) > 0)
                return true;

            if (EzThrottler.Throttle("Errands_NoLifestream", 10 * 60 * 1000))
                IceLogging.Warning("Limsa Lominsa needs Lifestream (aethernet to the Upper Decks) or a Maelstrom aetheryte ticket with \"Use GC Aetheryte Ticket\" enabled", "[Off-Moon Errands: Check]");
            return false;
        }

        private static bool GlamourLogReady(string throttleKey)
        {
            if (P.GlamourLog.Installed)
                return true;
            if (EzThrottler.Throttle($"Errands_{throttleKey}_NotInstalled", 60_000))
                IceLogging.Warning("Glamour storage is enabled but Glamour Log is not installed", "[Off-Moon Errands: Check]");
            return false;
        }

        private static List<uint> BagItemIds()
        {
            var ids = new List<uint>();
            var inventory = InventoryManager.Instance();
            foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null)
                    continue;
                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId != 0)
                        ids.Add(slot->ItemId);
                }
            }
            return ids;
        }

        private static List<uint> ArmoireCandidates()
            => BagItemIds().Distinct()
                .Where(id => CabinetItemIds.Contains(id) && !_glamourSkipItems.Contains(id) && !P.GlamourLog.IsItemInArmoire(id))
                .ToList();

        private static List<uint> DresserCandidates()
        {
            var setLookup = Svc.Data.GetExcelSheet<MirageStoreSetItemLookup>();
            return BagItemIds().Distinct()
                .Where(id => setLookup.HasRow(id) && !_glamourSkipItems.Contains(id) && !P.GlamourLog.IsItemOwned(id))
                .ToList();
        }

        // -------------------------------------------------------------------------
        // Queue
        // -------------------------------------------------------------------------

        public static void Enqueue()
        {
            _originMoon = Territory;
            _doArmoire = NeedsArmoire();
            _doDresser = NeedsDresser();
            _doGc = NeedsGcTurnin();
            _gcFreeSlotsBeforeRun = (int)InventoryManager.Instance()->GetEmptySlotsInBag();
            _returning = false;
            _aborted = false;
            ResetStep();

            if (!CosmicMoonRegistry.ByTerritoryId.ContainsKey(_originMoon) || (!_doArmoire && !_doDresser && !_doGc))
            {
                SchedulerMain.State = IceState.GrabMission;
                return;
            }

            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();

            P.TaskManager.EnqueueMulti
                (
                    new(Timed(TravelToGcCity, TravelTimeoutMs, "Travel to the Grand Company city"), "Errands: Travel to the Grand Company city"),
                    new(Timed(EnterInn, InnTimeoutMs, "Enter the inn room"), "Errands: Entering the inn room"),
                    new(Timed(StoreArmoire, StoreTimeoutMs, "Store items in the armoire"), "Errands: Storing items in the armoire"),
                    new(Timed(StoreDresser, StoreTimeoutMs, "Store items in the glamour dresser"), "Errands: Storing items in the glamour dresser"),
                    new(Timed(LeaveInnForGc, InnTimeoutMs, "Leave the inn room"), "Errands: Leaving the inn room"),
                    new(Timed(MoveToGcSupply, TravelTimeoutMs, "Move to the Grand Company supply officer"), "Errands: Moving to the GC supply officer"),
                    new(Timed(RunGcTurnin, GcTimeoutMs + GcStartTimeoutMs, "GC turn-in"), "Errands: AutoRetainer GC turn-in")
                );
            EnqueueReturn();
        }

        private static void EnqueueReturn()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(() => { _returning = true; ResetStep(); return true; }, "Errands: Start returning to the moon"),
                    new(CleanupBeforeReturn, "Errands: Closing windows before returning"),
                    new(() => { MarkAttempted(); return true; }, "Errands: Recording the results"),
                    new(Timed(TeleportToBestwayBurrow, TravelTimeoutMs, "Teleport to Bestway Burrow"), "Errands: Teleporting to Bestway Burrow"),
                    new(Timed(TravelToMoon, TravelTimeoutMs, "Travel back to the moon"), "Errands: Travelling back to the moon"),
                    new(Finish, "Errands: Handing control back to ICE")
                );
        }

        /// <summary>
        /// 无论成功或中途失败，都记录这次出行的结果，避免下次接任务前因同样的原因再次出行。
        /// </summary>
        private static void MarkAttempted()
        {
            string tag = "[Off-Moon Errands]";

            if (_doGc)
            {
                _gcFreeSlotsAfterRun = (int)InventoryManager.Instance()->GetEmptySlotsInBag();
                _gcNoProgress = _gcFreeSlotsAfterRun <= _gcFreeSlotsBeforeRun;
                if (_gcNoProgress)
                    IceLogging.Warning($"GC turn-in freed no inventory space ({_gcFreeSlotsAfterRun} free). Paused until free slots go above the threshold again. Check that GC delivery is enabled for this character in AutoRetainer.", tag);
                else
                    IceLogging.Info($"{_gcFreeSlotsAfterRun} free inventory slot(s) after the GC turn-in", tag);
            }

            if (!P.GlamourLog.Installed)
                return;

            var left = new HashSet<uint>();
            if (_doArmoire)
                left.UnionWith(ArmoireCandidates());
            if (_doDresser)
                left.UnionWith(DresserCandidates());
            if (left.Count == 0)
                return;

            _glamourSkipItems.UnionWith(left);
            IceLogging.Warning($"{left.Count} item(s) could not be stored by Glamour Log, they will be ignored until ICE is reloaded", tag);
        }

        private static Func<bool?> Timed(Func<bool?> step, long timeoutMs, string what) => () =>
        {
            if (_stepStartedAt == 0)
                _stepStartedAt = Environment.TickCount64;

            var result = step();
            if (_aborted)
            {
                _aborted = false;
                ResetStep();
                return true;
            }
            if (result != false)
            {
                ResetStep();
                return result;
            }

            if (Environment.TickCount64 - _stepStartedAt > timeoutMs)
            {
                Fail($"{what} timed out");
                _aborted = false;
                return true;
            }

            return false;
        };

        private static void ResetStep()
        {
            _stepStartedAt = 0;
            _idleSince = 0;
            _started = false;
            _closeStartedAt = 0;
        }

        private static bool? Fail(string reason)
        {
            ResetStep();
            _aborted = true;
            StopHelpers();
            P.TaskManager.Tasks.Clear();
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();

            if (_returning)
            {
                IceLogging.Error($"{reason}. Could not get back to the moon, stopping ICE", "[Off-Moon Errands]");
                SchedulerMain.DisablePlugin();
                return true;
            }

            IceLogging.Error($"{reason}. Skipping the remaining errands and returning to the moon", "[Off-Moon Errands]");
            EnqueueReturn();
            return true;
        }

        private static void StopHelpers()
        {
            if (P.AutoRetainer.Installed && P.AutoRetainer.IsBusy())
            {
                IceLogging.Info("Stopping AutoRetainer", "[Off-Moon Errands]");
                P.AutoRetainer.AbortAllTasks();
            }
            if (P.GlamourLog.Installed && P.GlamourLog.IsBusy())
            {
                IceLogging.Info("Stopping Glamour Log", "[Off-Moon Errands]");
                Svc.Commands.ProcessCommand("/glamourlog stop");
            }
        }

        /// <summary>
        /// 返回前停掉外部插件的自动化并关闭所有相关界面，否则一直处于 occupied 状态无法传送。
        /// </summary>
        private static bool? CleanupBeforeReturn()
        {
            if (_closeStartedAt == 0)
                _closeStartedAt = Environment.TickCount64;

            if (Environment.TickCount64 - _closeStartedAt > CleanupTimeoutMs)
            {
                IceLogging.Warning("Could not close every window, trying to return to the moon anyway", "[Off-Moon Errands: Return]");
                _closeStartedAt = 0;
                return true;
            }

            if ((P.AutoRetainer.Installed && P.AutoRetainer.IsBusy()) || (P.GlamourLog.Installed && P.GlamourLog.IsBusy()))
            {
                if (EzThrottler.Throttle("Errands_StopHelpers", 2000))
                    StopHelpers();
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_CleanupTalk", 200))
                    talk.Click();
                return false;
            }

            if (!InTransit && TryCloseOne(ErrandAddons))
                return false;

            if (InTransit || GenericHelpers.IsOccupied())
                return false;

            _closeStartedAt = 0;
            return true;
        }

        private static bool? Finish()
        {
            IceLogging.Info("Back on the moon, handing control back to ICE", "[Off-Moon Errands: Finish]");
            _returning = false;
            SchedulerMain.State = IceState.GrabMission;
            return true;
        }

        // -------------------------------------------------------------------------
        // Grand Company city / inn
        // -------------------------------------------------------------------------

        private static uint GcTerritory => Gc switch
        {
            GrandCompany.Maelstrom => 128u,
            GrandCompany.TwinAdder => 132u,
            _ => 130u,
        };

        private static uint GcAetheryte => Gc switch
        {
            GrandCompany.Maelstrom => 8u,
            GrandCompany.TwinAdder => 2u,
            _ => 9u,
        };

        private static uint GcTicketItemId => Gc switch
        {
            GrandCompany.Maelstrom => 21069u,
            GrandCompany.TwinAdder => 21070u,
            _ => 21071u,
        };

        private static Vector3 GcSupplyLocation => Gc switch
        {
            GrandCompany.Maelstrom => new(94.02f, 40.28f, 74.48f),
            GrandCompany.TwinAdder => new(-68.68f, -0.50f, -8.47f),
            _ => new(-142.83f, 4.10f, -106.31f),
        };

        private static uint InnTerritory => Gc switch
        {
            GrandCompany.Maelstrom => 177u,
            GrandCompany.TwinAdder => 179u,
            _ => 178u,
        };

        private static uint InnExitDoorDataId => Gc switch
        {
            GrandCompany.Maelstrom => 2001010u,
            GrandCompany.TwinAdder => 2000087u,
            _ => 2001011u,
        };

        private static Vector3 InnkeeperLocation => Gc switch
        {
            GrandCompany.Maelstrom => new(15.43f, 40.00f, 12.47f),
            GrandCompany.TwinAdder => new(25.66f, -8.00f, 99.74f),
            _ => new(28.86f, 7.00f, -80.13f),
        };

        private static uint InnkeeperDataId => Gc switch
        {
            GrandCompany.Maelstrom => 1000974u,
            GrandCompany.TwinAdder => 1000102u,
            _ => 1001976u,
        };

        private static bool? TravelToGcCity()
        {
            string tag = "[Off-Moon Errands: Travel]";

            if (Territory == GcTerritory)
                return !InTransit;

            if (InTransit || GenericHelpers.IsOccupied() || Player.IsAnimationLocked)
                return false;

            if (GcUseTicket && InventoryManager.Instance()->GetInventoryItemCount(GcTicketItemId) > 0)
            {
                if (EzThrottler.Throttle("Errands_Teleport", 8000))
                {
                    IceLogging.Info("Using the Grand Company aetheryte ticket", tag);
                    AgentInventoryContext.Instance()->UseItem(GcTicketItemId);
                }
                return false;
            }

            // 利姆萨：以太之光在下层甲板，补给处在上层甲板
            if (Gc == GrandCompany.Maelstrom && Territory == Svc.Data.GetExcelSheet<Aetheryte>().GetRow(GcAetheryte).Territory.RowId)
            {
                if (!P.Lifestream.Installed)
                    return Fail("Lifestream is required to use the Limsa Lominsa aethernet (or enable the Grand Company aetheryte ticket)");
                if (P.Lifestream.IsBusy())
                    return false;
                if (EzThrottler.Throttle("Errands_Teleport", 8000))
                {
                    IceLogging.Info("Using the aethernet to get to the Upper Decks", tag);
                    P.Lifestream.AethernetTeleportById(LimsaAftcastleAethernet);
                }
                return false;
            }

            Teleport(GcAetheryte, tag);
            return false;
        }

        private static bool? EnterInn()
        {
            string tag = "[Off-Moon Errands: Inn]";

            if (!_doArmoire && !_doDresser)
                return true;

            if (InInnRoom)
                return !InTransit && Player.Interactable;

            if (InTransit)
                return false;

            if (Territory != GcTerritory)
            {
                TravelToGcCity();
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_InnYesno", 500))
                    yesno.Yes();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var select) && select.IsAddonReady)
            {
                if (select.Entries.Length > 0 && EzThrottler.Throttle("Errands_InnSelect", 500))
                    select.Entries[0].Select();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_InnTalk", 200))
                    talk.Click();
                return false;
            }

            if (!Utils.TryGetObjectByDataId(InnkeeperDataId, out var innkeeper) || innkeeper == null || Player.DistanceTo(innkeeper.Position) > 6f)
            {
                MoveNear(InnkeeperLocation, 5f);
                return false;
            }

            if (MoveNear(innkeeper.Position, 4f) && !GenericHelpers.IsOccupied() && EzThrottler.Throttle("Errands_InnInteract", 3000))
            {
                IceLogging.Info("Talking to the innkeeper", tag);
                Svc.Targets.Target = innkeeper;
                Utils.InteractWithObject(innkeeper);
            }
            return false;
        }

        private static bool? LeaveInnForGc()
        {
            if (!_doGc || !InInnRoom)
                return true;

            if (InTransit)
                return false;

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_DoorYesno", 500))
                    yesno.Yes();
                return false;
            }

            if (!Utils.TryGetObjectByDataId(InnExitDoorDataId, out var door) || door == null)
            {
                // 不是本国旅馆（或找不到门），直接传送过去
                TravelToGcCity();
                return false;
            }

            if (MoveNear(door.Position, 2.5f) && !GenericHelpers.IsOccupied() && EzThrottler.Throttle("Errands_DoorInteract", 3000))
            {
                IceLogging.Info("Leaving the inn room", "[Off-Moon Errands: Inn]");
                Svc.Targets.Target = door;
                Utils.InteractWithObject(door);
            }
            return false;
        }

        // -------------------------------------------------------------------------
        // Armoire / Glamour Dresser (Glamour Log)
        // -------------------------------------------------------------------------

        private static bool CabinetReady
            => GenericHelpers.TryGetAddonByName<AtkUnitBase>("Cabinet", out var addon) && GenericHelpers.IsAddonReady(addon)
               && UIState.Instance()->Cabinet.IsCabinetLoaded();

        private static bool DresserReady
            => GenericHelpers.TryGetAddonByName<AtkUnitBase>("MiragePrismPrismBox", out var box) && GenericHelpers.IsAddonReady(box)
               && GenericHelpers.TryGetAddonByName<AtkUnitBase>("MiragePrismPrismBoxCrystallize", out var crystallize) && GenericHelpers.IsAddonReady(crystallize)
               && MirageManager.Instance()->PrismBoxLoaded;

        private static bool? StoreArmoire()
        {
            if (!_doArmoire)
                return true;

            return StoreWithGlamourLog("Armoire", ArmoireEventId, () => CabinetReady,
                ["Cabinet", "SelectString", "SelectYesno"]);
        }

        private static bool? StoreDresser()
        {
            if (!_doDresser)
                return true;

            return StoreWithGlamourLog("Glamour Dresser", DresserEventId, () => DresserReady,
                ["MiragePrismPrismBoxCrystallize", "MiragePrismPrismBox", "MiragePrismMiragePlate", "SelectString", "SelectYesno"]);
        }

        /// <summary>
        /// 走到衣柜旁打开界面 → Glamour Log EntrustAll → 等待其空闲 → 关闭界面。
        /// </summary>
        private static bool? StoreWithGlamourLog(string name, uint eventId, Func<bool> uiReady, string[] addonsToClose)
        {
            string tag = $"[Off-Moon Errands: {name}]";

            if (_closeStartedAt != 0)
                return CloseAddons(addonsToClose);

            if (_started)
            {
                if (P.GlamourLog.IsBusy())
                {
                    _idleSince = 0;
                    return false;
                }
                if (_idleSince == 0)
                    _idleSince = Environment.TickCount64;
                if (Environment.TickCount64 - _idleSince < IdleConfirmMs)
                    return false;

                IceLogging.Info($"Glamour Log finished storing into the {name}", tag);
                _closeStartedAt = Environment.TickCount64;
                return false;
            }

            if (!InInnRoom)
                return Fail($"Not in an inn room, can not use the {name}");

            // Glamour Log 会遍历所有可存物品（不依赖界面当前分类），没有可存的会立即结束
            if (uiReady())
            {
                if (EzThrottler.Throttle("Errands_EntrustAll", 1000))
                {
                    IceLogging.Info($"Asking Glamour Log to store everything into the {name}", tag);
                    P.GlamourLog.EntrustAll();
                    _started = true;
                    _idleSince = 0;
                }
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_GlamourYesno", 500))
                    yesno.Yes();
                return false;
            }
            // 时装衣柜交互后先弹出选项（第一项为打开时装衣柜）
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var select) && select.IsAddonReady)
            {
                if (select.Entries.Length > 0 && EzThrottler.Throttle("Errands_GlamourSelect", 500))
                    select.Entries[0].Select();
                return false;
            }

            var target = FindByEventId(eventId);
            if (target == null)
                return Fail($"Could not find the {name} in the inn room");

            if (MoveNear(target.Position, 2.5f) && !GenericHelpers.IsOccupied() && EzThrottler.Throttle("Errands_GlamourInteract", 3000))
            {
                IceLogging.Info($"Opening the {name}", tag);
                Svc.Targets.Target = target;
                Utils.InteractWithObject(target);
            }
            return false;
        }

        private static IGameObject? FindByEventId(uint eventId)
            => Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(o =>
            {
                var handler = o.Struct()->EventHandler;
                return handler != null && handler->Info.EventId.Id == eventId;
            });

        /// <summary>关闭列表中第一个打开着的界面，返回是否还有界面开着。</summary>
        private static bool TryCloseOne(string[] addons)
        {
            foreach (var name in addons)
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && GenericHelpers.IsAddonReady(addon))
                {
                    if (EzThrottler.Throttle($"Errands_Close_{name}", 500))
                        GenericHandlers.FireCallback(name, true, -1);
                    return true;
                }
            }
            return false;
        }

        private static bool? CloseAddons(string[] addons)
        {
            bool anyOpen = TryCloseOne(addons);

            // 超时也继续：剩下的界面会在返回前统一再关一次
            if ((!anyOpen && !GenericHelpers.IsOccupied()) || Environment.TickCount64 - _closeStartedAt > CloseTimeoutMs)
            {
                _closeStartedAt = 0;
                return true;
            }
            return false;
        }

        // -------------------------------------------------------------------------
        // GC turn-in (AutoRetainer)
        // -------------------------------------------------------------------------

        private static bool? MoveToGcSupply()
        {
            if (!_doGc)
                return true;

            if (Territory != GcTerritory)
            {
                TravelToGcCity();
                return false;
            }

            if (InTransit)
                return false;

            return MoveNear(GcSupplyLocation, 4f);
        }

        private static bool? RunGcTurnin()
        {
            string tag = "[Off-Moon Errands: GC Turn-in]";

            if (!_doGc)
                return true;

            if (_closeStartedAt != 0)
                return CloseAddons(["GrandCompanySupplyReward", "SelectYesno", "SelectString", "GrandCompanySupplyList"]);

            if (!_started)
            {
                IceLogging.Info("Starting AutoRetainer's GC turn-in", tag);
                P.AutoRetainer.EnqueueGCInitiation();
                _started = true;
                _idleSince = 0;
                _stepStartedAt = Environment.TickCount64;
                return false;
            }

            bool busy = P.AutoRetainer.IsBusy();
            long elapsed = Environment.TickCount64 - _stepStartedAt;

            if (busy)
            {
                // 用 _idleSince = -1 标记「已经开始工作过」
                _idleSince = -1;
                if (elapsed > GcTimeoutMs)
                {
                    IceLogging.Error("AutoRetainer GC turn-in did not finish in time, stopping it", tag);
                    P.AutoRetainer.AbortAllTasks();
                    _closeStartedAt = Environment.TickCount64;
                }
                return false;
            }

            if (_idleSince == 0)
            {
                if (elapsed > GcStartTimeoutMs)
                {
                    IceLogging.Warning("AutoRetainer did not start the GC turn-in, continuing", tag);
                    _closeStartedAt = Environment.TickCount64;
                }
                return false;
            }

            if (_idleSince < 0)
                _idleSince = Environment.TickCount64;
            else if (Environment.TickCount64 - _idleSince > GcIdleConfirmMs)
            {
                IceLogging.Info("AutoRetainer finished the GC turn-in", tag);
                _closeStartedAt = Environment.TickCount64;
            }

            return false;
        }

        // -------------------------------------------------------------------------
        // Return to the moon
        // -------------------------------------------------------------------------

        private static uint BestwayBurrowTerritory
            => Svc.Data.GetExcelSheet<Aetheryte>().GetRow(BestwayBurrowAetheryte).Territory.RowId;

        private static IGameObject? BestwayAetheryte
            => Svc.Objects.FirstOrDefault(o => o.ObjectKind == ObjectKind.Aetheryte && o.BaseId == BestwayBurrowAetheryte);

        private static bool? TeleportToBestwayBurrow()
        {
            if (Territory == _originMoon)
                return true;

            if (Territory == BestwayBurrowTerritory)
                return !InTransit;

            if (InTransit || GenericHelpers.IsOccupied() || Player.IsAnimationLocked)
                return false;

            Teleport(BestwayBurrowAetheryte, "[Off-Moon Errands: Return]");
            return false;
        }

        private static bool? TravelToMoon()
        {
            string tag = "[Off-Moon Errands: Return]";

            if (Territory == _originMoon)
                return !InTransit && Player.Interactable;

            if (InTransit)
                return false;

            if (Territory != BestwayBurrowTerritory)
            {
                TeleportToBestwayBurrow();
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var select) && select.IsAddonReady)
            {
                var options = MoonOptionTexts();
                var entries = select.Entries;
                int index = Array.FindIndex(entries, e => options.Any(o => e.Text.Trim().Contains(o, StringComparison.OrdinalIgnoreCase)));
                if (index < 0)
                {
                    // 可能是别的菜单，关掉后重新交互，直到步骤超时
                    if (EzThrottler.Throttle("Errands_MoonOptionMissing", 3000))
                    {
                        IceLogging.Warning($"Could not find the travel option for {CosmicMoonRegistry.ByTerritoryId[_originMoon].DisplayName} ({string.Join(" / ", options)}) in [{string.Join(" | ", entries.Select(e => e.Text.Trim()))}], retrying", tag);
                        GenericHandlers.FireCallback("SelectString", true, -1);
                    }
                    return false;
                }

                if (EzThrottler.Throttle("Errands_MoonSelect", 1000))
                {
                    IceLogging.Info($"Travelling back to {CosmicMoonRegistry.ByTerritoryId[_originMoon].DisplayName}: {entries[index].Text.Trim()}", tag);
                    entries[index].Select();
                }
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_MoonYesno", 500))
                    yesno.Yes();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Errands_MoonTalk", 200))
                    talk.Click();
                return false;
            }

            var aetheryte = BestwayAetheryte;
            if (aetheryte == null)
                return false;

            if (MoveNear(aetheryte.Position, 7f) && !GenericHelpers.IsOccupied() && EzThrottler.Throttle("Errands_AetheryteInteract", 3000))
            {
                IceLogging.Info("Interacting with the Bestway Burrow aetheryte", tag);
                Svc.Targets.Target = aetheryte;
                Utils.InteractWithObject(aetheryte);
            }
            return false;
        }

        /// <summary>
        /// 以太之光菜单中前往该星球的选项文本：transport/AetheryteBestwaysBurrow 第 1~4 行依次为
        /// 憧憬湾 / 法恩娜 / 俄匊斯 / 奥克塞西亚（与 ExpeditionTabIndex + 1 一致），并以星球地名兜底。
        /// </summary>
        private static List<string> MoonOptionTexts()
        {
            var texts = new List<string>();
            var moon = CosmicMoonRegistry.ByTerritoryId[_originMoon];

            try
            {
                if (Svc.Data.GetExcelSheet<QuestDialogueText>(name: MoonTravelSheet).TryGetRow((uint)moon.ExpeditionTabIndex + 1, out var row))
                {
                    var text = row.Value.GetText().Trim();
                    if (text.Length > 0)
                        texts.Add(text);
                }
            }
            catch (Exception ex)
            {
                IceLogging.Error($"Could not read {MoonTravelSheet}: {ex.Message}", "[Off-Moon Errands: Return]");
            }

            if (Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(_originMoon, out var territory))
            {
                var placeName = territory.PlaceName.Value.Name.GetText().Trim();
                if (placeName.Length > 0)
                    texts.Add(placeName);
            }

            return texts;
        }

        // -------------------------------------------------------------------------
        // Movement helpers (outside the moon, no mount / cosmoliner handling)
        // -------------------------------------------------------------------------

        private static void Teleport(uint aetheryteId, string tag)
        {
            if (!EzThrottler.Throttle("Errands_Teleport", 8000))
                return;

            IceLogging.Info($"Teleporting to aetheryte {aetheryteId}", tag);
            Telepo.Instance()->Teleport(aetheryteId, 0);
        }

        private static bool MoveNear(Vector3 position, float distance)
        {
            if (Player.DistanceTo(position) <= distance)
            {
                if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
                return !Player.IsMoving;
            }

            if (!P.Navmesh.Installed || !P.Navmesh.IsReady() || P.Navmesh.PathfindInProgress() || P.Navmesh.IsRunning())
                return false;

            if (InTransit || GenericHelpers.IsOccupied())
                return false;

            if (EzThrottler.Throttle("Errands_Pathfind", 2000))
                P.Navmesh.PathfindAndMoveTo(position, false);

            return false;
        }
    }
}
