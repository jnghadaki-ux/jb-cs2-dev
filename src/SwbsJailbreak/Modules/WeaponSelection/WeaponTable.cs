using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.WeaponSelection;

/// <summary>CSWeaponID equivalents used by the original ultjb_weapon_selection.inc (only the ones UltJB used).</summary>
public enum WeaponId
{
    None,
    Knife, KnifeT, KnifeGG, Taser,
    Glock, HKP2000, USP_Silencer, Elite, P250, CZ75A, FiveSeven, Tec9, Deagle, Revolver,
    Nova, XM1014, SawedOff, MAG7, M249, Negev,
    MAC10, MP7, MP9, UMP45, P90, Bizon, MP5SD,
    GalilAR, Famas, AK47, M4A1, M4A1_Silencer, SSG08, SG556, AUG, AWP, G3SG1, SCAR20,
    HEGrenade, Flashbang, SmokeGrenade, Decoy, IncGrenade, Molotov, TAGrenade, Healthshot, C4, BreachCharge
}

/// <summary>One row of the weapon table (spec 02 §6.2.2 - single source of truth).</summary>
public sealed record WeaponInfo(
    WeaponId Id,
    string Classname,
    ushort DefIndex,
    int DefaultTeam,
    string MenuText,
    WeaponCategory? Category,
    int Bit)
{
    /// <summary>Name used in <see cref="WeaponFilter.Disabled"/> (KnifeT shares the "Knife" item/bit with Knife).</summary>
    public string FilterKey => Id == WeaponId.KnifeT ? "weapon_knife" : Classname;

    /// <summary>What CS2 is actually asked to give (the golden knife cannot be given in CS2).</summary>
    public string GiveName => Id == WeaponId.KnifeGG ? "weapon_knife" : Classname;

    public bool Giveable => Id is not (WeaponId.BreachCharge or WeaponId.None);
}

/// <summary>
/// Port of the weapon helpers from UltJB_API_WeaponSelection.sp (GetEntNameFromWeaponID, GetItemDefIndexFromWeaponID,
/// GetWeaponsDefaultTeam, GetRandomWeaponFromFlags) plus the CS2 classname-by-item-definition resolver that the
/// Logger / LR / Days modules need because CS2 reports alternate weapons under their base designer name.
/// Pure static data: usable even when the WeaponSelection module is disabled.
/// </summary>
public static class WeaponTable
{
    public const int TeamAny = -1;
    public const int TeamNone = 0;
    public const int TeamT = 2;
    public const int TeamCT = 3;

    private const int A = TeamAny, N = TeamNone, T = TeamT, C = TeamCT;

