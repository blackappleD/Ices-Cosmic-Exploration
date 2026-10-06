using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.IPC;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 接取任务前的自动雇员：宇宙返回 → 走到基地传唤铃 → 交给 AutoRetainer 跑一轮单角色多角色模式 → 交回 ICE。
    /// </summary>
    internal static class Task_AutoRetainer
    {
        private const string CooldownKey = "AutoRetainer_Cooldown";
        // 一轮结束（无论成功与否）后的冷却，避免 AutoRetainer 未处理（如角色未在多角色模式中启用）时反复触发
        private const int CooldownMs = 5 * 60 * 1000;
        private const long StartTimeoutMs = 15_000;
        private const long RunTimeoutMs = 15 * 60 * 1000;
        private const float BellInteractDistance = 2.5f;

        private static long _startedAt;
        private static bool _multiModeSeen;

        private static string? _bellName;
        private static string BellName => _bellName ??= Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ToString();

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

            IceLogging.Info("Retainer ventures are ready, handing over to AutoRetainer before grabbing a mission", tag);
            return true;
        }

        public static void Enqueue()
        {
            _startedAt = 0;
            _multiModeSeen = false;

            P.TaskManager.EnqueueMulti
                (
                    new(ReturnToHub, "AutoRetainer: Stellar Return to hub"),
                    new(MoveToBell, "AutoRetainer: Moving to summoning bell"),
                    new(StartAutoRetainer, "AutoRetainer: Starting single multi mode"),
                    new(WaitForAutoRetainer, "AutoRetainer: Waiting for AutoRetainer to finish"),
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
                if (EzThrottler.Throttle("AutoRetainer_NoBell", 5000))
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

        private static bool? StartAutoRetainer()
        {
            string tag = "[AutoRetainer: Start]";

            if (_startedAt == 0)
            {
                IceLogging.Info("Enabling AutoRetainer single multi mode for the current character", tag);
                P.AutoRetainer.EnableSingleMultiMode(AutoRetainerIPC.MultiMode_Retainers);
                _startedAt = Environment.TickCount64;
                return false;
            }

            if (P.AutoRetainer.GetMultiModeStatus() || P.AutoRetainer.IsBusy())
            {
                _multiModeSeen = true;
                return true;
            }

            if (Environment.TickCount64 - _startedAt > StartTimeoutMs)
            {
                IceLogging.Warning("AutoRetainer did not start multi mode, continuing with missions", tag);
                return true;
            }

            return false;
        }

        private static bool? WaitForAutoRetainer()
        {
            string tag = "[AutoRetainer: Wait]";

            if (!_multiModeSeen)
                return true;

            bool running = P.AutoRetainer.GetMultiModeStatus() || P.AutoRetainer.IsBusy();
            if (!running && Player.Interactable && PlayerHelper.IsScreenReady() && !GenericHelpers.IsOccupied())
                return true;

            if (Environment.TickCount64 - _startedAt > RunTimeoutMs)
            {
                IceLogging.Error("AutoRetainer has been running for too long, aborting it and continuing with missions", tag);
                P.AutoRetainer.AbortAllTasks();
                P.AutoRetainer.DisableAllFunctions();
                return true;
            }

            if (EzThrottler.Throttle("AutoRetainer_WaitLog", 10_000))
                IceLogging.Verbose("AutoRetainer is still processing retainers...", tag);

            return false;
        }

        private static bool? Finish()
        {
            string tag = "[AutoRetainer: Finish]";

            EzThrottler.Throttle(CooldownKey, CooldownMs, true);

            if (P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara())
                IceLogging.Warning("AutoRetainer finished but retainers are still waiting. Make sure this character and its retainers are enabled in AutoRetainer's multi mode settings. Retrying in 5 minutes.", tag);
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

        private static bool FindBell(out Dalamud.Game.ClientState.Objects.Types.IGameObject bell)
        {
            bell = null!;
            float best = float.MaxValue;
            foreach (var obj in Svc.Objects)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj || !obj.IsTargetable)
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
