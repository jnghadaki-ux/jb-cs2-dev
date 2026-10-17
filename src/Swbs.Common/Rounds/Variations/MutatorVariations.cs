using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Swbs.Common;

// Mutator rounds: the mode's own loadout with one rule on top.

/// <summary>Low Gravity: everyone floats; no fall damage.</summary>
public sealed class LowGravityVariation : RoundVariation
{
    public override string Key => "lowgrav";
    public override VariationKind Kind => VariationKind.Mutator;

    private const string Gravity = "rounds.lowgrav";

    public override void End() => Movement.ClearKeyAll(Gravity);

    public override void Spawn(CCSPlayerController c) => Movement.SetGravity(c, Gravity, Tune.LowGravity);

    public override HookResult Damage(DamageContext ctx) => ctx.IsFall ? HookResult.Handled : HookResult.Continue;
}

/// <summary>Speed: everyone runs faster.</summary>
public sealed class SpeedVariation : RoundVariation
{
    public override string Key => "speed";
    public override VariationKind Kind => VariationKind.Mutator;

    private const string Speed = "rounds.speed";

    public override void End() => Movement.ClearKeyAll(Speed);

    public override void Spawn(CCSPlayerController c) => Movement.SetSpeed(c, Speed, Tune.SpeedMultiplier);
}

/// <summary>Vampire: a share of the damage you deal heals you, up to a cap above 100.</summary>
public sealed class VampireVariation : RoundVariation
{
    public override string Key => "vampire";
    public override VariationKind Kind => VariationKind.Mutator;

    private readonly Dictionary<int, int> _drained = new();

    public override void Start() => _drained.Clear();

    public override HookResult Damage(DamageContext ctx)
    {
        if (ctx.Attacker is not { IsValid: true } a || ctx.IsSelf || !a.IsAliveP()) return HookResult.Continue;
        var pawn = a.PawnOrNull();
        if (pawn == null) return HookResult.Continue;
        var dealt = Math.Min(ctx.Damage, ctx.VictimPawn.Health);
        var healed = RoundLogic.VampireHeal(pawn.Health, dealt, Tune.VampireShare, Tune.VampireMaxHealth);
        if (healed <= pawn.Health) return HookResult.Continue;
        _drained[a.Slot] = _drained.GetValueOrDefault(a.Slot) + healed - pawn.Health;
        var slot = a.Slot;
        // After the hit is applied (changing health inside the damage hook is unreliable).
        Server.NextFrame(() =>
        {
            if (Players.Get(slot).Controller is not { IsValid: true } c || c.PawnOrNull() is not { } p || !c.IsAliveP()) return;
            if (p.Health >= healed) return;
            if (p.MaxHealth < healed) p.MaxHealth = Math.Max(100, Tune.VampireMaxHealth);
            p.Health = healed;
            Utilities.SetStateChanged(p, "CBaseEntity", "m_iHealth");
        });
        return HookResult.Continue;
    }

    public override string? HudLine(CCSPlayerController c) => Lang.T("rounds.hud.drained", _drained.GetValueOrDefault(c.Slot));
}

/// <summary>Glass Cannon: everyone has very little health and no armour - aim decides everything.</summary>
public sealed class GlassCannonVariation : RoundVariation
{
    public override string Key => "glass";
    public override VariationKind Kind => VariationKind.Mutator;
    public override bool AllowModeRefills => false;

    public override void Spawn(CCSPlayerController c)
    {
        c.SetArmor(0, false);
        c.SetHp(Math.Max(1, Tune.GlassCannonHealth), false);
    }
}
