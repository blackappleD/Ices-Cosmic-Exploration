using ECommons.GameHelpers;
using ECommons.Throttlers;
using ICE.Utilities.Cosmic_Helper;
using System;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_AutoRetainer
    {
        public static void Enqueue()
        {
            if (!ShouldProcessRetainers())
                return;

            P.TaskManager.Enqueue(() => ProcessRetainers(), "Processing retainers");
        }

        private static bool ShouldProcessRetainers()
        {
            string tag = "[Task AutoRetainer: Check]";

            if (!Player.Available)
            {
                IceLogging.Verbose("Player not available", tag);
                return false;
            }

            var cid = (ulong)Player.CID;
            bool autoRetainerEnabled = C.Resolve(cid, ov => ov.AutoRetainer, c => c.AutoRetainer);

            if (!autoRetainerEnabled)
            {
                IceLogging.Verbose("Auto Retainer is disabled for this character", tag);
                return false;
            }

            if (!P.AutoRetainer.Installed)
            {
                if (EzThrottler.Throttle("AutoRetainerNotInstalled", 30000))
                    IceLogging.Warning("Auto Retainer is enabled but AutoRetainer/AutoRetainCN plugin is not installed", tag);
                return false;
            }

            try
            {
                if (P.AutoRetainer.AreAnyRetainersAvailable == null)
                {
                    IceLogging.Verbose("AutoRetainer IPC not ready", tag);
                    return false;
                }

                bool hasRetainers = P.AutoRetainer.AreAnyRetainersAvailable();
                if (!hasRetainers)
                {
                    IceLogging.Verbose("No retainers available for processing", tag);
                    return false;
                }

                IceLogging.Info("Retainers are available and will be processed", tag);
                return true;
            }
            catch (Exception ex)
            {
                IceLogging.Error($"Error checking retainer availability: {ex.Message}", tag);
                return false;
            }
        }

        private static bool? ProcessRetainers()
        {
            string tag = "[Task AutoRetainer: Process]";

            if (!P.AutoRetainer.Installed)
            {
                IceLogging.Error("AutoRetainer plugin not found", tag);
                return true;
            }

            try
            {
                if (P.AutoRetainer.IsBusy != null && P.AutoRetainer.IsBusy())
                {
                    IceLogging.Verbose("AutoRetainer is busy, waiting...", tag);
                    return false;
                }

                if (P.AutoRetainer.AreAnyRetainersAvailable != null && P.AutoRetainer.AreAnyRetainersAvailable())
                {
                    if (EzThrottler.Throttle("ProcessRetainers", 2000))
                    {
                        IceLogging.Info("Starting retainer processing", tag);
                        P.AutoRetainer.ProcessRetainers?.Invoke();
                    }
                    return false;
                }
                else
                {
                    IceLogging.Info("Retainer processing completed", tag);
                    return true;
                }
            }
            catch (Exception ex)
            {
                IceLogging.Error($"Error processing retainers: {ex.Message}", tag);
                return true;
            }
        }
    }
}
