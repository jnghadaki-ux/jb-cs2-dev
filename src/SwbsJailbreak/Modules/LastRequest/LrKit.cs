using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "LastRequestsExtra": the last requests added in 0.15.</summary>
public sealed class LrExtraConfig
{
    // ---------------------------------------------------------------- duels
    public bool DodgeballEnabled { get; set; } = true;
    public bool HeadshotDuelEnabled { get; set; } = true;
    public bool GrenadeWarEnabled { get; set; } = true;
    public bool TaserDuelEnabled { get; set; } = true;
    public bool QuickDrawEnabled { get; set; } = true;
    public bool GunRouletteEnabled { get; set; } = true;

    /// <summary>Guns the roulette draws from.</summary>
    public List<string> GunRoulettePool { get; set; } = new()
    {
        "weapon_deagle", "weapon_revolver", "weapon_ssg08", "weapon_awp", "weapon_nova", "weapon_mag7",
        "weapon_ak47", "weapon_m4a1_silencer", "weapon_p250", "weapon_xm1014"
    };

    /// <summary>Guns per roulette (each gives both players one shot). After the last one the healthier player wins.</summary>
    public int GunRouletteGuns { get; set; } = 5;

    /// <summary>Seconds a shooter has to take their shot before the turn passes.</summary>
    public int GunRouletteTurnSeconds { get; set; } = 8;

    // ---------------------------------------------------------------- contests
    public bool HotPotatoEnabled { get; set; } = true;
    public int HotPotatoMinSeconds { get; set; } = 15;
    public int HotPotatoMaxSeconds { get; set; } = 30;
    public bool HideAndSeekEnabled { get; set; } = true;
    public int HideSeconds { get; set; } = 20;
    public int SeekSeconds { get; set; } = 60;
    public bool LongestJumpEnabled { get; set; } = true;
    public int LongestJumpAttempts { get; set; } = 3;
    public bool ChickenHuntEnabled { get; set; } = true;
    public int ChickenCount { get; set; } = 12;
    public int ChickenHuntSeconds { get; set; } = 30;
    public bool TargetPracticeEnabled { get; set; } = true;
    public int TargetShots { get; set; } = 10;

    // ---------------------------------------------------------------- mind games
    public bool RockPaperScissorsEnabled { get; set; } = true;
    public bool MathRaceEnabled { get; set; } = true;
    public bool TypeRaceEnabled { get; set; } = true;
    public bool SimonSaysEnabled { get; set; } = true;

    /// <summary>Words for Type Race.</summary>
    public List<string> TypeRaceWords { get; set; } = new()
    {
        "jailbreak", "warden", "contraband", "freeday", "armory", "lockdown", "shank", "rebellion", "solitary",
        "cellblock", "handcuffs", "escape", "parole", "warday", "guardian", "prisoner", "uprising", "shakedown"
    };

    // ---------------------------------------------------------------- rebel / freeday
    public bool RocketeerEnabled { get; set; } = true;
    public int RocketeerHealth { get; set; } = 200;
    public bool TeleporterEnabled { get; set; } = true;
    public int TeleporterHealth { get; set; } = 150;
    public float TeleporterCooldown { get; set; } = 5f;
    public float TeleporterRange { get; set; } = 1500f;

    /// <summary>Played from the Teleporter after each jump (workshop pack teleport sound).</summary>
    public string TeleporterSound { get; set; } = "";
    public bool JetpackFreedayEnabled { get; set; } = true;
}

/// <summary>Small helpers shared by the 0.15 last requests.</summary>
internal static class LrKit
{
    public static JbPlayer? Opp(JbPlayer p) => Jb.LastRequests.GetOpponent(p);

    /// <summary>The prisoner whose LR this player is in (themselves for a prisoner, the prisoner for a guard opponent).</summary>
    public static JbPlayer? Owner(JbPlayer p) =>
        p.Controller?.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist ? p : Jb.LastRequests.GetOpponent(p);

    public static void Tell(JbPlayer? p, string key, params object?[] args) => Chat.Tell(p, key, args);

    public static void TellBoth(JbPlayer a, JbPlayer? b, string key, params object?[] args)
    {
        Chat.Tell(a, key, args);
        if (b != null) Chat.Tell(b, key, args);
    }

    public static void HudBoth(JbPlayer a, JbPlayer? b, string key, string html, int priority = 45, float seconds = 1.5f)
    {
        Hud.Set(a.Controller, key, html, priority, seconds);
        if (b != null) Hud.Set(b.Controller, key, html, priority, seconds);
    }

    /// <summary>The loser dies with the kill credited to the winner (LR stats and the kill feed see the winner).</summary>
    public static void Win(JbPlayer winner, JbPlayer loser)
    {
        if (loser.Controller is { IsValid: true } lc && lc.IsAliveP()) DamageService.KillWithCredit(lc, winner.Controller);
    }

    public static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    public static void Select(JbPlayer p, string weapon)
    {
        var slot = Weapons.IsKnife(weapon) ? "slot3" : Weapons.IsPistol(weapon) ? "slot2" : Weapons.IsGrenade(weapon) ? "slot4" : "slot1";
        if (weapon == "weapon_taser") slot = "slot3";
        var c = p.Controller;
        Server.NextFrame(() =>
        {
            if (c is { IsValid: true } && c.IsAliveP()) c.ExecuteClientCommand(slot);
        });
    }

    public static CBasePlayerWeapon? Give(JbPlayer p, string weapon, bool select = true)
    {
        var w = Jb.WeaponSelection.GiveWeapon(p, weapon);
        if (select) Select(p, weapon);
        return w;
    }

    public static void SetClip(CBasePlayerWeapon? w, int clip, int reserve = 0)
    {
        if (w == null || !w.IsValid) return;
        w.Clip1 = clip;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        w.ReserveAmmo[0] = reserve;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    public static bool Alive(JbPlayer? p) => p?.Controller is { IsValid: true } c && c.IsAliveP();
}
