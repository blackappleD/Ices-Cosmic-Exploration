using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.Scheduler.Handlers;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 接取任务前的自动雇员：宇宙返回 → 走到基地传唤铃 → 打开雇员列表 → 让 AutoRetainer 以普通模式处理 → 关闭列表交回 ICE。
    /// 不使用 AutoRetainer 的多角色模式，因此不要求在多角色模式中启用当前角色，只看 AutoRetainer 中勾选的雇员。
    /// </summary>
    internal static unsafe class Task_AutoRetainer
    {
        private const string CooldownKey = "AutoRetainer_Cooldown";
        // 一轮结束（无论成功与否）后的冷却，避免 AutoRetainer 无法处理时反复触发
        private const int CooldownMs = 5 * 60 * 1000;
        private const long OpenBellTimeoutMs = 20_000;
        private const long RunTimeoutMs = 5 * 60 * 1000;
        // AutoRetainer 处理完后需持续空闲这么久才认为结束（雇员之间切换时会短暂空闲）
        private const long IdleConfirmMs = 3_000;
        // 列表已打开但 AutoRetainer 一直不动（如雇员未在 AutoRetainer 中勾选）时放弃等待
        private const long StuckTimeoutMs = 30_000;
        private const long CloseTimeoutMs = 20_000;
        private const float BellInteractDistance = 2.5f;

        private static long _stepStartedAt;
        private static long _idleSince;
        private static long _atListSince;
        private static long _closeStartedAt;

        private static string? _bellName;
        private static string BellName => _bellName ??= Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ToString();

        private static bool RetainerListReady
            => GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon);

        public static bool Enabled
            => Player.Available && C.Resolve((ulong)Player.CID, ov => ov.AutoRetainer, c => c.AutoRetainer);

        /// <summary>
        /// 在接取任务前调用：开启了自动雇员、AutoRetainer 已安装、当前没有进行中的任务，且有雇员探险已完成。
        /// </summary>
        public static bool ShouldRun()
        {
            string tag = "[AutoRetainer: Check]";

            if (!Enabled || CosmicHelper.CurrentLunarMission != 0)
                return false;

            if (!P.AutoRetainer.Installed)
            {
                if (EzThrottler.Throttle("AutoRetainerNotInstalled", 60_000))
                    IceLogging.Warning("Auto Retainer is enabled but AutoRetainer is not installed", tag);
                return false;
            }

            if (!EzThrottler.Check(CooldownKey))
                return false;

            if (!P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara())
                return false;

            IceLogging.Info("Retainer ventures are ready, going to the summoning bell before grabbing a mission", tag);
            return true;
        }

        public static void Enqueue()
        {
            _stepStartedAt = 0;
            _idleSince = 0;
            _atListSince = 0;
            _closeStartedAt = 0;

            P.TaskManager.EnqueueMulti
                (
                    new(ReturnToHub, "AutoRetainer: Stellar Return to hub"),
                    new(MoveToBell, "AutoRetainer: Moving to summoning bell"),
                    new(OpenBell, "AutoRetainer: Opening the summoning bell"),
                    new(StartAutoRetainer, "AutoRetainer: Enabling AutoRetainer"),
                    new(WaitForAutoRetainer, "AutoRetainer: Waiting for AutoRetainer to finish"),
                    new(CloseBell, "AutoRetainer: Closing the retainer list"),
                    new(Finish, "AutoRetainer: Handing control back to ICE")
                );
        }

        private static unsafe bool? ReturnToHub()
        {
            string tag = "[AutoRetainer: Stellar Return]";

            if (FindBell(out _))
                return PlayerHelper.IsScreenReady();

            if (!CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var hubCenter))
            {
                IceLogging.Error($"No hub center for territory {Player.Territory.RowId}, skipping auto retainer", tag);
                return Abort();
            }

            if (Player.DistanceTo(hubCenter) < C.HubReturn_Distance)
                return PlayerHelper.IsScreenReady();

            if (!Player.IsBusy && PlayerHelper.IsScreenReady() && EzThrottler.Throttle("AutoRetainer_StellarReturn", 3000))
            {
                IceLogging.Info("Using Stellar Return to get back to the hub", tag);
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 26);
            }

            return false;
        }

        private static bool? MoveToBell()
        {
            string tag = "[AutoRetainer: Move To Bell]";

            if (!FindBell(out var bell))
            {
                IceLogging.Error($"Could not find a summoning bell ({BellName}) near the hub, skipping auto retainer", tag);
                return Abort();
            }

            if (Player.DistanceTo(bell.Position) <= BellInteractDistance)
            {
                if (P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
                return !Player.IsMoving;
            }

            Task_NavmeshMove.Task_NavTo(bell.Position, distance: BellInteractDistance);
            return false;
        }

        private static bool? OpenBell()
        {
            string tag = "[AutoRetainer: Open Bell]";

            if (RetainerListReady)
            {
                _stepStartedAt = 0;
                return true;
            }

            if (_stepStartedAt == 0)
                _stepStartedAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _stepStartedAt > OpenBellTimeoutMs)
            {
                IceLogging.Error("Could not open the retainer list from the summoning bell, skipping auto retainer", tag);
                return Abort();
            }

            if (!FindBell(out var bell) || Player.IsAnimationLocked || GenericHelpers.IsOccupied())
                return false;

            if (EzThrottler.Throttle("AutoRetainer_InteractBell", 3000))
            {
                IceLogging.Info("Interacting with the summoning bell", tag);
                Svc.Targets.Target = bell;
                Utils.InteractWithObject(bell);
            }

            return false;
        }

        private static bool? StartAutoRetainer()
        {
            string tag = "[AutoRetainer: Start]";

            // 打开铃后 AutoRetainer 可能按自身「打开传唤铃时」设置启用/禁用自己，统一在列表打开后再启用一次
            if (!EzThrottler.Throttle("AutoRetainer_Enable", 1000))
                return false;

            IceLogging.Info("Enabling AutoRetainer to process retainers", tag);
            Svc.Commands.ProcessCommand("/autoretainer e");
            _stepStartedAt = Environment.TickCount64;
            _idleSince = 0;
            _atListSince = 0;
            return true;
        }

        private static bool? WaitForAutoRetainer()
        {
            string tag = "[AutoRetainer: Wait]";

            bool atList = RetainerListReady && !P.AutoRetainer.IsBusy();
            bool idle = atList && !P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara();

            if (atList)
            {
                if (_atListSince == 0)
                    _atListSince = Environment.TickCount64;
                else if (Environment.TickCount64 - _atListSince > StuckTimeoutMs)
                {
                    IceLogging.Warning("AutoRetainer is not processing the retainers, continuing with missions", tag);
                    return true;
                }
            }
            else
            {
                _atListSince = 0;
            }

            if (idle)
            {
                if (_idleSince == 0)
                    _idleSince = Environment.TickCount64;
                else if (Environment.TickCount64 - _idleSince > IdleConfirmMs)
                {
                    IceLogging.Info("AutoRetainer finished processing retainers", tag);
                    return true;
                }
            }
            else
            {
                _idleSince = 0;
            }

            if (Environment.TickCount64 - _stepStartedAt > RunTimeoutMs)
            {
                IceLogging.Error("AutoRetainer did not finish in time, stopping it and continuing with missions", tag);
                P.AutoRetainer.AbortAllTasks();
                return true;
            }

            if (EzThrottler.Throttle("AutoRetainer_WaitLog", 10_000))
                IceLogging.Verbose("AutoRetainer is still processing retainers...", tag);

            return false;
        }

        private static bool? CloseBell()
        {
            if (_closeStartedAt == 0)
                _closeStartedAt = Environment.TickCount64;

            if (!RetainerListReady && !Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                Svc.Commands.ProcessCommand("/autoretainer d");
                return true;
            }

            if (Environment.TickCount64 - _closeStartedAt > CloseTimeoutMs)
            {
                IceLogging.Warning("Could not close the retainer list, continuing anyway", "[AutoRetainer: Close]");
                Svc.Commands.ProcessCommand("/autoretainer d");
                return true;
            }

            if (RetainerListReady && EzThrottler.Throttle("AutoRetainer_CloseList", 1000))
                GenericHandlers.FireCallback("RetainerList", true, -1);

            return false;
        }

        private static bool? Finish()
        {
            string tag = "[AutoRetainer: Finish]";

            EzThrottler.Throttle(CooldownKey, CooldownMs, true);

            if (P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara())
                IceLogging.Warning("AutoRetainer finished but retainers are still waiting. Make sure the retainers are enabled in AutoRetainer. Retrying in 5 minutes.", tag);
            else
                IceLogging.Info("Retainers processed, handing control back to ICE", tag);

            SchedulerMain.State = IceState.GrabMission;
            return true;
        }

        private static bool? Abort()
        {
            EzThrottler.Throttle(CooldownKey, CooldownMs, true);
            P.TaskManager.Tasks.Clear();
            SchedulerMain.State = IceState.GrabMission;
            return true;
        }

        private static bool FindBell(out IGameObject bell)
        {
            bell = null!;
            float best = float.MaxValue;
            foreach (var obj in Svc.Objects)
            {
                if (obj.ObjectKind != ObjectKind.EventObj || !obj.IsTargetable)
                    continue;
                if (!obj.Name.ToString().Equals(BellName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dist = Player.DistanceTo(obj.Position);
                if (dist < best && dist < C.HubReturn_Distance)
                {
                    best = dist;
                    bell = obj;
                }
            }
            return bell != null;
        }
    }
}
