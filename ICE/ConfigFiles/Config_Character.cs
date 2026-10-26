using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.ConfigFiles;

public partial class Config
{
    public class CharacterOverride
    {
        /// <summary>该角色专属的动作列表；null 表示沿用全局列表。</summary>
        [Newtonsoft.Json.JsonProperty(ItemConverterType = typeof(CharacterActionConverter))]
        public List<CharacterAction>? Actions { get; set; } = null;

        // Legacy: 修理 / 精炼 / 雇员 / 军票 / 幻化 的旧覆写，只用于迁移到 Actions（见 MigrateCharacterActions）
        public bool? SelfRepairGather { get; set; } = null;
        public bool? SelfRepairCrafter { get; set; } = null;
        public bool? RepairAtVendor { get; set; } = null;
        public int? RepairPercent { get; set; } = null;
        public bool? AutoExtractMateria { get; set; } = null;
        public bool? RepairAllGear { get; set; } = null;
        public int? Minimum_DarkMatter { get; set; } = null;

        public Global_Artisan? Artisan_GlobalStandard { get; set; } = null;
        public Global_Artisan? Artisan_GlobalExpert { get; set; } = null;

        public uint? MountId { get; set; } = null;
        public string? MountName { get; set; } = null;

        // 个人返回点：CrafterLocations 非 null 即表示该角色覆盖了全局（开关与位置一起覆盖）
        public bool? PersonalReturnSpot { get; set; } = null;
        public Dictionary<uint, Vector3>? CrafterLocations { get; set; } = null;

        public bool? Relic_SwapJob { get; set; } = null;
        public uint? Relic_BattleJob { get; set; } = null;
        public bool? Relic_Stylist { get; set; } = null;

        public bool? AutoRetainer { get; set; } = null;

        public bool? AutoGCTurnin { get; set; } = null;
        public int? GCTurnin_SlotsLeft { get; set; } = null;
        public bool? GCTurnin_UseTicket { get; set; } = null;

        public bool? AutoGlamourDresser { get; set; } = null;
        public bool? AutoArmoire { get; set; } = null;
    }

    public Dictionary<ulong, CharacterOverride> CharacterOverrides { get; set; } = new();
    public Dictionary<ulong, string> KnownCharacters { get; set; } = new();

    /// <summary>每个角色的实际运行时长（只计插件处于运行状态的时间），见 RuntimeTracker。</summary>
    public class CharacterRuntime
    {
        /// <summary>累计运行秒数（本次运行时长只在内存中，见 RuntimeTracker）。</summary>
        public double TotalSeconds { get; set; } = 0;
    }

    // 单独存放，删除角色页签（CharacterOverrides / KnownCharacters）时不会丢失统计
    public Dictionary<ulong, CharacterRuntime> CharacterRuntimes { get; set; } = new();

    public T Resolve<T>(ulong cid, Func<CharacterOverride, T?> overrideProp, Func<Config, T> globalProp) where T : struct
        => CharacterOverrides.TryGetValue(cid, out var ov) && overrideProp(ov) is { } val
            ? val
            : globalProp(this);

    // Overload for reference types (string, Global_Artisan, etc.)
    public T ResolveRef<T>(ulong cid, Func<CharacterOverride, T?> overrideProp, Func<Config, T> globalProp) where T : class
        => CharacterOverrides.TryGetValue(cid, out var ov) && overrideProp(ov) is { } val
            ? val
            : globalProp(this);
}
