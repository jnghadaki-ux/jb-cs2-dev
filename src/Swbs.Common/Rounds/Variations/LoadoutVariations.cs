using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Swbs.Common;

// Loadout rounds: everyone gets the same special weapons (SWBS Jailbreak wardays as deathmatch rounds).

/// <summary>Shared parts of the two rocket rounds.</summary>
public abstract class RocketVariationBase : RoundVariation
{
    public override VariationKind Kind => VariationKind.Loadout;

    private readonly Dictionary<int, int> _airshots = new();

    protected abstract void Configure(RocketLauncher rockets);

    public override void Start()
    {
        _airshots.Clear();
        Rounds.Rockets.Clear();
        Configure(Rounds.Rockets);
        Rounds.Rockets.Airshot += (attacker, victim) =>
        {
            _airshots[attacker.Slot] = _airshots.GetValueOrDefault(attacker.Slot) + 1;
            // An airshot is worth an extra point on the round's scoreboard.
            Rounds.AddScore(Players.Get(attacker.Slot), 1);
            Chat.TellAll("rounds.airshot_chat", Chat.Name(attacker), Chat.Name(victim));
        };
    }

    public override void End() => Rounds.Rockets.Clear();

    public override void Spawn(CCSPlayerController c)
    {
        c.SetArmor(0, false);
        Rounds.Rockets.Arm(c);
    }

    // Rocket jumping: no fall damage while the launcher is out.
    public override HookResult Damage(DamageContext ctx) => ctx.IsFall ? HookResult.Handled : HookResult.Continue;

    public override string? HudLine(CCSPlayerController c) =>
        Lang.T("rounds.hud.airshots", _airshots.GetValueOrDefault(c.Slot));
}

/// <summary>RPG Arena: SMPlugins CustomWeapon_RPG for everyone - rocket jumps, splash, airshots kill.</summary>
public sealed class RpgArenaVariation : RocketVariationBase
{
    public override string Key => "rpg";

    protected override void Configure(RocketLauncher rockets)
    {
        rockets.ClipSize = Math.Max(1, Tune.RpgClip);
        rockets.DelayFactor = 1f;
    }
}

/// <summary>Rocket Rain: fast-firing launchers with a big clip, but only players in the air can be hurt.</summary>
public sealed class RocketRainVariation : RocketVariationBase
{
    public override string Key => "rocketrain";

    protected override void Configure(RocketLauncher rockets)
    {
        rockets.ClipSize = Math.Max(1, Tune.RocketRainClip);
        rockets.DelayFactor = Math.Clamp(Tune.RocketRainSpeed, 0.1f, 1f);
        rockets.AirborneOnly = true;
    }
}

/// <summary>Throwing Knives: SMPlugins ThrowingKnife - every knife swing throws a knife; head hits deal double.</summary>
public sealed class ThrowingKnivesVariation : RoundVariation
{
    public override string Key => "knives";
    public override VariationKind Kind => VariationKind.Loadout;

    private readonly Dictionary<int, (int Thrown, int Hits)> _stats = new();

    public override void Start()
    {
        _stats.Clear();
        Rounds.Knives.Hit += (thrower, _, _) =>
        {
            var s = _stats.GetValueOrDefault(thrower.Slot);
            _stats[thrower.Slot] = (s.Thrown, s.Hits + 1);
        };
    }

    public override void Spawn(CCSPlayerController c)
    {
        c.SetArmor(0, false);
        c.Give(KnifeFor(c));
        SelectSlot(c, "slot3");
    }

    public override void WeaponFire(CCSPlayerController c, string weapon)
    {
        if (!Weapons.IsKnife(weapon) || !Rounds.Knives.Throw(c)) return;
        var s = _stats.GetValueOrDefault(c.Slot);
        _stats[c.Slot] = (s.Thrown + 1, s.Hits);
    }

    public override string? HudLine(CCSPlayerController c)
    {
        var s = _stats.GetValueOrDefault(c.Slot);
        var accuracy = s.Thrown > 0 ? (int)MathF.Round(100f * s.Hits / s.Thrown) : 0;
        return Lang.T("rounds.hud.knives", s.Hits, s.Thrown, accuracy);
    }
}

/// <summary>Dodgeball: decoys only, a hit in flight kills, a new decoy after every throw, floaty gravity.</summary>
public sealed class DodgeballVariation : RoundVariation
{
    public override string Key => "dodgeball";
    public override VariationKind Kind => VariationKind.Loadout;

    private const string Gravity = "rounds.dodgeball";
    private readonly Dictionary<int, int> _streak = new();

    public override void Start() => _streak.Clear();

    public override void End() => Movement.ClearKeyAll(Gravity);

    public override void Spawn(CCSPlayerController c)
    {
        c.SetArmor(0, false);
        c.SetHp(1, false);
        c.Give("weapon_decoy");
        Movement.SetGravity(c, Gravity, Tune.DodgeballGravity);
        SelectSlot(c, "slot4");
    }

