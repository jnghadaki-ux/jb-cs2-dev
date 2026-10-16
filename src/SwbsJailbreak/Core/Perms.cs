using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;

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
        ["warden.beacon"] = "@css/config",   // (Lead tier and root) !wbeacon: warden pointer tracer and beacon colour / size
        ["cells"] = "@css/root",             // sm_celldoors_open / sm_cdo (original: ADMFLAG_ROOT)
        ["cells.edit"] = "@css/root",        // sm_celldoors_edit + save (original: ADMFLAG_ROOT)
        ["cells.open"] = "@css/generic",
        ["crews.admin"] = "@css/kick",       // !crewadmin: rename / retag / kick / disband crews, set respect     // !open / !o (addition: any basic admin may open the cells)
        ["ctban"] = "@css/kick",             // timed CT bans (Level 2+)
        ["ctban.permanent"] = "@css/unban",  // permanent CT bans / unban others' bans
        ["days"] = "@css/generic",           // force-start / stop days
        ["days.edit"] = "@css/unban",        // sm_de / sm_daysedit (original: ADMFLAG_UNBAN)
        ["lr"] = "@css/generic",             // abort LRs
        ["lr.edit"] = "@css/unban",          // !lredit: turn last requests on/off (like days.edit)
        ["mute"] = "@css/chat",              // mute/gag/silence
        ["teams"] = "@css/kick",             // move players between teams, swap
        ["slay"] = "@css/slay",              // slay, slap, beacon, freeze, ... (fun commands)
        ["fun"] = "@css/cheats",             // gravity, give weapons, speed, hp/armor (Level 3)
        ["noclip"] = "@swbs/noclip",         // !noclip (Lead tier and root)
        ["god"] = "@swbs/god",               // !god (Lead tier and root)
        ["respawn"] = "@css/slay",           // !respawn, !1up, !xup (Level 1 and up)
        ["teleport"] = "@css/cheats",        // !bring, !goto (Level 3 and up)
        ["gravity"] = "@css/cheats",         // !gravity (Level 3 and up)
        ["bhc"] = "@css/unban",              // !bhc / !bhce bhop logs (Level 3 and up)
        ["rcon"] = "@css/rcon",
        ["root"] = "@css/root",
        ["immunity"] = "@css/generic",       // immune to dead-talk mute etc.
        ["vip"] = "@css/reservation",        // donator / VIP perks (old SMPlugins "Donators")
        ["logs"] = "@css/generic",           // view jail logs at any time
        ["games.admin"] = "@css/generic",    // warden games: force-end, mark map game buttons (!gamebuttons)
        ["zones"] = "@css/config",           // edit map zones / LR locations
        ["kick"] = "@css/kick",
        ["ban"] = "@css/ban",                // server bans: !ban, !banip, !addban, !banlist (Level 3+)
        ["unban"] = "@swbs/unban",           // !unban and !unctban (Lead tier and root)
        ["map"] = "@css/changemap",
        ["cvar"] = "@css/cvar",
        ["config"] = "@css/config",
        ["chat"] = "@css/chat",
        ["points"] = "@css/root",            // !givepoints / !takepoints / !setpoints (testing the store)
    };

    /// <summary>Check a named capability (see <see cref="Defaults"/>) or a raw CSS flag / group ("@css/ban", "#css/admin").</summary>
    public static bool Has(CCSPlayerController? player, string capabilityOrFlag)
    {
        if (player == null) return true; // server console
        var flag = Resolve(capabilityOrFlag);
        if (string.IsNullOrEmpty(flag)) return player.IsValid && !player.IsBot;
        return HasFlag(player, flag);
    }

    /// <summary>
    /// The SteamID used for admin lookups. CSS's own checks use only the Steam-verified ID, which stays empty when Steam
    /// never validates the player (a server started with -insecure or without a GSLT), so admins.json never matches.
    /// General.TrustUnverifiedSteamIds (test servers only, default off) falls back to the unverified ID.
    /// </summary>
    public static SteamID? AdminId(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return null;
        try
        {
            var verified = player.AuthorizedSteamID;
            if (verified != null) return verified;
            var trust = JailbreakPlugin.Instance?.Config.General.TrustUnverifiedSteamIds ?? false;
            return trust && player.SteamID != 0 ? new SteamID(player.SteamID) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the player's admin identity comes from the unverified fallback.</summary>
    public static bool IsUnverified(CCSPlayerController player)
    {
        try { return player.AuthorizedSteamID == null; }
        catch { return true; }
    }

    /// <summary>Raw CSS flag ("@css/ban") or group ("#css/admin") check; @css/root passes every flag. Console: true.</summary>
    public static bool HasFlag(CCSPlayerController? player, string flag)
    {
        if (player == null) return true;
        var id = AdminId(player);
        if (id == null) return false;
        try
        {
            if (flag.StartsWith('#')) return AdminManager.PlayerInGroup(id, flag) || AdminLevels.TierImplies(TierOf(id), flag);
            if (AdminManager.PlayerHasPermissions(id, flag) || AdminManager.PlayerHasPermissions(id, "@css/root")) return true;
            // SWBS tiers are a ladder: a tier member has every flag of their tier and the tiers below, even when
            // admin_groups.json is old, was edited, or has not been reloaded yet.
            return AdminLevels.TierGrants(TierOf(id), flag);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The admin's SWBS tier: by tier group, or by the tier flags they were given directly (the higher one).</summary>
    private static int TierOf(SteamID id) => Math.Max(
        AdminLevels.TierOf(g =>
        {
            try { return AdminManager.PlayerInGroup(id, g); }
            catch { return false; }
        }),
        AdminLevels.TierOfFlags(f =>
        {
            try { return AdminManager.PlayerHasPermissions(id, f); }
            catch { return false; }
        }));

    public static AdminData? AdminDataOf(CCSPlayerController player)
    {
        var id = AdminId(player);
        try { return id == null ? null : AdminManager.GetPlayerAdminData(id); }
        catch { return null; }
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
            var targetId = AdminId(target);
            if (targetId == null || AdminManager.CanPlayerTarget(AdminId(admin), targetId)) return true;
            // CSS compares only the immunity numbers in admins.json / admin_groups.json: a root admin or an admin given
            // tier flags directly usually has 0 and could not touch a tier admin. Ranks decide: root > Lead > 3 > 2 > 1.
            var adminId = AdminId(admin);
            return adminId != null && AdminLevels.EffectiveImmunity(Immunity(adminId), IsRoot(adminId), TierOf(adminId))
                >= AdminLevels.EffectiveImmunity(Immunity(targetId), IsRoot(targetId), TierOf(targetId));
        }
        catch
        {
            return true;
        }
    }

    private static uint Immunity(SteamID id)
    {
        try { return AdminManager.GetPlayerImmunity(id); }
        catch { return 0; }
    }

    private static bool IsRoot(SteamID id)
    {
        try { return AdminManager.PlayerHasPermissions(id, "@css/root"); }
        catch { return false; }
    }
}

/// <summary>
/// The command is for everyone: its body checks admin rights only for an extra (admins may look up anyone's points,
/// admins also receive !g) or the game rules decide (warden, deputy, prisoner). Listed as open in !commands.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OpenCommandAttribute : Attribute { }
