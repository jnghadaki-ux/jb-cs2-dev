using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Swbs.Common;

// Objective rounds: the goal changes, not the guns.

/// <summary>
/// Bounty Hunter: one player carries the bounty (outlined in gold). Their kills raise its value; whoever kills them
/// cashes it in and becomes the next bounty.
/// </summary>
public sealed class BountyHunterVariation : RoundVariation
{
    public override string Key => "bounty";
    public override VariationKind Kind => VariationKind.Objective;
    public override int MinPlayers => 3;

    private int _bounty = -1;
    private int _streak;

    public override void Start()
    {
        _bounty = -1;
        _streak = 0;
    }

    public override void End()
    {
        if (_bounty >= 0 && Players.Get(_bounty).Controller is { IsValid: true } c) Rounds.Highlight(c, null);
        _bounty = -1;
    }

    private void Mark(CCSPlayerController c, bool announce)
    {
        if (_bounty >= 0 && _bounty != c.Slot && Players.Get(_bounty).Controller is { IsValid: true } old) Rounds.Highlight(old, null);
        _bounty = c.Slot;
        _streak = 0;
        Rounds.Highlight(c, Color.Gold);
        if (announce) Chat.TellAll("rounds.bounty_new", Chat.Name(c), Value);
        Hud.Set(c, "rounds.bounty.you", Lang.T("rounds.hud.bounty_you"), 70, 3);
    }

    private int Value => RoundLogic.BountyValue(Tune.BountyBase, _streak);

    public override void Second()
    {
        if (_bounty >= 0 && Players.Get(_bounty).Controller is { IsValid: true } b && b.IsAliveP()) return;
        var alive = Players.Alive().ToList();
        if (alive.Count < 2) return;
        Mark(alive[Random.Shared.Next(alive.Count)], true);
    }

    public override int KillScore(SwbsPlayer victim, SwbsPlayer attacker, EventPlayerDeath ev)
    {
        if (victim.Slot == _bounty) return Value;
        return attacker.Slot == _bounty ? 2 : 1;
    }

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (victim.Slot == _bounty)
        {
            var value = Value;
            if (victim.Controller is { IsValid: true } vc) Rounds.Highlight(vc, null);
            _bounty = -1;
            if (attacker?.Controller is { IsValid: true } ac && attacker.Slot != victim.Slot && ac.IsAliveP())
            {
                Chat.TellAll("rounds.bounty_claimed", attacker.Name, victim.Name, value);
                Mark(ac, false);
            }

            return;
        }

        if (attacker != null && attacker.Slot == _bounty && attacker.Slot != victim.Slot)
        {
            _streak++;
            if (_streak % 3 == 0) Chat.TellAll("rounds.bounty_rising", attacker.Name, Value);
        }
    }

    public override string? HudLine(CCSPlayerController c)
    {
        if (_bounty < 0) return null;
        return c.Slot == _bounty
            ? Lang.T("rounds.hud.bounty_self", Value)
            : Lang.T("rounds.hud.bounty", Hud.Escape(Players.Get(_bounty).Name), Value);
    }
}

/// <summary>
/// Juggernaut (the SWBS Jailbreak day): one player is a slow, armoured juggernaut with a machine gun. Killing them is
/// worth big points and makes you the juggernaut; the juggernaut scores a point for every 15 seconds alive.
/// </summary>
public sealed class JuggernautVariation : RoundVariation
{
    public override string Key => "juggernaut";
    public override VariationKind Kind => VariationKind.Objective;
    public override int MinPlayers => 3;

    private const string Speed = "rounds.jugg";
    private int _jugg = -1;
    private int _aliveSeconds;

    public override void Start()
    {
        _jugg = -1;
        _aliveSeconds = 0;
    }

    public override void End()
    {
        Release();
        Movement.ClearKeyAll(Speed);
    }

    private void Release()
    {
        if (_jugg >= 0 && Players.Get(_jugg).Controller is { IsValid: true } c)
        {
            Rounds.Highlight(c, null);
            Movement.ClearSpeed(c, Speed);
        }

        _jugg = -1;
    }