    public override void GrenadeThrown(CCSPlayerController c, string weapon)
    {
        if (!weapon.Contains("decoy")) return;
        var slot = c.Slot;
        Rounds.After(Tune.DodgeballRefillSeconds, this, () =>
        {
            if (Players.Get(slot).Controller is { IsValid: true } pc && pc.IsAliveP() && !pc.HasWeapon("weapon_decoy"))
            {
                pc.Give("weapon_decoy");
                SelectSlot(pc, "slot4");
            }
        });
    }

    public override HookResult Damage(DamageContext ctx)
    {
        if (ctx.IsFall) return HookResult.Handled;
        if (ctx.Weapon is "decoy_projectile" or "weapon_decoy") ctx.Damage = 999f;
        return HookResult.Continue;
    }

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        _streak.Remove(victim.Slot);
        if (attacker == null || attacker.Slot == victim.Slot) return;
        var streak = _streak[attacker.Slot] = _streak.GetValueOrDefault(attacker.Slot) + 1;
        if (streak is 3 or 5 or 8) Chat.TellAll("rounds.dodgeball_streak", attacker.Name, streak);
    }

    /// <summary>A CS2 decoy hit does not always arrive as damage: a decoy in flight inside a player's hull kills them.</summary>
    public override void Tick(float now)
    {
        var ffa = RoundClock.FreeForAll;
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("decoy_projectile"))
        {
            if (!e.IsValid || e.AbsOrigin == null) continue;
            var v = e.AbsVelocity;
            if (v.X * v.X + v.Y * v.Y + v.Z * v.Z < 150f * 150f) continue; // rolling or resting: harmless
            var ownerPawn = e.OwnerEntity.Value;
            var thrower = ownerPawn is { IsValid: true } && ownerPawn.DesignerName == "player"
                ? ownerPawn.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>()
                : null;
            var pos = e.AbsOrigin;
            foreach (var c in Players.Alive())
            {
                if (thrower != null && c.Slot == thrower.Slot) continue;
                if (!ffa && thrower != null && c.Team == thrower.Team) continue;
                var o = c.PawnOrNull()?.AbsOrigin;
                if (o == null || Math.Abs(pos.X - o.X) > 20f || Math.Abs(pos.Y - o.Y) > 20f || pos.Z < o.Z - 4f || pos.Z > o.Z + 76f) continue;
                DamageService.KillWithCredit(c, thrower is { IsValid: true } ? thrower : null, "decoy");
                e.AcceptInput("Kill");
                break;
            }
        }
    }

    public override string? HudLine(CCSPlayerController c) => Lang.T("rounds.hud.streak", _streak.GetValueOrDefault(c.Slot));
}

/// <summary>Scoutz Knivez: SSG 08 and knife in low gravity, no fall damage.</summary>
public sealed class ScoutzKnivezVariation : RoundVariation
{
    public override string Key => "scoutz";
    public override VariationKind Kind => VariationKind.Loadout;

    private const string Gravity = "rounds.scoutz";

    public override void End() => Movement.ClearKeyAll(Gravity);

    public override void Spawn(CCSPlayerController c)
    {
        c.Give(KnifeFor(c));
        GiveWeapon(c, "weapon_ssg08")?.SetAmmo(10, 90);
        Movement.SetGravity(c, Gravity, Tune.LowGravity);
        SelectSlot(c, "slot1");
    }

    public override HookResult Damage(DamageContext ctx) => ctx.IsFall ? HookResult.Handled : HookResult.Continue;
}

/// <summary>One in the Chamber: a Deagle with one bullet that always kills; every kill gives the bullet back.</summary>
public sealed class OneInTheChamberVariation : RoundVariation
{
    public override string Key => "oitc";
    public override VariationKind Kind => VariationKind.Loadout;

    public override void Spawn(CCSPlayerController c)
    {
        c.SetArmor(0, false);
        c.Give(KnifeFor(c));
        GiveWeapon(c, "weapon_deagle")?.SetAmmo(1, 0);
        SelectSlot(c, "slot2");
    }

    public override HookResult Damage(DamageContext ctx)
    {
        if (ctx.Weapon == "weapon_deagle" && !ctx.IsSelf) ctx.Damage = 999f;
        return HookResult.Continue;
    }

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (attacker?.Controller is not { } c || attacker.Slot == victim.Slot) return;
        var slot = c.Slot;
        Server.NextFrame(() =>
        {
            if (Players.Get(slot).Controller is not { IsValid: true } pc || !pc.IsAliveP()) return;
            var deagle = pc.Weapons().FirstOrDefault(w => w.DesignerName == "weapon_deagle");
            if (deagle == null) GiveWeapon(pc, "weapon_deagle")?.SetAmmo(1, 0);
            else deagle.SetAmmo(Math.Min(7, deagle.Clip1 + 1), 0);
        });
    }

    public override string? HudLine(CCSPlayerController c)
    {
        var deagle = c.IsAliveP() ? c.Weapons().FirstOrDefault(w => w.DesignerName == "weapon_deagle") : null;
        return Lang.T("rounds.hud.bullets", deagle?.Clip1 ?? 0);
    }
}

