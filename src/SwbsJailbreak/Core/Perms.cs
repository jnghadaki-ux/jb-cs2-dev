using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;

namespace SwbsJailbreak.Core;

/// <summary>
/// Admin permission checks on top of CounterStrikeSharp's admin system (configs/admins.json, admin_groups.json,
/// admin_overrides.json). The original SMPlugins "Admins" library used numbered admin levels; here every
/// capability is a named permission in the plugin config (General.Permissions) mapped to CSS flags, so a server
/// owner can re-map e.g. "ctban" to "@css/ban" without touching code.
/// Commands should still be decorated with [RequiresPermissions] where a fixed flag is fine; CSS lets owners
/// override those per command in admin_overrides.json.
/// </summary>
public static class Perms
{
    /// <summary>Default capability → CSS flag map (written into the config on first run).</summary>
    public static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = "@css/generic",          // basic admin: see admin chat, logs, use !admin menu
        ["warden.manage"] = "@css/generic",  // force/remove warden
        ["cells"] = "@css/root",             // sm_celldoors_open / sm_cdo (original: ADMFLAG_ROOT)
        ["cells.edit"] = "@css/root",        // sm_celldoors_edit + save (original: ADMFLAG_ROOT)
        ["cells.open"] = "@css/generic",
        ["crews.admin"] = "@css/ban",        // !crewadmin: rename / retag / kick / disband crews, set respect     // !open / !o (addition: any basic admin may open the cells)
        ["ctban"] = "@css/ban",              // CT ban/unban
        ["ctban.permanent"] = "@css/unban",  // permanent CT bans / unban others' bans
        ["days"] = "@css/generic",           // force-start / stop days
        ["days.edit"] = "@css/unban",        // sm_de / sm_daysedit (original: ADMFLAG_UNBAN)
        ["lr"] = "@css/generic",             // abort LRs
        ["mute"] = "@css/chat",              // mute/gag/silence
        ["teams"] = "@css/kick",             // move players between teams, swap
        ["slay"] = "@css/slay",              // slay, slap, beacon, freeze, ... (fun commands)
        ["fun"] = "@css/cheats",             // gravity, noclip, god, give weapons, speed
        ["respawn"] = "@css/cheats",
        ["rcon"] = "@css/rcon",
        ["root"] = "@css/root",
        ["immunity"] = "@css/generic",       // immune to dead-talk mute etc.
        ["vip"] = "@css/reservation",        // donator / VIP perks (old SMPlugins "Donators")
        ["logs"] = "@css/generic",           // view jail logs at any time
        ["zones"] = "@css/config",           // edit map zones / LR locations
        ["kick"] = "@css/kick",
        ["ban"] = "@css/ban",
        ["unban"] = "@css/unban",
        ["map"] = "@css/changemap",
        ["cvar"] = "@css/cvar",
        ["config"] = "@css/config",
        ["chat"] = "@css/chat",
    };

    /// <summary>Check a named capability (see <see cref="Defaults"/>) or a raw CSS flag / group ("@css/ban", "#css/admin").</summary>
    public static bool Has(CCSPlayerController? player, string capabilityOrFlag)
    {
        if (player == null) return true; // server console
        if (!player.IsValid || player.IsBot) return false;
        var flag = Resolve(capabilityOrFlag);
        if (string.IsNullOrEmpty(flag)) return true;
        try
        {
            if (flag.StartsWith('#')) return AdminManager.PlayerInGroup(player, flag);
            return AdminManager.PlayerHasPermissions(player, flag) || AdminManager.PlayerHasPermissions(player, "@css/root");
        }
        catch
        {
            return false;
        }
    }

    public static bool Has(JbPlayer? player, string capabilityOrFlag) => player?.Controller is { } c && Has(c, capabilityOrFlag);

    public static bool IsAdmin(CCSPlayerController? player) => Has(player, "admin");

    public static string Resolve(string capabilityOrFlag)
    {
        if (capabilityOrFlag.StartsWith('@') || capabilityOrFlag.StartsWith('#')) return capabilityOrFlag;
        var map = JailbreakPlugin.Instance?.Config.General.Permissions;
        if (map != null && map.TryGetValue(capabilityOrFlag, out var v)) return v;
        return Defaults.TryGetValue(capabilityOrFlag, out var d) ? d : "@css/root";
    }

    /// <summary>CSS immunity comparison: can <paramref name="admin"/> target <paramref name="target"/>?</summary>
    public static bool CanTarget(CCSPlayerController? admin, CCSPlayerController target)
    {
        if (admin == null) return true;
        if (admin == target) return true;
        try
        {
            return AdminManager.CanPlayerTarget(admin, target);
        }
        catch
        {
            return true;
        }
    }
}
