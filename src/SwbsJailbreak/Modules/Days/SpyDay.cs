using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Days;

/// <summary>
/// Spy Day warday: a few prisoners are secretly spies with a hidden pistol; they hide among the identical-looking
/// prisoners while the guards hunt them. Innocent prisoners can neither hurt nor be hurt by spies, and a guard who hits an
/// innocent takes part of the damage back. Spies see each other through an outline only spies receive (per-player
/// transmit), and the kill feed is hidden so kills do not give them away. Guards win when every spy is dead; prisoners
/// win when every guard is dead.
/// Spies cannot be disguised as guards: CS2 model swaps leave players as boxes, and a team switch shows on the
/// scoreboard and clan tag. So the spies blend in with the prisoners instead.
/// </summary>
public sealed class SpyDay : JbModule
{
    private NewDaysConfig Cfg => Section<NewDaysConfig>("DaysNew");

    public override string Name => "Days.Spy";
    public override int Order => 12;

    private DayDefinition? _day;
    private readonly HashSet<int> _spies = new();
    private readonly List<string> _spyNames = new();
    private readonly Dictionary<int, GlowHandle> _glows = new();
    private bool _released;
    private Timer? _hud;

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);
    private static bool HasFreeday(JbPlayer? p) => p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;
    private bool IsSpy(CCSPlayerController c) => c.Team == CsTeam.Terrorist && _spies.Contains(c.Slot);

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule days || !Cfg.SpyDayEnabled) return;
        _day = new DayDefinition
        {
            Name = "Spy Day", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying |
                    DayFlags.GiveGuardsInfiniteAmmo | DayFlags.DisableGuardsRadar,
            Description = "Some prisoners are secret spies with a hidden pistol. Guards must find them without hurting the innocent.",
            CanStart = () => Players.CountAlive(CsTeam.Terrorist) >= 2 && Players.CountAlive(CsTeam.CounterTerrorist) >= 1,
            OnStart = _ => Begin(),
            OnFreezeEnd = Release,
            OnEnd = _ => Finish()
        };
        days.Register(_day);
        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Finish();
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (_glows.Remove(player.Slot, out var glow)) Glow.Remove(glow);
        if (!Active || !_spies.Remove(player.Slot)) return;
        Server.NextFrame(CheckWin);
    }

    private void Begin()
    {
        _spies.Clear();
        _spyNames.Clear();
        _released = false;
        var prisoners = Players.AlivePrisoners().Where(c => !HasFreeday(c.Jb())).ToList();
        var count = DayLogic.SpyCount(prisoners.Count, Cfg.SpyRatio, Cfg.SpyMax);
        foreach (var c in prisoners.OrderBy(_ => Random.Shared.Next()).Take(count))
        {
            _spies.Add(c.Slot);
            _spyNames.Add(c.PlayerName);
        }

        foreach (var slot in _spies)
        {
            var c = Players.Get(slot).Controller;
            if (c == null) continue;
            Chat.Tell(c, "days.spy.you_are");
            var others = _spyNames.Where(n => n != c.PlayerName).ToList();
            Chat.Tell(c, others.Count == 0 ? "days.spy.alone" : "days.spy.partners", string.Join(", ", others));
            // Spies see each other's outline; nobody else receives these outline entities.
            if (Glow.Attach(c, Color.FromArgb(255, 255, 40, 40), viewer => viewer.Slot != slot && _spies.Contains(viewer.Slot)) is { } g)
                _glows[slot] = g;
        }

        Chat.TellAll("days.spy.start", _spies.Count);
    }

    private void Release()
    {
        _released = true;
        foreach (var p in DayUtil.AliveOn(CsTeam.CounterTerrorist))
        {
            DayUtil.Give(p, "weapon_knife");
            DayUtil.Give(p, "weapon_usp_silencer");
            DayUtil.Give(p, "weapon_m4a1_silencer", true);
        }

        foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist))
        {
            if (HasFreeday(p)) continue;
            DayUtil.Give(p, "weapon_knife_t", true);
            if (_spies.Contains(p.Slot)) DayUtil.Give(p, Cfg.SpyWeapon); // in the holster: the knife stays in hand
        }

        _hud = Round.Timer(1f, ShowHud, true);
        Chat.TellAll("days.spy.go");
        CheckWin();
    }

    private void Finish()
    {
        try { _hud?.Kill(); }
        catch { /* already stopped */ }
        _hud = null;
        foreach (var g in _glows.Values) Glow.Remove(g);
        _glows.Clear();
        if (_spyNames.Count > 0) Chat.TellAll("days.spy.reveal", string.Join(", ", _spyNames));
        _spies.Clear();
        _spyNames.Clear();
        _released = false;
        Hud.ClearPrefix("days.spy");
    }

    private void ShowHud()
    {
        if (!Active) return;
        var left = _spies.Count(s => Players.Get(s).Controller is { } c && c.IsAliveP());
        foreach (var c in Players.Humans())
        {
            if (IsSpy(c) && c.IsAliveP()) Hud.Set(c, "days.spy", Lang.T("days.spy.hud_spy", left), 20, 1.5f);
            else Hud.Set(c, "days.spy", Lang.T("days.spy.hud", left), 20, 1.5f);
        }
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active || ctx.Attacker == null || ctx.IsSelf) return HookResult.Continue;
        var a = ctx.Attacker;
        var v = ctx.Victim;
        if (HasFreeday(a.Jb()) || HasFreeday(v.Jb())) return HookResult.Continue;
        var rule = DayLogic.SpyDamage(a.Team == CsTeam.CounterTerrorist, IsSpy(a), v.Team == CsTeam.CounterTerrorist, IsSpy(v));
        switch (rule)
        {
            case DayLogic.SpyHit.Allow:
                return HookResult.Continue;
            case DayLogic.SpyHit.Misfire:
                Misfire(a, ctx.Damage);
                return HookResult.Handled;
            default:
                return HookResult.Handled;
        }
    }

    /// <summary>A guard hit an innocent prisoner: the damage is blocked and part of it hurts the guard.</summary>
    private void Misfire(CCSPlayerController guard, float damage)
    {
        var amount = (int)MathF.Round(damage * Math.Clamp(Cfg.SpyMisfireShare, 0f, 2f));
        Hud.Set(guard, "days.spy.misfire", Lang.T("days.spy.misfire"), 60, 2f);
        if (amount <= 0) return;
        var slot = guard.Slot;
        // Not inside the damage hook: change the guard's health on the next frame.
        Server.NextFrame(() =>
        {
            var c = Players.Get(slot).Controller;
            var pawn = c?.PawnOrNull();
            if (c == null || pawn == null || !c.IsAliveP()) return;
            if (pawn.Health - amount > 0)
            {
                pawn.Health -= amount;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            }
            else
            {
                DamageService.KillWithCredit(c, null, "misfire");
            }
        });
    }

    private HookResult OnDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        if (Active && Cfg.SpyHideKillFeed) info.DontBroadcast = true;
        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Active) return HookResult.Continue;
        var victim = ev.Userid;
        if (victim == null || !victim.IsValid) return HookResult.Continue;
        if (_glows.Remove(victim.Slot, out var glow)) Glow.Remove(glow);
        if (_spies.Remove(victim.Slot))
        {
            var left = _spies.Count(s => Players.Get(s).Controller is { } c && c.IsAliveP());
            Chat.TellAll("days.spy.spy_down", victim.PlayerName, left);
        }

        Server.NextFrame(CheckWin);
        return HookResult.Continue;
    }

    private void CheckWin()
    {
        if (!Active || !_released || !Round.IsActive) return;
        var spiesAlive = _spies.Count(s => Players.Get(s).Controller is { } c && c.IsAliveP() && c.Team == CsTeam.Terrorist);
        if (spiesAlive == 0)
        {
            Chat.TellAll("days.spy.guards_win");
            Round.End(CsTeam.CounterTerrorist);
        }
        else if (Players.CountAlive(CsTeam.CounterTerrorist) == 0)
        {
            Chat.TellAll("days.spy.spies_win");
            Round.End(CsTeam.Terrorist);
        }
    }
}
