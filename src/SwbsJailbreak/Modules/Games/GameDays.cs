using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Days;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Games;

public sealed class GameDaysConfig
{
    public bool HideAndSeekEnabled { get; set; } = true;
    /// <summary>HideAndSeek hns_hide_time: seconds the hiders have before the seekers are released.</summary>
    public int HideSeconds { get; set; } = 15;
    public int HiderFlashbangs { get; set; } = 1;

    public bool JetpackEnabled { get; set; } = true;
    /// <summary>Upward speed added per tick while jump is held in the air (u/s), and the climb speed cap.</summary>
    public float JetpackThrust { get; set; } = 22f;
    public float JetpackMaxUp { get; set; } = 350f;
    public float JetpackFuelSeconds { get; set; } = 4f;
    /// <summary>Fuel seconds regained per second on the ground.</summary>
    public float JetpackRegen { get; set; } = 1f;

    /// <summary>NoScopeRounds noscope_chance: % chance (0–100) that a normal round is no-scope only. 0 = off.</summary>
    public int NoScopeChance { get; set; } = 0;
}

/// <summary>
/// Days from SMPlugins' other modes (hlstriker): Hide &amp; Seek (Mods/HideAndSeek), Jetpack (Plugins/Jetpacks) and the
/// no-scope round chance (Plugins/NoScopeRounds). Design: docs/design/MINIGAMES_SUITE_DESIGN.md §5.
/// </summary>
public sealed class GameDaysModule : JbModule
{
    private GameDaysConfig Cfg => Section<GameDaysConfig>("GameDays");

    public override string Name => "Games.Days";
    public override int Order => 16;

    private static readonly string[] ScopedWeapons = { "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1", "weapon_aug", "weapon_sg556" };

    private DayDefinition? _hns, _jetpack;
    private readonly float[] _fuel = new float[Players.MaxSlots];
    private bool _noScopeRound;
    private int _tick;

    private static bool Active(DayDefinition? d) => d != null && ReferenceEquals(Jb.Days.Current, d);

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule days) return;

        if (Cfg.HideAndSeekEnabled)
        {
            _hns = new DayDefinition
            {
                Name = "Hide and Seek", Type = DayType.Warday,
                Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying,
                Description = "Prisoners hide while the guards are blind; the guards then hunt them with knives.",
                FreezeTime = Math.Clamp(Cfg.HideSeconds, 3, 120),
                FreezeTeams = FreezeTeams.Guards,
                OnStart = _ => HnsStart(),
                OnFreezeEnd = HnsRelease,
                OnEnd = _ => HnsEnd()
            };
            days.Register(_hns);
        }

        if (Cfg.JetpackEnabled)
        {
            _jetpack = new DayDefinition
            {
                Name = "Jetpack", Type = DayType.Freeday, Flags = DayFlags.None,
                Description = "Hold jump in the air to fly; fuel refills on the ground.",
                OnStart = _ =>
                {
                    Array.Fill(_fuel, Cfg.JetpackFuelSeconds);
                    Chat.TellAll("games.jetpack.how");
                },
                OnEnd = _ => Hud.ClearPrefix("games.jetpack")
            };
            days.Register(_jetpack);
        }

        DamageService.Register(Name, 95, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        if (Active(_hns)) HnsEnd();
    }

    public override void OnRoundStart()
    {
        _noScopeRound = Cfg.NoScopeChance > 0 && Random.Shared.Next(100) < Math.Clamp(Cfg.NoScopeChance, 0, 100);
        if (_noScopeRound) Chat.TellAll("games.noscope.round");
    }

    // ====================================================================== Hide & Seek

    private void HnsStart()
    {
        foreach (var g in DayUtil.AliveOn(CsTeam.CounterTerrorist))
        {
            DayUtil.Give(g, "weapon_knife", true);
            // The original faded the seekers' screens black for the hide time (FFADE_STAYOUT).
            if (g.Controller is { } c) Fx.Fade(c, Color.FromArgb(255, 0, 0, 0), 0f, 0f, Fx.FadeStayOut | Fx.FadePurge);
        }

        foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist))
        {
            DayUtil.Give(p, "weapon_knife_t", true);
            for (var i = 0; i < Math.Clamp(Cfg.HiderFlashbangs, 0, 2); i++) DayUtil.Give(p, "weapon_flashbang");
        }

        Chat.TellAll("games.hns.start", Math.Clamp(Cfg.HideSeconds, 3, 120));
    }

    private void HnsRelease()
    {
        // Seekers see again (the original faded in over 3 s).
        foreach (var c in Players.Guards()) Fx.Fade(c, Color.FromArgb(255, 0, 0, 0), 3f, 0f, Fx.FadeIn | Fx.FadePurge);
        Chat.TellAll("games.hns.released");
    }

    private void HnsEnd()
    {
        foreach (var c in Players.Humans()) Fx.ClearFade(c);
    }

    // ====================================================================== shared

    /// <summary>Hide &amp; Seek: the hiders' knives cannot hurt anyone (the original froze their attack times).</summary>
    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active(_hns) || ctx.Attacker == null || ctx.IsSelf) return HookResult.Continue;
        return ctx.Attacker.Team == CsTeam.Terrorist ? HookResult.Handled : HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Active(_hns)) return HookResult.Continue;
        Server.NextFrame(() =>
        {
            if (!Active(_hns) || !Round.IsActive) return;
            if (Players.CountAlive(CsTeam.Terrorist) == 0) { Chat.TellAll("games.hns.seekers_win"); Round.End(CsTeam.CounterTerrorist); }
            else if (Players.CountAlive(CsTeam.CounterTerrorist) == 0) { Chat.TellAll("games.hns.hiders_win"); Round.End(CsTeam.Terrorist); }
        });
        return HookResult.Continue;
    }

    private void OnTick()
    {
        _tick++;
        if (Active(_jetpack)) JetpackTick();
        if (_noScopeRound && !Jb.Days.IsInProgress) NoScopeTick();
    }

    // ====================================================================== Jetpack

    private void JetpackTick()
    {
        var dt = Server.TickInterval;
        var hud = _tick % 16 == 0;
        foreach (var c in Players.Alive())
        {
            var pawn = c.PawnOrNull();
            if (pawn == null || pawn.MoveType != MoveType_t.MOVETYPE_WALK) continue;
            var slot = c.Slot;
            if (c.OnGround())
            {
                _fuel[slot] = Math.Min(Cfg.JetpackFuelSeconds, _fuel[slot] + Cfg.JetpackRegen * dt);
            }
            else if ((c.Buttons & PlayerButtons.Jump) != 0 && _fuel[slot] > 0f)
            {
                var v = pawn.AbsVelocity;
                pawn.Teleport(null, null, new CssVector(v.X, v.Y, Jetpack.Climb(v.Z, Cfg.JetpackThrust, Cfg.JetpackMaxUp)));
                _fuel[slot] = Math.Max(0f, _fuel[slot] - dt);
            }

            if (hud && !c.IsBot) Hud.Set(c, "games.jetpack", Hud.Small(Jetpack.FuelBar(_fuel[slot], Cfg.JetpackFuelSeconds)), 5, 0.5f);
        }
    }

    // ====================================================================== No-scope rounds

    private static void NoScopeTick()
    {
        foreach (var c in Players.Alive())
        {
            var w = c.ActiveWeapon();
            if (w == null || w.NextSecondaryAttackTick == int.MaxValue || !ScopedWeapons.Contains(w.DesignerName)) continue;
            w.NextSecondaryAttackTick = int.MaxValue;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
        }
    }
}
