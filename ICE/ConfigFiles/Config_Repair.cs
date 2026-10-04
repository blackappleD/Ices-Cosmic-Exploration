using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.ConfigFiles;

public partial class Config
{
    public bool SelfRepairGather { get; set; } = true;
    public bool SelfRepairCrafter { get; set; } = false;
    public bool RepairAtVendor { get; set; } = false;
    public int RepairPercent { get; set; } = 50;
    public bool AutoExtractMateria { get; set; } = true;
    // Legacy key from the old gather-only toggle; migrates into AutoExtractMateria on load, never written back
    [JsonProperty] private bool SelfSpiritbondGather { set => AutoExtractMateria = value; }
    public bool RepairAllGear { get; set; } = true;
    public bool Stop_DarkMatter { get; set; } = true;
    public int Minimum_DarkMatter { get; set; } = 12;
}