/// <summary>Gun Game (the SWBS Jailbreak day): every kill moves you up the ladder; a knife kill on the last level wins.</summary>
public sealed class GunGameVariation : RoundVariation
{
    public override string Key => "gungame";
    public override VariationKind Kind => VariationKind.Loadout;

    private List<string> _ladder = new();
    private readonly Dictionary<int, int> _level = new();

    public override void Start()
    {
        _ladder = RoundLogic.GunGameLadder(Tune.GunGameWeapons);
        _level.Clear();
    }

    private int LevelOf(int slot) => _level.GetValueOrDefault(slot);

    public override void Spawn(CCSPlayerController c) => Equip(c);

    private void Equip(CCSPlayerController c)
    {
        if (!c.IsAliveP()) return;
        Strip(c);
        c.Give(KnifeFor(c));
        var weapon = _ladder[Math.Clamp(LevelOf(c.Slot), 0, _ladder.Count - 1)];
        if (!Weapons.IsKnife(weapon))
        {
            GiveWeapon(c, weapon);
            SelectSlot(c, Weapons.IsPistol(weapon) ? "slot2" : "slot1");
        }
        else SelectSlot(c, "slot3");
    }

    public override void Death(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        var weapon = ev.Weapon ?? "";
        if (attacker == null || attacker.Slot == victim.Slot) return;
        var knifed = weapon.Contains("knife") || weapon.Contains("bayonet");
        if (knifed && Tune.GunGameKnifeDemote > 0)
        {
            var before = LevelOf(victim.Slot);
            _level[victim.Slot] = RoundLogic.GunGameDemote(before, _ladder.Count, Tune.GunGameKnifeDemote);
            if (_level[victim.Slot] < before) Chat.TellAll("rounds.gungame_demoted", victim.Name, attacker.Name);
        }

        var next = RoundLogic.GunGameAdvance(LevelOf(attacker.Slot), _ladder.Count);
        if (next < 0)
        {
            Rounds.AddScore(attacker, 10);
            Rounds.Finish("rounds.gungame_won", attacker.Name);
            return;
        }

        _level[attacker.Slot] = next;
        if (next == _ladder.Count - 1) Chat.TellAll("rounds.gungame_knife_level", attacker.Name);
        var slot = attacker.Slot;
        Server.NextFrame(() => { if (Players.Get(slot).Controller is { IsValid: true } pc) Equip(pc); });
    }

    public override string? HudLine(CCSPlayerController c)
    {
        var level = LevelOf(c.Slot);
        var weapon = _ladder.Count > 0 ? _ladder[Math.Clamp(level, 0, _ladder.Count - 1)] : "";
        return Lang.T("rounds.hud.gungame", level + 1, _ladder.Count, Hud.Escape(Weapons.Display(weapon)));
    }
}

/// <summary>HE Party: HE grenades only, refilled after every throw, in low gravity.</summary>
public sealed class HePartyVariation : RoundVariation
{
    public override string Key => "heparty";
    public override VariationKind Kind => VariationKind.Loadout;

    private const string Gravity = "rounds.he";

    public override void End() => Movement.ClearKeyAll(Gravity);

    public override void Spawn(CCSPlayerController c)
    {
        c.Give(KnifeFor(c));
        c.Give("weapon_hegrenade");
        Movement.SetGravity(c, Gravity, Tune.HeGravity);
        SelectSlot(c, "slot4");
    }

    public override void GrenadeThrown(CCSPlayerController c, string weapon)
    {
        if (!weapon.Contains("hegrenade")) return;
        var slot = c.Slot;
        Rounds.After(Tune.HeRefillSeconds, this, () =>
        {
            if (Players.Get(slot).Controller is { IsValid: true } pc && pc.IsAliveP() && !pc.HasWeapon("weapon_hegrenade"))
            {
                pc.Give("weapon_hegrenade");
                SelectSlot(pc, "slot4");
            }
        });
    }

    public override HookResult Damage(DamageContext ctx) => ctx.IsFall ? HookResult.Handled : HookResult.Continue;
}

/// <summary>Deagle Headshots: Deagles with endless ammo, and only headshots deal damage.</summary>
public sealed class DeagleHeadshotsVariation : RoundVariation
{
    public override string Key => "headshots";
    public override VariationKind Kind => VariationKind.Loadout;

    public override void Spawn(CCSPlayerController c)
    {
        c.Give(KnifeFor(c));
        GiveWeapon(c, "weapon_deagle")?.SetAmmo(7, 999);
        SelectSlot(c, "slot2");
    }

    public override HookResult Damage(DamageContext ctx)
    {
        if (ctx.IsWorld || ctx.IsSelf || ctx.IsKnife) return HookResult.Continue;
        return ctx.HitGroup == HitGroup_t.HITGROUP_HEAD ? HookResult.Continue : HookResult.Handled;
    }

    public override void WeaponFire(CCSPlayerController c, string weapon)
    {
        if (weapon != "weapon_deagle") return;
        var w = c.ActiveWeapon();
        if (w != null && w.ReserveAmmo[0] < 100) w.SetAmmo(w.Clip1, 999);
    }
}
