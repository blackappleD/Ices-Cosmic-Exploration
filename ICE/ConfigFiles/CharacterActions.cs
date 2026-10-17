using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using static ICE.Localization.L10n;

namespace ICE.ConfigFiles;

/// <summary>
/// 角色设置中可自由增减 / 排序的动作（参考 AutoDuty 的 LoopActionConfig）。
/// 列表顺序决定接取任务前「自动雇员」与「出星球跑腿」的先后，以及一趟出行中各项的执行顺序。
/// </summary>
public abstract class CharacterAction
{
    /// <summary>反序列化时用来还原具体类型，只写不读。</summary>
    [JsonProperty] public string Kind => GetType().Name;

    public bool Enabled { get; set; } = true;

    [JsonIgnore] public abstract string Name { get; }
    [JsonIgnore] public virtual string? HelpText => null;
    /// <summary>依赖的插件（显示名, InternalName），null 表示不依赖。</summary>
    [JsonIgnore] public virtual (string Display, string InternalName)? RequiredPlugin => null;
    [JsonIgnore] public virtual bool HasConfig => true;

    public virtual void DrawSettings() { }

    public CharacterAction Clone() => (CharacterAction)MemberwiseClone();

    /// <summary>「+」菜单中可添加的动作，按显示顺序。</summary>
    public static readonly (Type Type, Func<CharacterAction> Create)[] Available =
    [
        (typeof(RepairAction), () => new RepairAction()),
        (typeof(ExtractMateriaAction), () => new ExtractMateriaAction()),
        (typeof(AutoRetainerAction), () => new AutoRetainerAction()),
        (typeof(GCTurninAction), () => new GCTurninAction()),
        (typeof(GlamourDresserAction), () => new GlamourDresserAction()),
        (typeof(ArmoireAction), () => new ArmoireAction()),
    ];

    public static CharacterAction? Create(string? kind)
        => Available.FirstOrDefault(a => a.Type.Name == kind).Create?.Invoke();

    public static List<CharacterAction> DefaultList() => [.. Available.Select(a => a.Create())];

    protected static bool SlotsInput(string id, ref int slots)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(T("Inventory Slots Left @"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (!ImGui.InputInt($"##{id}", ref slots, 1, 5))
            return false;
        slots = Math.Clamp(slots, 0, 140);
        return true;
    }
}

/// <summary>按 Kind 字段还原 <see cref="CharacterAction"/> 的具体类型；未知类型返回 null（读取后会被过滤掉）。</summary>
public sealed class CharacterActionConverter : JsonConverter
{
    public override bool CanWrite => false;
    public override bool CanConvert(Type objectType) => typeof(CharacterAction).IsAssignableFrom(objectType);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var jo = JObject.Load(reader);
        var action = CharacterAction.Create(jo["Kind"]?.Value<string>());
        if (action == null)
            return null;

        using var sub = jo.CreateReader();
        serializer.Populate(sub, action);
        return action;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();
}

public enum InnLocation
{
    GrandCompany = 0,
    Limsa = 1,
    Gridania = 2,
    Uldah = 3,
    // 所属大国防联军的军营（冒险者小队），房间里同样有时装衣柜和幻化衣柜
    Barracks = 4,
}

public sealed class RepairAction : CharacterAction
{
    public override string Name => T("Repair Gear");

    public bool RepairAtVendor { get; set; } = false;
    public bool SelfRepairGather { get; set; } = true;
    public bool SelfRepairCrafter { get; set; } = false;
    public int RepairPercent { get; set; } = 50;
    public bool RepairAllGear { get; set; } = true;
    public bool StopDarkMatter { get; set; } = true;
    public int MinimumDarkMatter { get; set; } = 12;

    public override void DrawSettings()
    {
        bool repairAtVendor = RepairAtVendor;
        if (ImGui.Checkbox(T("Repair at Vendor"), ref repairAtVendor))
        { RepairAtVendor = repairAtVendor; C.Save(); }

        using (ImRaii.Disabled(repairAtVendor))
        {
            bool selfRepairGather = SelfRepairGather;
            if (ImGui.Checkbox(T("Self Repair Gather"), ref selfRepairGather))
            { SelfRepairGather = selfRepairGather; C.Save(); }

            bool selfRepairCrafter = SelfRepairCrafter;
            if (ImGui.Checkbox(T("Self Repair Crafter"), ref selfRepairCrafter))
            { SelfRepairCrafter = selfRepairCrafter; C.Save(); }
        }

        float repairAmount = RepairPercent;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("###Repair %", ref repairAmount, 0f, 99f, "%.0f%%") && RepairPercent != (int)repairAmount)
        { RepairPercent = (int)repairAmount; C.SaveDebounced(); }

        bool repairAll = RepairAllGear;
        if (ImGui.Checkbox(T("Repair all gear in bag"), ref repairAll))
        { RepairAllGear = repairAll; C.Save(); }

        bool stopDarkMatter = StopDarkMatter;
        if (ImGui.Checkbox(T("Stop when below x dark matter"), ref stopDarkMatter))
        { StopDarkMatter = stopDarkMatter; C.Save(); }

        int minDM = MinimumDarkMatter;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt(T("Minimum Grade 8 Dark Matter"), ref minDM, 1, 10))
        { MinimumDarkMatter = minDM; C.SaveDebounced(); }

        PlayerHelper.GetItemCount(Utils.DarkMatter_8Id, out var dmCount);
        ImGui.Text(T("Currently have: {0:N0} Grade 8 Dark Matter", dmCount));
    }
}

