using CounterStrikeSharp.API.Core;

namespace Swbs.Common;

/// <summary>What a variation changes, shown in the vote and the intro.</summary>
public enum VariationKind
{
    /// <summary>Everyone gets the same special weapons (the mode's loadout is replaced).</summary>
    Loadout,

    /// <summary>A rule on top of the mode's own loadout (gravity, healing, health ...).</summary>
    Mutator,

    /// <summary>A goal that changes how the round is won (bounties, a juggernaut, lives).</summary>
    Objective
}

/// <summary>
/// One round variation (the round version of a SWBS Jailbreak day). Built-ins live in Rounds/Variations; a game mode can
/// register its own with <see cref="RoundsModule.Register"/>. Hooks run only while the variation is the current round.
/// </summary>
public abstract class RoundVariation
{
    /// <summary>Stable id used in the config and commands ("rpg").</summary>
    public abstract string Key { get; }

    public abstract VariationKind Kind { get; }

    /// <summary>Game mode tags it suits ("dm", "surf"); the mode lists the tags it runs in CommonHost.RoundTags.</summary>
    public virtual IReadOnlyCollection<string> Tags { get; } = new[] { "dm", "surf" };

    public virtual int MinPlayers => 2;

    /// <summary>True when this variation hands out every weapon itself (the mode's !guns loadout is skipped).</summary>
    public virtual bool OwnsLoadout => Kind == VariationKind.Loadout;

    /// <summary>The mode's own kill rewards (health and clip refills) still apply. Off for loadout rounds.</summary>
    public virtual bool AllowModeRefills => Kind != VariationKind.Loadout;

    public string Name => Lang.T($"rounds.v.{Key}.name");
    public string Description => Lang.T($"rounds.v.{Key}.desc");

    public RoundsModule Rounds { get; internal set; } = null!;

    protected RoundTuning Tune => Rounds.Tuning;

    /// <summary>Extra conditions (e.g. a zone or a minimum of alive players).</summary>
    public virtual bool CanRun() => true;

    public virtual void Start() { }
    public virtual void End() { }

    /// <summary>A player spawned during the round (after the mode's own spawn handling).</summary>
    public virtual void Spawn(CCSPlayerController c) { }

    public virtual void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev) { }

    /// <summary>Change or block damage (DamageService, priority 95).</summary>
    public virtual HookResult Damage(DamageContext ctx) => HookResult.Continue;

    public virtual void Tick(float now) { }

    /// <summary>Once a second.</summary>
    public virtual void Second() { }

    public virtual void GrenadeThrown(CCSPlayerController c, string weapon) { }

    public virtual void WeaponFire(CCSPlayerController c, string weapon) { }

    /// <summary>A line of the HUD panel for this player (their objective state), or null.</summary>
    public virtual string? HudLine(CCSPlayerController c) => null;

    /// <summary>False keeps a dead player out until the round ends (Last Man Standing).</summary>
    public virtual bool AllowRespawn(SwbsPlayer p) => true;

    /// <summary>Score for a kill (the round's scoreboard decides the MVP). Default 1.</summary>
    public virtual int KillScore(SwbsPlayer victim, SwbsPlayer attacker, EventPlayerDeath ev) => 1;

    // ---- helpers for variations

    protected static void Strip(CCSPlayerController c)
    {
        try { c.RemoveWeapons(); }
        catch { /* pawn gone */ }
    }

    protected static string KnifeFor(CCSPlayerController c) => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t";

    protected static void SelectSlot(CCSPlayerController c, string slot) =>
        CounterStrikeSharp.API.Server.NextFrame(() => { if (c.IsValid && c.IsAliveP()) c.ExecuteClientCommand(slot); });

    protected static void SetClip(CBasePlayerWeapon? w, int clip, int? reserve = null)
    {
        if (w == null || !w.IsValid) return;
        w.Clip1 = clip;
        CounterStrikeSharp.API.Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        if (reserve == null) return;
        w.ReserveAmmo[0] = reserve.Value;
        CounterStrikeSharp.API.Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    protected static CBasePlayerWeapon? GiveWeapon(CCSPlayerController c, string weapon)
    {
        c.Give(weapon);
        return c.Weapons().FirstOrDefault(w => w.DesignerName == weapon || weapon == "weapon_usp_silencer" && w.DesignerName == "weapon_hkp2000");
    }
}

/// <summary>What the game modes ask the rounds system (their loadout and respawn code checks this).</summary>
public static class Rounds
{
    /// <summary>The running variation, or null in a normal round / when the system is off.</summary>
    public static RoundVariation? Current => RoundsModule.Instance?.Active;

    /// <summary>The mode's spawn loadout must not be given: the variation hands out the weapons.</summary>
    public static bool OwnsLoadout => Current?.OwnsLoadout == true;

    public static bool AllowRespawn(SwbsPlayer p) => Current?.AllowRespawn(p) ?? true;

    /// <summary>The mode may give its kill rewards (DM health / clip refills).</summary>
    public static bool AllowModeRefills => Current?.AllowModeRefills ?? true;

    /// <summary>The mode's own random round modifiers (DM no-scope rounds) stay off during a variation round.</summary>
    public static bool VariationRound => Current != null;
}