    private void Make(CCSPlayerController c)
    {
        Release();
        _jugg = c.Slot;
        _aliveSeconds = 0;
        var players = Players.All().Count(p => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist);
        c.SetHp(RoundLogic.JuggernautHealth(Tune.JuggernautBaseHealth, Tune.JuggernautHealthPerPlayer, players));
        c.SetArmor(100, true);
        Movement.SetSpeed(c, Speed, Tune.JuggernautSpeed);
        Rounds.Highlight(c, Color.Red);
        if (!string.IsNullOrWhiteSpace(Tune.JuggernautWeapon))
        {
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                if (!c.IsValid || !c.IsAliveP() || c.Slot != _jugg) return;
                Strip(c);
                c.Give(KnifeFor(c));
                GiveWeapon(c, Tune.JuggernautWeapon)?.SetAmmo(150, 999);
                SelectSlot(c, "slot1");
            });
        }

        Chat.TellAll("rounds.jugg_new", Chat.Name(c), c.GetHp());
        Hud.Set(c, "rounds.jugg.you", Lang.T("rounds.hud.jugg_you"), 70, 3);
    }

    public override void Second()
    {
        if (_jugg >= 0 && Players.Get(_jugg).Controller is { IsValid: true } j && j.IsAliveP())
        {
            if (++_aliveSeconds % 15 == 0) Rounds.AddScore(Players.Get(_jugg), 1);
            return;
        }

        var alive = Players.Alive().ToList();
        if (alive.Count < 2) return;
        Make(alive[Random.Shared.Next(alive.Count)]);
    }

    public override int KillScore(SwbsPlayer victim, SwbsPlayer attacker, EventPlayerDeath ev) =>
        victim.Slot == _jugg ? Math.Max(1, Tune.JuggernautKillScore) : 1;

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (victim.Slot != _jugg) return;
        Release();
        if (attacker?.Controller is { IsValid: true } ac && attacker.Slot != victim.Slot && ac.IsAliveP())
        {
            Chat.TellAll("rounds.jugg_down", attacker.Name, victim.Name);
            Make(ac);
        }
    }

    public override string? HudLine(CCSPlayerController c)
    {
        if (_jugg < 0) return null;
        if (c.Slot == _jugg) return Lang.T("rounds.hud.jugg_self", c.GetHp());
        var j = Players.Get(_jugg);
        return Lang.T("rounds.hud.jugg", Hud.Escape(j.Name), j.Controller?.GetHp() ?? 0);
    }
}

/// <summary>Last Man Standing: everyone has a few lives; out of lives means out of the round. The last one alive wins.</summary>
public sealed class LastManStandingVariation : RoundVariation
{
    public override string Key => "lastman";
    public override VariationKind Kind => VariationKind.Objective;
    public override int MinPlayers => 3;

    private readonly Dictionary<int, int> _lives = new();
    private float _started;

    public override void Start()
    {
        _lives.Clear();
        _started = CounterStrikeSharp.API.Server.CurrentTime;
    }

    private int Lives(int slot) => _lives.GetValueOrDefault(slot);

    public override void Spawn(CCSPlayerController c)
    {
        // Late joiners get one life after the first 30 seconds.
        if (!_lives.ContainsKey(c.Slot))
            _lives[c.Slot] = CounterStrikeSharp.API.Server.CurrentTime - _started < 30f ? Math.Max(1, Tune.LastManStandingLives) : 1;
    }

    public override bool AllowRespawn(SwbsPlayer p) => !_lives.TryGetValue(p.Slot, out var l) || l > 0;

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (!_lives.TryGetValue(victim.Slot, out var l)) return;
        _lives[victim.Slot] = l = Math.Max(0, l - 1);
        if (l == 0) Chat.TellAll("rounds.lms_out", victim.Name, Remaining().Count);
        else Chat.Tell(victim, "rounds.lms_lives", l);
    }

    private List<int> Remaining() =>
        _lives.Where(kv => kv.Value > 0 && Players.Get(kv.Key).Controller is { IsValid: true } c && c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            .Select(kv => kv.Key).ToList();

    public override void Second()
    {
        var left = Remaining();
        if (!RoundLogic.LastManDecided(left.Count, _lives.Count)) return;
        if (left.Count == 1)
        {
            var winner = Players.Get(left[0]);
            Rounds.AddScore(winner, 5);
            Rounds.Finish("rounds.lms_won", winner.Name, Lives(winner.Slot));
        }
        else Rounds.Finish("rounds.lms_nobody");
    }

    public override string? HudLine(CCSPlayerController c) =>
        Lang.T("rounds.hud.lms", Lives(c.Slot), Remaining().Count);
}