public sealed class ExtractMateriaAction : CharacterAction
{
    public override string Name => T("Extract Materia");
    public override string? HelpText => T("Extracts materia from gear at 100% spiritbond while on a gathering or crafting job.");
    public override bool HasConfig => false;
}

public sealed class AutoRetainerAction : CharacterAction
{
    public override string Name => T("AutoRetainer (Retainers)");
    public override (string Display, string InternalName)? RequiredPlugin => ("AutoRetainer", IPC.AutoRetainerIPC.Name);
    public override string? HelpText => T("Before grabbing each mission, if any retainer venture is finished, uses Stellar Return, opens the hub summoning bell and lets AutoRetainer process the retainers, then continues ICE.\nRequires AutoRetainer (or AutoRetainer-CN). Only retainers enabled in AutoRetainer are processed.");
    public override bool HasConfig => false;
}

public sealed class GCTurninAction : CharacterAction
{
    public override string Name => T("GC Turn-in");
    public override (string Display, string InternalName)? RequiredPlugin => ("AutoRetainer", IPC.AutoRetainerIPC.Name);
    public override string? HelpText => T("Before grabbing a mission, when free inventory slots drop to the value below, leaves the moon, lets AutoRetainer hand in Expert Delivery items at your Grand Company, then returns to the moon you came from.\nRequires AutoRetainer and Grand Company rank 6 or higher. Which items are handed in follows AutoRetainer's own GC delivery settings.");

    public int SlotsLeft { get; set; } = 10;
    public bool UseTicket { get; set; } = false;

    public override void DrawSettings()
    {
        int slots = SlotsLeft;
        if (SlotsInput("GCTurninSlotsLeft", ref slots))
        { SlotsLeft = slots; C.SaveDebounced(); }
        ImGuiEx.HelpMarker(T("Triggers when free inventory slots are at or below this value. If a turn-in frees no space, ICE waits until the inventory gets fuller before trying again."));

        bool useTicket = UseTicket;
        if (ImGui.Checkbox(T("Use GC Aetheryte Ticket"), ref useTicket))
        { UseTicket = useTicket; C.Save(); }
        ImGuiEx.HelpMarker(T("Uses the Grand Company aetheryte ticket when you have one. Otherwise teleports to the city aetheryte (Limsa Lominsa also needs Lifestream for the aethernet)."));
    }
}

/// <summary>幻化衣柜 / 时装衣柜共用：剩余格数阈值 + 去哪家旅馆。</summary>
public abstract class GlamourStorageAction : CharacterAction
{
    public override (string Display, string InternalName)? RequiredPlugin => ("Glamour Log", IPC.GlamourLogIPC.Name);

    public int SlotsLeft { get; set; } = 10;
    public InnLocation Inn { get; set; } = InnLocation.GrandCompany;

    public static string InnName(InnLocation inn) => inn switch
    {
        InnLocation.Limsa => T("The Mizzenmast (Limsa Lominsa)"),
        InnLocation.Gridania => T("The Roost (Gridania)"),
        InnLocation.Uldah => T("The Hourglass (Ul'dah)"),
        InnLocation.Barracks => $"{T("Grand Company barracks (Squadron)")} ({CurrentGcCityName()})",
        _ => $"{T("Inn of your Grand Company city")} ({CurrentGcCityName()})",
    };

    /// <summary>当前角色所属大国防联军的主城，直接显示出来，方便确认「所属大国防联军主城」指的是哪座城。</summary>
    private static unsafe string CurrentGcCityName()
    {
        if (!Player.Available)
            return T("not logged in");
        return PlayerState.Instance()->GrandCompany switch
        {
            1 => T("Limsa Lominsa"),
            2 => T("Gridania"),
            3 => T("Ul'dah"),
            _ => T("no Grand Company"),
        };
    }

    public override void DrawSettings()
    {
        int slots = SlotsLeft;
        if (SlotsInput($"{Kind}SlotsLeft", ref slots))
        { SlotsLeft = slots; C.SaveDebounced(); }
        ImGuiEx.HelpMarker(T("Only leaves the moon when free inventory slots are at or below this value and there is something to store."));

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(T("Inn"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo($"##{Kind}Inn", InnName(Inn)))
        {
            foreach (var inn in Enum.GetValues<InnLocation>())
            {
                if (ImGui.Selectable(InnName(inn), Inn == inn))
                { Inn = inn; C.Save(); }
            }
            ImGui.EndCombo();
        }
        ImGuiEx.HelpMarker(T("Where to store the items. Inn rooms and the Grand Company barracks both have the armoire and the glamour dresser.\nThe barracks needs the squadron unlocked; without it the inn of your Grand Company city is used."));
    }
}

public sealed class GlamourDresserAction : GlamourStorageAction
{
    public override string Name => T("Glamour Dresser");
    public override string? HelpText => T("Before grabbing a mission, if your inventory has outfit pieces that are not stored yet, goes to an inn room, lets Glamour Log store them into the glamour dresser, then returns to the moon you came from.\nStoring into the glamour dresser uses glamour prisms.");
}

public sealed class ArmoireAction : GlamourStorageAction
{
    public override string Name => T("Armoire");
    public override string? HelpText => T("Before grabbing a mission, if your inventory has items that can be stored in the armoire, goes to an inn room, lets Glamour Log store them into the armoire, then returns to the moon you came from.");
}
