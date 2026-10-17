using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.ConfigFiles;
using ICE.Scheduler.Handlers;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using ECommons.Automation.NeoTaskManager;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 接取任务前去基地传唤铃：宇宙返回 → 走到传唤铃 → 打开雇员列表 → 让 AutoRetainer 以普通模式处理探险
    /// 和/或让 Allagan Market 自动压价 → 关闭列表交回 ICE。两者同时到期时在同一次停留中完成，AutoRetainer 在前。
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
        // Allagan Market 拒绝启动（其他自动化在运行等）时重试这么久后放弃本次压价
        private const long UndercutStartTimeoutMs = 15_000;
        // 每个物品都要查一次市场，雇员多、上架多时很慢
        private const long UndercutRunTimeoutMs = 30 * 60 * 1000;
        private const long UndercutCancelTimeoutMs = 30_000;

        private static long _stepStartedAt;
        private static long _idleSince;
        private static long _atListSince;
        private static long _closeStartedAt;
        private static long _prepareStartedAt;
        private static long _undercutStartedAt;
        private static bool _undercutRunning;
        private static bool _undercutCancelled;

        // 本次停留要做的事，由 ShouldRun 决定
        private static bool _runRetainers;
        private static bool _runUndercut;

        // 各角色上次压价的时间（Environment.TickCount64）。插件重载后清空，即启动后第一次接任务前先压价一次
        private static readonly Dictionary<ulong, long> LastUndercut = [];

        private static string? _bellName;
        private static string BellName => _bellName ??= Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ToString();

        private static bool RetainerListReady
            => GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon);

        /// <summary>自动雇员或自动压价在角色动作列表中的位置（取靠前的），用于和出星球跑腿排先后；都不在列表中时为 int.MaxValue。</summary>
        public static int ListIndex()
        {
            int index = Char_Info.Actions.FindIndex(a => a.Enabled && a is AutoRetainerAction or AllaganMarketUndercutAction);
            return index < 0 ? int.MaxValue : index;
        }

        /// <summary>
        /// 在接取任务前调用：当前没有进行中的任务，且自动雇员或自动压价到期时返回 true，并记下本次要做哪些。
        /// 同时开启了自动雇员（且已装 AutoRetainer）时，压价不单独跑传唤铃，只在收雇员时顺便压价（仍受压价间隔限制）。
        /// </summary>
        public static bool ShouldRun()
        {
            if (!Player.Available || CosmicHelper.CurrentLunarMission != 0)
                return false;

            _runRetainers = RetainersDue();
            bool undercutWithRetainersOnly = Char_Info.Action<AutoRetainerAction>() != null && P.AutoRetainer.Installed;
            _runUndercut = (_runRetainers || !undercutWithRetainersOnly) && UndercutDue();
            if (!_runRetainers && !_runUndercut)
                return false;

            string what = _runRetainers && _runUndercut ? "Retainer ventures are ready and the undercut is due"
                : _runRetainers ? "Retainer ventures are ready" : "The Allagan Market undercut is due";
            IceLogging.Info($"{what}, going to the summoning bell before grabbing a mission", "[AutoRetainer: Check]");
            return true;
        }

        /// <summary>开启了自动雇员、AutoRetainer 已安装、不在冷却中，且有雇员探险已完成。</summary>
        private static bool RetainersDue()
        {
            string tag = "[AutoRetainer: Check]";

            if (Char_Info.Action<AutoRetainerAction>() == null)
                return false;

            if (!P.AutoRetainer.Installed)
            {
                if (EzThrottler.Throttle("AutoRetainerNotInstalled", 60_000))
                    IceLogging.Warning("Auto Retainer is enabled but AutoRetainer is not installed", tag);
                return false;
            }

            if (!EzThrottler.Check(CooldownKey))
                return false;

            return P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara();
        }

        /// <summary>开启了自动压价、Allagan Market 已安装、距上次压价已过设定间隔，且有勾选了自动压价的雇员。</summary>
        private static bool UndercutDue()
        {
            string tag = "[AllaganMarket: Check]";

            var action = Char_Info.Action<AllaganMarketUndercutAction>();
            if (action == null)
                return false;

            if (!P.AllaganMarket.Installed)
            {
                if (EzThrottler.Throttle("AllaganMarketNotInstalled", 60_000))
                    IceLogging.Warning("Auto Undercut is enabled but Allagan Market (CN build) is not installed", tag);
                return false;
            }

            if (LastUndercut.TryGetValue((ulong)Player.CID, out var last)
                && Environment.TickCount64 - last < action.IntervalMinutes * 60_000L)
                return false;

            if (P.AllaganMarket.EnabledRetainerCount() <= 0)
            {
                if (EzThrottler.Throttle("AllaganMarketNoRetainers", 10 * 60_000))
                    IceLogging.Warning("Auto Undercut is enabled but no retainer of this character has Auto Undercut ticked in Allagan Market (or Allagan Market is too old to have the IPC)", tag);
                return false;
            }

            return true;
        }

        public static void Enqueue()
        {
            _stepStartedAt = 0;
            _idleSince = 0;
            _atListSince = 0;
            _closeStartedAt = 0;
            _prepareStartedAt = 0;
            _undercutStartedAt = 0;
            _undercutRunning = false;
            _undercutCancelled = false;

            List<TaskManagerTask> tasks =
            [
                new(ReturnToHub, "AutoRetainer: Stellar Return to hub"),
                new(MoveToBell, "AutoRetainer: Moving to summoning bell"),
                new(OpenBell, "AutoRetainer: Opening the summoning bell"),
            ];

            if (_runRetainers)
            {
                tasks.Add(new(StartAutoRetainer, "AutoRetainer: Enabling AutoRetainer"));
                tasks.Add(new(WaitForAutoRetainer, "AutoRetainer: Waiting for AutoRetainer to finish"));
            }

            if (_runUndercut)
            {
                tasks.Add(new(PrepareUndercut, "AllaganMarket: Stopping AutoRetainer before the undercut"));
                // AutoRetainer 处理完后可能按自身设置关掉了雇员列表，这时重新打开
                tasks.Add(new(OpenBell, "AllaganMarket: Making sure the retainer list is open"));
                tasks.Add(new(StartUndercut, "AllaganMarket: Starting the undercut"));
                tasks.Add(new(WaitForUndercut, "AllaganMarket: Waiting for the undercut to finish"));
            }

            tasks.Add(new(CloseBell, "AutoRetainer: Closing the retainer list"));
            tasks.Add(new(Finish, "AutoRetainer: Handing control back to ICE"));

            P.TaskManager.EnqueueMulti([.. tasks]);
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

        private static bool? PrepareUndercut()
        {
            string tag = "[AllaganMarket: Prepare]";

            // 不论是 ICE 刚才启用的，还是打开传唤铃时 AutoRetainer 按自身设置启用的，都等它处理完再停用，免得和压价抢雇员窗口
            if (P.AutoRetainer.Installed)
            {
                if (_prepareStartedAt == 0)
                    _prepareStartedAt = Environment.TickCount64;

                if (P.AutoRetainer.IsBusy())
                {
                    if (Environment.TickCount64 - _prepareStartedAt <= RunTimeoutMs)
                        return false;

                    IceLogging.Warning("AutoRetainer is still busy, stopping it before the undercut", tag);
                    P.AutoRetainer.AbortAllTasks();
                }

                Svc.Commands.ProcessCommand("/autoretainer d");
            }

            // 接下来再走一次 OpenBell，它用 _stepStartedAt 计超时
            _stepStartedAt = 0;
            return true;
        }

        private static bool? StartUndercut()
        {
            string tag = "[AllaganMarket: Start]";

            if (!RetainerListReady)
                return false;

            if (_undercutStartedAt == 0)
                _undercutStartedAt = Environment.TickCount64;

            if (!EzThrottler.Throttle("AllaganMarket_Start", 1000))
                return false;

            if (P.AllaganMarket.Start())
            {
                IceLogging.Info("Allagan Market started repricing the retainers", tag);
                _undercutRunning = true;
                _undercutStartedAt = Environment.TickCount64;
                return true;
            }

            if (Environment.TickCount64 - _undercutStartedAt > UndercutStartTimeoutMs)
            {
                IceLogging.Warning("Allagan Market did not start the undercut (another Allagan Market automation is running, or no retainer has Auto Undercut ticked), skipping it this time", tag);
                return true;
            }

            return false;
        }

        private static bool? WaitForUndercut()
        {
            string tag = "[AllaganMarket: Wait]";

            if (!_undercutRunning)
                return true;

            if (!P.AllaganMarket.IsRunning())
            {
                IceLogging.Info(_undercutCancelled ? "Allagan Market stopped the undercut" : "Allagan Market finished repricing", tag);
                return true;
            }

            long elapsed = Environment.TickCount64 - _undercutStartedAt;
            if (!_undercutCancelled && elapsed > UndercutRunTimeoutMs)
            {
                IceLogging.Error("Allagan Market did not finish the undercut in time, cancelling it", tag);
                P.AllaganMarket.Cancel();
                _undercutCancelled = true;
            }
            else if (_undercutCancelled && elapsed > UndercutRunTimeoutMs + UndercutCancelTimeoutMs)
            {
                IceLogging.Error("Allagan Market is still running after being cancelled, continuing anyway", tag);
                return true;
            }

            if (EzThrottler.Throttle("AllaganMarket_WaitLog", 10_000))
                IceLogging.Verbose("Allagan Market is still repricing...", tag);

            return false;
        }

        private static bool? CloseBell()
        {
            if (_closeStartedAt == 0)
                _closeStartedAt = Environment.TickCount64;

            if (!RetainerListReady && !Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                DisableAutoRetainer();
                return true;
            }

            if (Environment.TickCount64 - _closeStartedAt > CloseTimeoutMs)
            {
                IceLogging.Warning("Could not close the retainer list, continuing anyway", "[AutoRetainer: Close]");
                DisableAutoRetainer();
                return true;
            }

            if (RetainerListReady && EzThrottler.Throttle("AutoRetainer_CloseList", 1000))
                GenericHandlers.FireCallback("RetainerList", true, -1);

            return false;
        }

        private static bool? Finish()
        {
            string tag = "[AutoRetainer: Finish]";

            MarkDone();

            if (_runRetainers && P.AutoRetainer.AreAnyRetainersAvailableForCurrentChara())
                IceLogging.Warning("AutoRetainer finished but retainers are still waiting. Make sure the retainers are enabled in AutoRetainer. Retrying in 5 minutes.", tag);
            else
                IceLogging.Info("Done at the summoning bell, handing control back to ICE", tag);

            SchedulerMain.State = IceState.GrabMission;
            return true;
        }

        private static bool? Abort()
        {
            MarkDone();
            P.TaskManager.Tasks.Clear();
            SchedulerMain.State = IceState.GrabMission;
            return true;
        }

        /// <summary>无论成功与否都记下本次已执行，避免无法完成时反复往返传唤铃。</summary>
        private static void MarkDone()
        {
            if (_runRetainers)
                EzThrottler.Throttle(CooldownKey, CooldownMs, true);
            if (_runUndercut)
                LastUndercut[(ulong)Player.CID] = Environment.TickCount64;
        }

        private static void DisableAutoRetainer()
        {
            if (P.AutoRetainer.Installed)
                Svc.Commands.ProcessCommand("/autoretainer d");
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
