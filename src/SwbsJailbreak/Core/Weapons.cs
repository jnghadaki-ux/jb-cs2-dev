using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>Weapon catalog (CS2 designer names) used by LRs, days and weapon selection menus.</summary>
public static class Weapons
{
    public static readonly string[] Pistols =
    {
        "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_p250", "weapon_fiveseven",
        "weapon_tec9", "weapon_cz75a", "weapon_deagle", "weapon_revolver", "weapon_elite"
    };

    public static readonly string[] Smgs =
    {
        "weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_p90", "weapon_bizon"
    };

    public static readonly string[] Rifles =
    {
        "weapon_galilar", "weapon_famas", "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_sg556", "weapon_aug"
    };

    public static readonly string[] Snipers = { "weapon_ssg08", "weapon_awp", "weapon_g3sg1", "weapon_scar20" };

    public static readonly string[] Heavy = { "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7", "weapon_m249", "weapon_negev" };

    public static readonly string[] Grenades =
    {
        "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_decoy", "weapon_molotov", "weapon_incgrenade"
    };

    public static readonly string[] Primaries = Smgs.Concat(Rifles).Concat(Snipers).Concat(Heavy).ToArray();

    public static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"] = "Glock-18", ["weapon_usp_silencer"] = "USP-S", ["weapon_hkp2000"] = "P2000", ["weapon_p250"] = "P250",
        ["weapon_fiveseven"] = "Five-SeveN", ["weapon_tec9"] = "Tec-9", ["weapon_cz75a"] = "CZ75-Auto", ["weapon_deagle"] = "Desert Eagle",
        ["weapon_revolver"] = "R8 Revolver", ["weapon_elite"] = "Dual Berettas", ["weapon_mac10"] = "MAC-10", ["weapon_mp9"] = "MP9",
        ["weapon_mp7"] = "MP7", ["weapon_mp5sd"] = "MP5-SD", ["weapon_ump45"] = "UMP-45", ["weapon_p90"] = "P90", ["weapon_bizon"] = "PP-Bizon",
        ["weapon_galilar"] = "Galil AR", ["weapon_famas"] = "FAMAS", ["weapon_ak47"] = "AK-47", ["weapon_m4a1"] = "M4A4",
        ["weapon_m4a1_silencer"] = "M4A1-S", ["weapon_sg556"] = "SG 553", ["weapon_aug"] = "AUG", ["weapon_ssg08"] = "SSG 08 (Scout)",
        ["weapon_awp"] = "AWP", ["weapon_g3sg1"] = "G3SG1", ["weapon_scar20"] = "SCAR-20", ["weapon_nova"] = "Nova", ["weapon_xm1014"] = "XM1014",
        ["weapon_sawedoff"] = "Sawed-Off", ["weapon_mag7"] = "MAG-7", ["weapon_m249"] = "M249", ["weapon_negev"] = "Negev",
        ["weapon_hegrenade"] = "HE Grenade", ["weapon_flashbang"] = "Flashbang", ["weapon_smokegrenade"] = "Smoke Grenade",
        ["weapon_decoy"] = "Decoy", ["weapon_molotov"] = "Molotov", ["weapon_incgrenade"] = "Incendiary", ["weapon_taser"] = "Zeus x27",
        ["weapon_knife"] = "Knife", ["weapon_healthshot"] = "Healthshot"
    };

    public static string Display(string designer) =>
        DisplayNames.TryGetValue(designer, out var n) ? n : designer.Replace("weapon_", "");

    public static bool IsKnife(string designer) =>
        designer.Contains("knife", StringComparison.OrdinalIgnoreCase) || designer.Contains("bayonet", StringComparison.OrdinalIgnoreCase);

    public static bool IsGrenade(string designer) => Grenades.Contains(designer) || designer.EndsWith("_projectile") || designer == "inferno";

    public static bool IsPistol(string designer) => Pistols.Contains(designer);

    public static bool IsPrimary(string designer) => Primaries.Contains(designer);

    /// <summary>Normalize user input ("ak47", "weapon_ak47", "AK-47") to a designer name, or null.</summary>
    public static string? Parse(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (!s.StartsWith("weapon_")) s = "weapon_" + s;
        if (DisplayNames.ContainsKey(s)) return s;
        var byDisplay = DisplayNames.FirstOrDefault(kv => string.Equals(kv.Value, input, StringComparison.OrdinalIgnoreCase));
        return byDisplay.Key;
    }
}

