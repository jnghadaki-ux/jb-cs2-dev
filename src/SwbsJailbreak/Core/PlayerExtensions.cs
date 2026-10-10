using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

/// <summary>
/// Source 2 safe helpers for common player manipulation. Every setter that touches a networked schema field
/// calls Utilities.SetStateChanged so clients actually receive the change.
/// </summary>
public static class PlayerExtensions
{
    public static bool IsAliveP(this CCSPlayerController? c) => Players.IsAlive(c);

    public static CCSPlayerPawn? PawnOrNull(this CCSPlayerController? c)
    {
        if (c == null || !c.IsValid) return null;
        var pawn = c.PlayerPawn.Value;
        return pawn is { IsValid: true } ? pawn : null;
    }

    public static JbPlayer? Jb(this CCSPlayerController? c) => Players.Get(c);

    // ------------------------------------------------------------------ health / armour

    public static void SetHp(this CCSPlayerController c, int health, bool raiseMax = true)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        if (raiseMax && health > pawn.MaxHealth)
        {
            pawn.MaxHealth = health;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        }

        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    public static void SetMaxHp(this CCSPlayerController c, int maxHealth)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        pawn.MaxHealth = maxHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
    }

    public static int GetHp(this CCSPlayerController c) => c.PawnOrNull()?.Health ?? 0;

    public static void AddHp(this CCSPlayerController c, int amount, int cap = 0)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        var hp = pawn.Health + amount;
        if (cap > 0) hp = Math.Min(hp, cap);
        c.SetHp(hp, cap <= 0);
    }

    public static void SetArmor(this CCSPlayerController c, int armor, bool? helmet = null)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        pawn.ArmorValue = armor;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        if (helmet.HasValue && pawn.ItemServices != null)
        {
            var items = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            items.HasHelmet = helmet.Value;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pItemServices");
        }
    }

    public static void SetMoney(this CCSPlayerController c, int money)
    {
        var ms = c.InGameMoneyServices;
        if (ms == null) return;
        ms.Account = money;
        Utilities.SetStateChanged(c, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    // ------------------------------------------------------------------ rendering

    public static void SetColor(this CCSPlayerController c, Color color)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        pawn.SetEntityColor(color);
    }

    public static void SetEntityColor(this CBaseModelEntity ent, Color color)
    {
        if (!ent.IsValid) return;
        ent.RenderMode = color.A < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
        ent.Render = color;
        Utilities.SetStateChanged(ent, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(ent, "CBaseModelEntity", "m_nRenderMode");
    }

    public static void ResetColor(this CCSPlayerController c) => c.SetColor(Color.FromArgb(255, 255, 255, 255));

    /// <summary>Set the player's alpha (0 invisible .. 255 opaque) and apply the same alpha to their weapons.</summary>
    public static void SetAlpha(this CCSPlayerController c, int alpha, bool includeWeapons = true)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        var col = pawn.Render;
        pawn.SetEntityColor(Color.FromArgb(Math.Clamp(alpha, 0, 255), col.R, col.G, col.B));
        if (!includeWeapons || pawn.WeaponServices == null) return;
        foreach (var h in pawn.WeaponServices.MyWeapons)
        {
            var w = h.Value;
            if (w is { IsValid: true })
            {
                var wc = w.Render;
                w.SetEntityColor(Color.FromArgb(Math.Clamp(alpha, 0, 255), wc.R, wc.G, wc.B));
            }
        }
    }

    // ------------------------------------------------------------------ movement

    public static void SetMoveType(this CCSPlayerController c, MoveType_t moveType)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    /// <summary>Freeze in place (MOVETYPE_OBSOLETE blocks movement but keeps the camera usable).</summary>
    public static void Freeze(this CCSPlayerController c)
    {
        c.SetMoveType(MoveType_t.MOVETYPE_OBSOLETE);
        var pawn = c.PawnOrNull();
        pawn?.Teleport(null, null, new Vector(0, 0, 0));
    }

    public static void Unfreeze(this CCSPlayerController c) => c.SetMoveType(MoveType_t.MOVETYPE_WALK);

    public static bool IsFrozen(this CCSPlayerController c) =>
        c.PawnOrNull()?.MoveType is MoveType_t.MOVETYPE_OBSOLETE or MoveType_t.MOVETYPE_NONE;

    public static bool IsNoclip(this CCSPlayerController c) => c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NOCLIP;

    public static void SetNoclip(this CCSPlayerController c, bool on) =>
        c.SetMoveType(on ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK);

    public static Vector? Origin(this CCSPlayerController c) => c.PawnOrNull()?.AbsOrigin;

    public static Vector EyePosition(this CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        if (pawn?.AbsOrigin == null) return new Vector(0, 0, 0);
        var o = pawn.AbsOrigin;
        return new Vector(o.X, o.Y, o.Z + pawn.ViewOffset.Z);
    }

    public static void TeleportTo(this CCSPlayerController c, Vector pos, QAngle? angles = null, Vector? velocity = null)
    {
        c.PawnOrNull()?.Teleport(pos, angles, velocity ?? new Vector(0, 0, 0));
    }

    public static void SetVelocity(this CCSPlayerController c, Vector velocity)
    {
        c.PawnOrNull()?.Teleport(null, null, velocity);
    }

    public static float HorizontalSpeed(this CCSPlayerController c)
    {
        var v = c.PawnOrNull()?.AbsVelocity;
        return v == null ? 0f : MathF.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    public static bool OnGround(this CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        return pawn != null && (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
    }

    /// <summary>SourceMod-style slap: optional damage plus a random upward push.</summary>
    public static void Slap(this CCSPlayerController c, int damage = 0)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null || !c.IsAliveP()) return;
        var v = pawn.AbsVelocity;
        var nv = new Vector(v.X + Random.Shared.Next(-180, 181), v.Y + Random.Shared.Next(-180, 181), v.Z + Random.Shared.Next(200, 301));
        pawn.Teleport(null, null, nv);
        if (damage <= 0) return;
        var hp = pawn.Health - damage;
        if (hp <= 0) c.CommitSuicide(false, true);
        else c.SetHp(hp, false);
    }

    // ------------------------------------------------------------------ weapons

    /// <summary>Remove every weapon (and optionally give back a knife).</summary>
    public static void Strip(this CCSPlayerController c, bool giveKnife = true)
    {
        if (!c.IsAliveP()) return;
        c.RemoveWeapons();
        if (giveKnife) c.GiveNamedItem("weapon_knife");
    }

    public static void Give(this CCSPlayerController c, string item)
    {
        if (!c.IsAliveP()) return;
        if (!item.StartsWith("weapon_") && !item.StartsWith("item_")) item = "weapon_" + item;
        c.GiveNamedItem(item);
    }

    public static IEnumerable<CBasePlayerWeapon> Weapons(this CCSPlayerController c)
    {
        var ws = c.PawnOrNull()?.WeaponServices;
        if (ws == null) yield break;
        foreach (var h in ws.MyWeapons)
        {
            var w = h.Value;
            if (w is { IsValid: true }) yield return w;
        }
    }

    public static CBasePlayerWeapon? ActiveWeapon(this CCSPlayerController c)
    {
        var w = c.PawnOrNull()?.WeaponServices?.ActiveWeapon.Value;
        return w is { IsValid: true } ? w : null;
    }

    public static bool HasWeapon(this CCSPlayerController c, string designerName) =>
        c.Weapons().Any(w => w.DesignerName == designerName);

    /// <summary>Set clip / reserve ammo on a weapon entity (networked).</summary>
    public static void SetAmmo(this CBasePlayerWeapon weapon, int clip, int reserve = -1)
    {
        if (!weapon.IsValid) return;
        if (clip >= 0)
        {
            weapon.Clip1 = clip;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
        }

        if (reserve >= 0)
        {
            weapon.ReserveAmmo[0] = reserve;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        }
    }

    // ------------------------------------------------------------------ misc

    public static void SetClanTag(this CCSPlayerController c, string tag)
    {
        if (!c.IsValid) return;
        c.Clan = tag;
        Utilities.SetStateChanged(c, "CCSPlayerController", "m_szClan");
    }

    public static void SetName(this CCSPlayerController c, string name)
    {
        if (!c.IsValid) return;
        c.PlayerName = name;
        Utilities.SetStateChanged(c, "CBasePlayerController", "m_iszPlayerName");
    }

    public static void SetScore(this CCSPlayerController c, int score)
    {
        c.Score = score;
        Utilities.SetStateChanged(c, "CCSPlayerController", "m_iScore");
    }

    public static void Kill(this CCSPlayerController c)
    {
        if (c.IsAliveP()) c.CommitSuicide(false, true);
    }

    public static float DistanceTo(this CCSPlayerController a, CCSPlayerController b)
    {
        var pa = a.Origin();
        var pb = b.Origin();
        if (pa == null || pb == null) return float.MaxValue;
        return VectorUtil.Distance(pa, pb);
    }
}

public static class VectorUtil
{
    public static float Distance(Vector a, Vector b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static float Distance2D(Vector a, Vector b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static Vector Copy(Vector v) => new(v.X, v.Y, v.Z);

    public static Vector Add(Vector v, float x, float y, float z) => new(v.X + x, v.Y + y, v.Z + z);

    /// <summary>Forward direction vector for an angle (pitch/yaw), unit length.</summary>
    public static Vector Forward(QAngle angles)
    {
        var pitch = angles.X * MathF.PI / 180f;
        var yaw = angles.Y * MathF.PI / 180f;
        var cp = MathF.Cos(pitch);
        return new Vector(cp * MathF.Cos(yaw), cp * MathF.Sin(yaw), -MathF.Sin(pitch));
    }
}
