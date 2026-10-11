using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "FreezeTag" (ultjb_freezetag_* cvars).</summary>
public sealed class FreezeTagConfig
{
    public int GuardFreezeTime { get; set; } = 15;   // ultjb_freezetag_freeze_time
    public int Time { get; set; } = 240;             // ultjb_freezetag_time
    public int CtKnifeAdd { get; set; } = 10;        // ultjb_freezetag_ct_knife_add
    public int ChancesBeforeDeath { get; set; } = 2; // ultjb_chances_before_death
}

/// <summary>
/// Port of Days/UltJB_Warday_FreezeTag.sp: guards (frozen and blinded first) knife prisoners to freeze them, prisoners
/// unfreeze teammates by hitting them; a prisoner frozen more than N times dies. Guards win when no prisoner can move,
/// prisoners win when the clock (extended 10 s per freeze) runs out.
/// </summary>
public sealed class FreezeTagDay : JbModule
{
    private FreezeTagConfig Cfg => Section<FreezeTagConfig>("FreezeTag");

    public override string Name => "Days.FreezeTag";
    public override int Order => 12;

    private static readonly Color Frozen = Color.FromArgb(255, 117, 202, 255);

    private DayDefinition? _day;
    private Timer? _guardFreezeTimer, _tagTimer, _knifeTimer;
    private int _countdown;
    private readonly int[] _frozenCount = new int[Players.MaxSlots];
    private readonly bool[] _isFrozen = new bool[Players.MaxSlots];

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule) return;
        _day = new DayDefinition
        {
            Name = "Freeze Tag", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons,
            OnStart = _ =>
            {
                Array.Clear(_frozenCount);
                Array.Clear(_isFrozen);
                foreach (var g in Players.AliveGuards())
                {
                    SetFrozen(g, true);
                    Fx.Fade(g, Color.FromArgb(255, 0, 0, 0), 1.5f, 1.5f, Fx.FadeOut | Fx.FadeStayOut);
                }

                _countdown = 0;
                ShowUnfreezeCountdown();
                Kill(ref _guardFreezeTimer);
                _guardFreezeTimer = Plugin.AddTimer(1f, GuardFreezeTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            },
            OnEnd = _ =>
            {
                Kill(ref _guardFreezeTimer);
                Kill(ref _tagTimer);
                Kill(ref _knifeTimer);
                foreach (var c in Players.Alive()) if (_isFrozen[c.Slot]) SetFrozen(c, false);
            }
        };
        Jb.Days.Register(_day);
        _day.FreezeTime = 0;
        DamageService.Register(Name, 90, OnDamage);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Kill(ref _guardFreezeTimer);
        Kill(ref _tagTimer);
        Kill(ref _knifeTimer);
    }

    private static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    private void SetFrozen(CCSPlayerController c, bool frozen)
    {
        _isFrozen[c.Slot] = frozen;
        c.SetMoveType(frozen ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK);
        if (frozen) c.SetColor(Frozen);
        else c.ResetColor();
    }

    private void ShowUnfreezeCountdown()
    {
        var html = Lang.T("days.retake.unfreeze_in", Cfg.GuardFreezeTime - _countdown);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.freezetag", html, 60, 2);
    }

    private void GuardFreezeTick()
    {
        _countdown++;
        if (_countdown < Cfg.GuardFreezeTime)
        {
            ShowUnfreezeCountdown();
            return;
        }

        Kill(ref _guardFreezeTimer);
        foreach (var g in Players.AliveGuards())
        {
            SetFrozen(g, false);
            Fx.Fade(g, Color.FromArgb(0, 0, 0, 0), 1.5f, 0f, Fx.FadeIn | Fx.FadePurge);
        }

        foreach (var c in Players.Humans()) Hud.Set(c, "days.freezetag", Lang.T("days.retake.unfrozen"), 60, 3);
        _countdown = 0;
        CountdownHud();
        _tagTimer = Plugin.AddTimer(1f, TagTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        GiveKnives();
        _knifeTimer = Plugin.AddTimer(3f, GiveKnives, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void TagTick()
    {
        _countdown++;
        if (_countdown < Cfg.Time)
        {
            CountdownHud();
            return;
        }

        Kill(ref _tagTimer);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.freezetag", Lang.T("days.freezetag.prisoners_win"), 60, 5);
        SlayTeam(CsTeam.CounterTerrorist);
    }

    private static bool IsFreedayPlayer(CCSPlayerController c) =>
        c.Jb() is { } p && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    private (int active, int alive) CountPrisoners()
    {
        int active = 0, alive = 0;
        foreach (var c in Players.AlivePrisoners())
        {
            if (IsFreedayPlayer(c)) continue;
            alive++;
            if (!_isFrozen[c.Slot]) active++;
        }

        return (active, alive);
    }

    private void CountdownHud()
    {
        var (active, alive) = CountPrisoners();
        if (active == 0 && _tagTimer != null)
        {
            SlayTeam(CsTeam.Terrorist);
            return;
        }

        var left = Math.Max(0, Cfg.Time - _countdown);
        var html = Lang.T("days.freezetag.hud", left / 60, left % 60, active, alive);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.freezetag", html, 60, 2);
    }

    private void CheckRemaining()
    {
        var (active, _) = CountPrisoners();
        if (active == 0)
        {
            foreach (var c in Players.Humans()) Hud.Set(c, "days.freezetag", Lang.T("days.freezetag.guards_win"), 60, 5);
            SlayTeam(CsTeam.Terrorist);
        }
        else
        {
            CountdownHud();
        }
    }

    private static void SlayTeam(CsTeam team)
    {
        foreach (var c in Players.AliveOnTeam(team).ToList()) c.CommitSuicide(false, true);
    }

    private void GiveKnives()
    {
        foreach (var p in DayUtil.AliveAll())
        {
            var c = p.Controller!;
            if (c.Weapons().Any(w => Weapons.IsKnife(w.DesignerName))) continue;
            DayUtil.Give(p, c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t", true);
        }
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active) return HookResult.Continue;
        var victim = ctx.Victim;
        if (ctx.Attacker == null)
            return _isFrozen[victim.Slot] && (ctx.Info.BitsDamageType & DamageTypes_t.DMG_DROWN) != 0 ? HookResult.Handled : HookResult.Continue;

        var attacker = ctx.Attacker;
        if (IsFreedayPlayer(attacker) || IsFreedayPlayer(victim)) return HookResult.Continue;
        if (attacker.Team == CsTeam.Terrorist)
        {
            // Prisoners free their frozen teammates.
            if (victim.Team == attacker.Team && _isFrozen[victim.Slot])
            {
                SetFrozen(victim, false);
                CountdownHud();
            }

            return HookResult.Handled;
        }

        if (victim.Team != attacker.Team && !_isFrozen[victim.Slot] && _tagTimer != null)
        {
            _frozenCount[victim.Slot]++;
            _countdown = Math.Max(0, _countdown - Cfg.CtKnifeAdd);
            SetFrozen(victim, true);
            if (_frozenCount[victim.Slot] > Cfg.ChancesBeforeDeath)
            {
                var v = victim;
                Server.NextFrame(() =>
                {
                    if (v.IsValid && v.IsAliveP()) DamageService.KillWithCredit(v, attacker);
                });
            }

            Server.NextFrame(CheckRemaining); // may slay the prisoners: never inside the damage hook
        }

        return HookResult.Handled;
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (!Active || _tagTimer == null) return;
        if (victim.Controller?.Team == CsTeam.Terrorist && !_isFrozen[victim.Slot]) CheckRemaining();
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (!Active || _tagTimer == null) return;
        var c = player.Controller;
        _isFrozen[player.Slot] = false;
        if (c?.Team == CsTeam.Terrorist) Server.NextFrame(CheckRemaining);
    }
}