/// <summary>
/// Weapon pickup / drop rules. The CanAcquire hook is only installed while at least one pickup rule exists
/// (same pattern working CS2 jailbreaks use) to keep the hot path clean.
/// A rule returns false to block. Rules are identified by owner so modules can remove theirs.
/// </summary>
public static class WeaponService
{
    public delegate bool PickupRule(CCSPlayerController player, string weapon, AcquireMethod method);

    public delegate bool DropRule(CCSPlayerController player, CBasePlayerWeapon? weapon);

    private static readonly Dictionary<string, PickupRule> PickupRules = new();

    /// <summary>
    /// The CEconItemView being acquired, valid only while pickup rules run. For a ground pickup it is the weapon
    /// entity's own item (AttributeManager.Item), which lets a rule find out which weapon entity is being picked up.
    /// </summary>
    public static nint CurrentItem { get; private set; }
    private static readonly Dictionary<string, DropRule> DropRules = new();
    private static bool _hooked;

    public static void SetPickupRule(string owner, PickupRule rule)
    {
        PickupRules[owner] = rule;
        UpdateHook();
    }

    public static void RemovePickupRule(string owner)
    {
        if (PickupRules.Remove(owner)) UpdateHook();
    }

    public static void SetDropRule(string owner, DropRule rule) => DropRules[owner] = rule;

    public static void RemoveDropRule(string owner) => DropRules.Remove(owner);

    internal static void Clear()
    {
        PickupRules.Clear();
        DropRules.Clear();
        UpdateHook();
    }

    private static void UpdateHook()
    {
        try
        {
            if (PickupRules.Count > 0 && !_hooked)
            {
                VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);
                _hooked = true;
            }
            else if (PickupRules.Count == 0 && _hooked)
            {
                VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre);
                _hooked = false;
            }
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "CanAcquire hook toggle failed");
        }
    }

    private static HookResult OnCanAcquire(DynamicHook hook)
    {
        try
        {
            if (PickupRules.Count == 0) return HookResult.Continue;
            var method = hook.GetParam<AcquireMethod>(2);
            var services = hook.GetParam<CCSPlayer_ItemServices>(0);
            var player = services.Pawn.Value?.Controller.Value?.As<CCSPlayerController>();
            if (player == null || !player.IsValid) return HookResult.Continue;
            var item = hook.GetParam<CEconItemView>(1);
            var data = VirtualFunctions.GetCSWeaponDataFromKey(-1, item.ItemDefinitionIndex.ToString());
            var name = data?.Name ?? "";
            if (string.IsNullOrEmpty(name)) return HookResult.Continue;
            CurrentItem = item.Handle;
            try
            {
                foreach (var rule in PickupRules.Values.ToArray())
                {
                    if (rule(player, name, method)) continue;
                    hook.SetReturn(AcquireResult.NotAllowedByMode);
                    return HookResult.Handled;
                }
            }
            finally
            {
                CurrentItem = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "CanAcquire rule failed");
        }

        return HookResult.Continue;
    }

    /// <summary>"drop" command listener (registered by the core).</summary>
    internal static HookResult OnDropCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || DropRules.Count == 0) return HookResult.Continue;
        var weapon = player.ActiveWeapon();
        foreach (var rule in DropRules.Values.ToArray())
        {
            try
            {
                if (!rule(player, weapon)) return HookResult.Handled;
            }
            catch (Exception ex)
            {
                JailbreakPlugin.Instance?.Logger.LogError(ex, "Drop rule failed");
            }
        }

        return HookResult.Continue;
    }
}
