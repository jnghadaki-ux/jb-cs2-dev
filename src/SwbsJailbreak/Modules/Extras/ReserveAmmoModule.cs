using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "ReserveAmmo" (SMPlugins UnlimitedReserveAmmo.sp).</summary>
public sealed class ReserveAmmoConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Reserve ammo set on every reload (the original used 500).</summary>
    public int ReserveAmount { get; set; } = 500;

    /// <summary>Which teams get it: "both", "guards" or "prisoners".</summary>
    public string Teams { get; set; } = "both";
}

/// <summary>
/// Port of UnlimitedReserveAmmo.sp: on every reload of a gun (not grenades) the reserve is topped up to ReserveAmount.
/// Never for players in a last request and never during days, which control ammo themselves (Mag For Mag, Shot For
/// Shot, One In The Chamber...).
/// </summary>
public sealed class ReserveAmmoModule : JbModule
{
    private ReserveAmmoConfig Cfg => Section<ReserveAmmoConfig>("ReserveAmmo");

    public override string Name => "ReserveAmmo";
    public override int Order => 118;
    public override bool Enabled => Cfg.Enabled;

    /// <summary>Engine-free (unit-tested): does this team get unlimited reserve ammo?</summary>
    internal static bool TeamAllowed(string teams, bool guard) => (teams ?? "both").Trim().ToLowerInvariant() switch
    {
        "guards" or "ct" => guard,
        "prisoners" or "t" => !guard,
        "none" or "off" => false,
        _ => true
    };

    [GameEventHandler]
    public HookResult OnWeaponReload(EventWeaponReload ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || !c.IsAliveP()) return HookResult.Continue;
        if (Jb.Days.IsInProgress) return HookResult.Continue;
        var p = c.Jb();
        if (p == null || Jb.LastRequests.IsInLastRequest(p)) return HookResult.Continue;
        if (!TeamAllowed(Cfg.Teams, c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist)) return HookResult.Continue;

        var w = c.ActiveWeapon();
        if (w == null || !w.IsValid || Weapons.IsGrenade(w.DesignerName) || Weapons.IsKnife(w.DesignerName)) return HookResult.Continue;
        w.SetAmmo(-1, Math.Max(0, Cfg.ReserveAmount));
        return HookResult.Continue;
    }
}
