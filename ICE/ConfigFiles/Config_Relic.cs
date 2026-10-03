using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.ConfigFiles;

public partial class Config
{
    public bool TurninRelic { get; set; } = false;
    public bool Relic_SwapJob { get; set; } = false;
    public uint Relic_BattleJob { get; set; } = 0;
    // 沿用旧名以保留已有配置，现在使用 Gearsetter 重新穿戴推荐装备
    public bool Relic_Stylist { get; set; } = true;

    public Dictionary<uint, bool> RelicJobs { get; set; } = new()
    {
        [8] = true,
        [9] = true,
        [10] = true,
        [11] = true,
        [12] = true,
        [13] = true,
        [14] = true,
        [15] = true,
        [16] = true,
        [17] = true,
        [18] = true
    };
    public bool FarmAllRelics { get; set; } = false;
    public bool Stop_AllRelicsComplete { get; set; } = false;
}
