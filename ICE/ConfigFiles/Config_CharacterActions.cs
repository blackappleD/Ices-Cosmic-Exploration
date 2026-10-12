using Newtonsoft.Json;
using System.Collections.Generic;

namespace ICE.ConfigFiles;

public partial class Config
{
    /// <summary>全局的角色动作列表；null 表示还没从旧版的分散设置迁移过来。</summary>
    [JsonProperty(ItemConverterType = typeof(CharacterActionConverter))]
    public List<CharacterAction>? CharacterActions { get; set; } = null;

    /// <summary>
    /// 把旧版「修理 / 精炼 / 自动雇员 / 军票上缴 / 幻化收纳」的分散设置（含角色覆写）转成动作列表，只执行一次。
    /// 旧字段保留，仅用于迁移。
    /// </summary>
    public void MigrateCharacterActions()
    {
        if (CharacterActions != null)
        {
            // 去掉无法识别的条目（例如降级后读到新版本写入的类型）
            CharacterActions.RemoveAll(a => a == null);
            foreach (var ov in CharacterOverrides.Values)
                ov.Actions?.RemoveAll(a => a == null);
            return;
        }

        CharacterActions = BuildLegacyActions(null);

        foreach (var ov in CharacterOverrides.Values)
        {
            bool hasLegacyOverride =
                ov.SelfRepairGather.HasValue || ov.SelfRepairCrafter.HasValue || ov.RepairAtVendor.HasValue
                || ov.RepairPercent.HasValue || ov.RepairAllGear.HasValue || ov.Minimum_DarkMatter.HasValue
                || ov.AutoExtractMateria.HasValue || ov.AutoRetainer.HasValue
                || ov.AutoGCTurnin.HasValue || ov.GCTurnin_SlotsLeft.HasValue || ov.GCTurnin_UseTicket.HasValue
                || ov.AutoGlamourDresser.HasValue || ov.AutoArmoire.HasValue;

            if (hasLegacyOverride && ov.Actions == null)
                ov.Actions = BuildLegacyActions(ov);
        }

        Save();
    }

    private List<CharacterAction> BuildLegacyActions(CharacterOverride? ov) =>
    [
        new RepairAction
        {
            RepairAtVendor = ov?.RepairAtVendor ?? RepairAtVendor,
            SelfRepairGather = ov?.SelfRepairGather ?? SelfRepairGather,
            SelfRepairCrafter = ov?.SelfRepairCrafter ?? SelfRepairCrafter,
            RepairPercent = ov?.RepairPercent ?? RepairPercent,
            RepairAllGear = ov?.RepairAllGear ?? RepairAllGear,
            StopDarkMatter = Stop_DarkMatter,
            MinimumDarkMatter = ov?.Minimum_DarkMatter ?? Minimum_DarkMatter,
        },
        new ExtractMateriaAction { Enabled = ov?.AutoExtractMateria ?? AutoExtractMateria },
        new AutoRetainerAction { Enabled = ov?.AutoRetainer ?? AutoRetainer },
        new GCTurninAction
        {
            Enabled = ov?.AutoGCTurnin ?? AutoGCTurnin,
            SlotsLeft = ov?.GCTurnin_SlotsLeft ?? GCTurnin_SlotsLeft,
            UseTicket = ov?.GCTurnin_UseTicket ?? GCTurnin_UseTicket,
        },
        new GlamourDresserAction { Enabled = ov?.AutoGlamourDresser ?? AutoGlamourDresser },
        new ArmoireAction { Enabled = ov?.AutoArmoire ?? AutoArmoire },
    ];
}