    public static readonly IReadOnlyList<WeaponInfo> All = new List<WeaponInfo>
    {
        new(WeaponId.Knife, "weapon_knife", 42, C, "Knife", WeaponCategory.Knife, 0),
        new(WeaponId.KnifeT, "weapon_knife_t", 59, T, "Knife", WeaponCategory.Knife, 0),
        new(WeaponId.KnifeGG, "weapon_knifegg", 41, A, "Golden Knife", null, -1),
        new(WeaponId.Taser, "weapon_taser", 31, A, "Taser", WeaponCategory.Knife, 1),

        new(WeaponId.Glock, "weapon_glock", 4, T, "Glock", WeaponCategory.Pistols, 0),
        new(WeaponId.HKP2000, "weapon_hkp2000", 32, N, "P2000", WeaponCategory.Pistols, 1),
        new(WeaponId.USP_Silencer, "weapon_usp_silencer", 61, C, "USP-S", WeaponCategory.Pistols, 2),
        new(WeaponId.Elite, "weapon_elite", 2, A, "Dual Berettas", WeaponCategory.Pistols, 3),
        new(WeaponId.P250, "weapon_p250", 36, A, "P250", WeaponCategory.Pistols, 4),
        new(WeaponId.CZ75A, "weapon_cz75a", 63, A, "CZ75-Auto", WeaponCategory.Pistols, 5),
        new(WeaponId.FiveSeven, "weapon_fiveseven", 3, C, "Five-Seven", WeaponCategory.Pistols, 6),
        new(WeaponId.Tec9, "weapon_tec9", 30, T, "Tec-9", WeaponCategory.Pistols, 7),
        new(WeaponId.Deagle, "weapon_deagle", 1, A, "Desert Eagle", WeaponCategory.Pistols, 8),
        new(WeaponId.Revolver, "weapon_revolver", 64, A, "R8 Revolver", WeaponCategory.Pistols, 9),

        new(WeaponId.Nova, "weapon_nova", 35, A, "Nova", WeaponCategory.Heavy, 0),
        new(WeaponId.XM1014, "weapon_xm1014", 25, A, "XM1014", WeaponCategory.Heavy, 1),
        new(WeaponId.SawedOff, "weapon_sawedoff", 29, T, "Sawed-Off", WeaponCategory.Heavy, 2),
        new(WeaponId.MAG7, "weapon_mag7", 27, C, "MAG-7", WeaponCategory.Heavy, 3),
        new(WeaponId.M249, "weapon_m249", 14, A, "M249", WeaponCategory.Heavy, 4),
        new(WeaponId.Negev, "weapon_negev", 28, A, "Negev", WeaponCategory.Heavy, 5),

        new(WeaponId.MAC10, "weapon_mac10", 17, T, "MAC-10", WeaponCategory.Smgs, 0),
        new(WeaponId.MP7, "weapon_mp7", 33, A, "MP7", WeaponCategory.Smgs, 1),
        new(WeaponId.MP9, "weapon_mp9", 34, C, "MP9", WeaponCategory.Smgs, 2),
        new(WeaponId.UMP45, "weapon_ump45", 24, A, "UMP-45", WeaponCategory.Smgs, 3),
        new(WeaponId.P90, "weapon_p90", 19, A, "P90", WeaponCategory.Smgs, 4),
        new(WeaponId.Bizon, "weapon_bizon", 26, A, "PP-Bizon", WeaponCategory.Smgs, 5),
        new(WeaponId.MP5SD, "weapon_mp5sd", 23, A, "MP5-SD", WeaponCategory.Smgs, 6),

        new(WeaponId.GalilAR, "weapon_galilar", 13, T, "Galil AR", WeaponCategory.Rifles, 0),
        new(WeaponId.Famas, "weapon_famas", 10, C, "FAMAS", WeaponCategory.Rifles, 1),
        new(WeaponId.AK47, "weapon_ak47", 7, T, "AK-47", WeaponCategory.Rifles, 2),
        new(WeaponId.M4A1, "weapon_m4a1", 16, C, "M4A4", WeaponCategory.Rifles, 3),
        new(WeaponId.M4A1_Silencer, "weapon_m4a1_silencer", 60, C, "M4A1-S", WeaponCategory.Rifles, 4),
        new(WeaponId.SSG08, "weapon_ssg08", 40, A, "SSG 08", WeaponCategory.Rifles, 5),
        new(WeaponId.SG556, "weapon_sg556", 39, T, "SG 553", WeaponCategory.Rifles, 6),
        new(WeaponId.AUG, "weapon_aug", 8, C, "AUG", WeaponCategory.Rifles, 7),
        new(WeaponId.AWP, "weapon_awp", 9, A, "AWP", WeaponCategory.Rifles, 8),
        new(WeaponId.G3SG1, "weapon_g3sg1", 11, T, "G3SG1", WeaponCategory.Rifles, 9),
        new(WeaponId.SCAR20, "weapon_scar20", 38, C, "SCAR-20", WeaponCategory.Rifles, 10),

        new(WeaponId.HEGrenade, "weapon_hegrenade", 44, A, "HE Grenade", WeaponCategory.Grenades, -1),
        new(WeaponId.Flashbang, "weapon_flashbang", 43, A, "Flashbang", WeaponCategory.Grenades, -1),
        new(WeaponId.SmokeGrenade, "weapon_smokegrenade", 45, A, "Smoke Grenade", WeaponCategory.Grenades, -1),
        new(WeaponId.Decoy, "weapon_decoy", 47, A, "Decoy", WeaponCategory.Grenades, -1),
        new(WeaponId.IncGrenade, "weapon_incgrenade", 48, C, "Incendiary", WeaponCategory.Grenades, -1),
        new(WeaponId.Molotov, "weapon_molotov", 46, T, "Molotov", WeaponCategory.Grenades, -1),
        new(WeaponId.TAGrenade, "weapon_tagrenade", 68, A, "TA Grenade", WeaponCategory.Grenades, -1),
        new(WeaponId.Healthshot, "weapon_healthshot", 57, A, "Healthshot", null, -1),
        new(WeaponId.C4, "weapon_c4", 49, T, "C4", null, -1),
        new(WeaponId.BreachCharge, "weapon_breachcharge", 70, A, "Breach Charge", null, -1),
    };

