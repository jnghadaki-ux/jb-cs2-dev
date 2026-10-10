using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>Information about one pending damage application (from CSS's native OnPlayerTakeDamagePre).</summary>
public sealed class DamageContext
{
    public required CCSPlayerController Victim { get; init; }
    public required CCSPlayerPawn VictimPawn { get; init; }
    public CCSPlayerController? Attacker { get; init; }
    public required CTakeDamageInfo Info { get; init; }

    /// <summary>Weapon designer name ("weapon_ak47", "weapon_knife", "hegrenade_projectile", "inferno", "" for world).</summary>
    public string Weapon { get; init; } = "";

    public bool IsSelf => Attacker != null && Attacker.Slot == Victim.Slot;
    public bool IsWorld => Attacker == null;
    public bool IsFall => (Info.BitsDamageType & DamageTypes_t.DMG_FALL) != 0;
    public bool IsKnife => Weapons.IsKnife(Weapon);

    public float Damage
    {
        get => Info.Damage;
        set => Info.Damage = value;
    }

    public HitGroup_t HitGroup
    {
        get
        {
            try { return Info.GetHitGroup(); }
            catch { return HitGroup_t.HITGROUP_GENERIC; }
        }
    }
}

/// <summary>
/// Single OnPlayerTakeDamagePre listener dispatching to module handlers in priority order (highest first).
/// A handler returning HookResult.Handled or Stop blocks the damage entirely; handlers may also change
/// ctx.Damage. Replaces SDKHooks OnTakeDamage from the SourceMod original.
/// </summary>
public static class DamageService
{
    public delegate HookResult Handler(DamageContext ctx);

    private static readonly List<(int Priority, string Owner, Handler Handler)> Handlers = new();

    /// <summary>Register a handler. Higher priority runs first (LR/days 100, rebel tracking 50, misc 0).</summary>
    public static void Register(string owner, int priority, Handler handler)
    {
        Handlers.Add((priority, owner, handler));
        Handlers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    public static void Unregister(Handler handler) => Handlers.RemoveAll(h => h.Handler == handler);

    internal static void Clear() => Handlers.Clear();

    internal static HookResult OnPlayerTakeDamagePre(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        if (Handlers.Count == 0) return HookResult.Continue;
        try
        {
            if (pawn == null || !pawn.IsValid) return HookResult.Continue;
            var victim = pawn.Controller.Value?.As<CCSPlayerController>();
            if (victim == null || !victim.IsValid) return HookResult.Continue;

            var ctx = new DamageContext
            {
                Victim = victim,
                VictimPawn = pawn,
                Attacker = ResolveAttacker(info),
                Info = info,
                Weapon = ResolveWeapon(info)
            };

            foreach (var (_, owner, handler) in Handlers.ToArray())
            {
                HookResult r;
                try
                {
                    r = handler(ctx);
                }
                catch (Exception ex)
                {
                    JailbreakPlugin.Instance?.Logger.LogError(ex, "Damage handler {Owner} failed", owner);
                    continue;
                }

                if (r >= HookResult.Handled) return HookResult.Handled;
            }
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "Damage dispatch failed");
        }

        return HookResult.Continue;
    }

    public static CCSPlayerController? ResolveAttacker(CTakeDamageInfo info)
    {
        try
        {
            var ent = info.Attacker.Value;
            if (ent == null || !ent.IsValid) return null;
            if (ent.DesignerName == "player")
            {
                var pawn = ent.As<CCSPlayerPawn>();
                var c = pawn.Controller.Value?.As<CCSPlayerController>();
                return c is { IsValid: true } ? c : null;
            }

            // grenades / projectiles: owner entity is the thrower's pawn
            var owner = ent.OwnerEntity.Value;
            if (owner is { IsValid: true } && owner.DesignerName == "player")
            {
                var c = owner.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>();
                return c is { IsValid: true } ? c : null;
            }
        }
        catch
        {
            // ignore - treat as world damage
        }

        return null;
    }

    public static string ResolveWeapon(CTakeDamageInfo info)
    {
        try
        {
            var ability = info.Ability.Value;
            if (ability is { IsValid: true } && ability.DesignerName.StartsWith("weapon_")) return ability.DesignerName;
            var inflictor = info.Inflictor.Value;
            if (inflictor is { IsValid: true })
            {
                if (inflictor.DesignerName == "player")
                {
                    var w = inflictor.As<CCSPlayerPawn>().WeaponServices?.ActiveWeapon.Value;
                    return w is { IsValid: true } ? w.DesignerName : "";
                }

                return inflictor.DesignerName;
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }
}
