using ECommons.GameHelpers;
using ICE.ConfigFiles;
using System;
using System.Collections.Generic;
using System.Text;
using static ICE.ConfigFiles.Config;

namespace ICE.Utilities
{
    public static class Char_Info
    {
        private static Config G => C; // just an alias for readability
        private static ulong CID => Player.CID != 0 ? (ulong)Player.CID : 0UL;

        private static CharacterOverride? Ov => C.CharacterOverrides.GetValueOrDefault(CID);

        // Character actions (repair / materia / retainer / GC turn-in / glamour storage)
        public static List<CharacterAction> Actions => Ov?.Actions ?? G.CharacterActions ?? [];

        /// <summary>当前角色列表中第一个已启用的该类动作；没有或未启用时为 null。</summary>
        public static T? Action<T>() where T : CharacterAction
            => Actions.OfType<T>().FirstOrDefault(a => a.Enabled);

        // Repair — 列表中没有已启用的「修理装备」时不修理
        private static RepairAction? Repair => Action<RepairAction>();
        public static bool RepairEnabled => Repair != null;
        public static bool SelfRepairGather => Repair?.SelfRepairGather ?? false;
        public static bool SelfRepairCrafter => Repair?.SelfRepairCrafter ?? false;
        public static bool RepairAtVendor => Repair?.RepairAtVendor ?? false;
        public static int RepairPercent => Repair?.RepairPercent ?? 0;
        public static bool RepairAllGear => Repair?.RepairAllGear ?? false;
        public static bool Stop_DarkMatter => Repair?.StopDarkMatter ?? false;
        public static int Minimum_DarkMatter => Repair?.MinimumDarkMatter ?? 0;

        // Materia
        public static bool AutoExtractMateria => Action<ExtractMateriaAction>() != null;

        // Artisan
        public static Global_Artisan Artisan_GlobalStandard => Ov?.Artisan_GlobalStandard ?? G.Artisan_GlobalStandard;
        public static Global_Artisan Artisan_GlobalExpert => Ov?.Artisan_GlobalExpert ?? G.Artisan_GlobalExpert;

        // Mount — the four radius/usage ones are global-only
        public static uint MountId => Ov?.MountId ?? G.MountId;
        public static string MountName => Ov?.MountName ?? G.MountName;
        public static bool UseMountOutsideMission => G.UseMountOutsideMission;
        public static bool UseMountInMission => G.UseMountInMission;
        public static float MountRadius => G.MountRadius;
        public static float DismountRadius => G.DismountRadius;

        // Relic
        public static bool Relic_SwapJob => Ov?.Relic_SwapJob ?? G.Relic_SwapJob;
        public static uint Relic_BattleJob => Ov?.Relic_BattleJob ?? G.Relic_BattleJob;
        public static bool Relic_Stylist => Ov?.Relic_Stylist ?? G.Relic_Stylist;
    }
}