    private static readonly Dictionary<WeaponId, WeaponInfo> ById = All.ToDictionary(w => w.Id);
    private static readonly Dictionary<string, WeaponInfo> ByClass = All.ToDictionary(w => w.Classname, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ushort, WeaponInfo> ByDef = All.ToDictionary(w => w.DefIndex);

    /// <summary>Categories shown by the selection menu, in menu order, with their menu text.</summary>
    public static readonly (WeaponCategory Category, string Text)[] MenuCategories =
    {
        (WeaponCategory.Knife, "Knife/Taser"),
        (WeaponCategory.Pistols, "Pistols"),
        (WeaponCategory.Heavy, "Heavy"),
        (WeaponCategory.Smgs, "SMGs"),
        (WeaponCategory.Rifles, "Rifles"),
    };

    public static WeaponInfo? Get(WeaponId id) => ById.TryGetValue(id, out var w) ? w : null;

    /// <summary>Lookup by classname; accepts "ak47" or "weapon_ak47".</summary>
    public static WeaponInfo? Find(string? classname)
    {
        if (string.IsNullOrWhiteSpace(classname)) return null;
        var s = classname.Trim();
        if (!s.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)) s = "weapon_" + s;
        return ByClass.TryGetValue(s, out var w) ? w : null;
    }

    public static WeaponInfo? FindByDefIndex(ushort defIndex) => ByDef.TryGetValue(defIndex, out var w) ? w : null;

    /// <summary>The menu entries of a category in the original menu order (Knife before KnifeT is collapsed to one item).</summary>
    public static IEnumerable<WeaponInfo> MenuItems(WeaponCategory category) =>
        All.Where(w => w.Category == category && w.Bit >= 0 && w.Id != WeaponId.KnifeT);

    /// <summary>UltJB_Weapons_GetEntNameFromWeaponID.</summary>
    public static string GetEntNameFromWeaponId(WeaponId id) => Get(id)?.Classname ?? "";

    /// <summary>UltJB_Weapons_GetItemDefIndexFromWeaponID (CS2 item definition index; never written to entities).</summary>
    public static ushort GetItemDefIndexFromWeaponId(WeaponId id) => Get(id)?.DefIndex ?? 0;

    /// <summary>UltJB_Weapons_GetWeaponsDefaultTeam: -1 any, 0 none, 2 T, 3 CT.</summary>
    public static int GetWeaponsDefaultTeam(string classname) => Find(classname)?.DefaultTeam ?? TeamAny;

    public static WeaponCategory? GetCategory(string weapon) => Find(weapon)?.Category;

    /// <summary>
    /// Classname of a weapon entity resolved from its item definition index (authoritative), falling back to the
    /// designer name. CS2 reports USP-S / M4A1-S / R8 / MP5-SD / CZ75 under weapon_hkp2000 / weapon_m4a1 /
    /// weapon_deagle / weapon_mp7 / weapon_p250.
    /// </summary>
    public static string ResolveClassname(CBasePlayerWeapon? weapon)
    {
        if (weapon == null || !weapon.IsValid) return "";
        try
        {
            var def = weapon.AttributeManager.Item.ItemDefinitionIndex;
            var w = FindByDefIndex(def);
            if (w != null) return w.Classname;
        }
        catch
        {
            // fall through to the designer name
        }

        return weapon.DesignerName ?? "";
    }

    /// <summary>Is this weapon allowed by the filter (KnifeT follows the "Knife" item).</summary>
    public static bool Allows(WeaponFilter filter, WeaponInfo w)
    {
        if (w.Category == null) return !filter.Disabled.Contains(w.Classname);
        return filter.Allows(w.FilterKey, w.Category.Value) && !filter.Disabled.Contains(w.Classname);
    }

    /// <summary>UltJB_Weapons_GetRandomWeaponFromFlags: uniform pick in table order; the Knife item is knife or knife_t at random.</summary>
    public static WeaponInfo? GetRandom(WeaponFilter filter)
    {
        var allowed = new List<WeaponInfo>();
        foreach (var (cat, _) in MenuCategories)
            foreach (var w in MenuItems(cat))
                if (Allows(filter, w))
                    allowed.Add(w.Id == WeaponId.Knife ? RandomKnife() : w);
        return allowed.Count == 0 ? null : allowed[Random.Shared.Next(allowed.Count)];
    }

    /// <summary>The original re-rolls CSWeapon_KNIFE / CSWeapon_KNIFE_T every time the Knife item is built.</summary>
    public static WeaponInfo RandomKnife() => Get(Random.Shared.Next(2) == 0 ? WeaponId.Knife : WeaponId.KnifeT)!;
}

/// <summary>
/// The original WPN_FLAGS_DISABLE_* bit masks (a set bit disables that weapon). Callers ported from SourcePawn can
/// keep their flag arrays and convert them with <see cref="ToFilter(int[])"/>.
/// </summary>
public static class WeaponSelectionFlags
{
    public const int KnifeKnife = 1 << 0, KnifeTaser = 1 << 1, AllKnives = 0x3;

    public const int PistolGlock = 1 << 0, PistolHkp2000 = 1 << 1, PistolUspSilencer = 1 << 2, PistolElite = 1 << 3,
        PistolP250 = 1 << 4, PistolCz75a = 1 << 5, PistolFiveSeven = 1 << 6, PistolTec9 = 1 << 7, PistolDeagle = 1 << 8,
        PistolRevolver = 1 << 9, AllPistols = 0x3FF;

    public const int HeavyNova = 1 << 0, HeavyXm1014 = 1 << 1, HeavySawedOff = 1 << 2, HeavyMag7 = 1 << 3,
        HeavyM249 = 1 << 4, HeavyNegev = 1 << 5, AllHeavy = 0x3F;

    public const int SmgMac10 = 1 << 0, SmgMp7 = 1 << 1, SmgMp9 = 1 << 2, SmgUmp45 = 1 << 3, SmgP90 = 1 << 4,
        SmgBizon = 1 << 5, SmgMp5sd = 1 << 6, AllSmgs = 0x7F;

    public const int RifleGalilAr = 1 << 0, RifleFamas = 1 << 1, RifleAk47 = 1 << 2, RifleM4a1 = 1 << 3,
        RifleM4a1Silencer = 1 << 4, RifleSsg08 = 1 << 5, RifleSg556 = 1 << 6, RifleAug = 1 << 7, RifleAwp = 1 << 8,
        RifleG3sg1 = 1 << 9, RifleScar20 = 1 << 10, AllRifles = 0x7FF;

    /// <summary>flags[WPN_CAT_*] as in UltJB_Weapons_DisplaySelectionMenu (6 entries; GRENADES is ignored).</summary>
    public static WeaponFilter ToFilter(int[] flags)
    {
        int F(int i) => flags != null && flags.Length > i ? flags[i] : 0;
        return ToFilter(F(0), F(1), F(2), F(3), F(4));
    }

    public static WeaponFilter ToFilter(int knife, int pistols, int heavy, int smgs, int rifles)
    {
        var filter = new WeaponFilter();
        var masks = new Dictionary<WeaponCategory, int>
        {
            [WeaponCategory.Knife] = knife, [WeaponCategory.Pistols] = pistols, [WeaponCategory.Heavy] = heavy,
            [WeaponCategory.Smgs] = smgs, [WeaponCategory.Rifles] = rifles
        };
        foreach (var w in WeaponTable.All)
        {
            if (w.Category == null || w.Bit < 0 || !masks.TryGetValue(w.Category.Value, out var mask)) continue;
            if ((mask & (1 << w.Bit)) != 0) filter.Disabled.Add(w.FilterKey);
        }

        filter.DisabledCategories.Add(WeaponCategory.Grenades); // GRENADES has no bits and is never shown
        return filter;
    }
}
