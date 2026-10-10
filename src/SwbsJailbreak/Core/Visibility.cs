using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>
/// Per-player visibility for helper entities (the original sent editor beams with TE_SendToClient to one admin only).
/// Restricted entities are removed from every other player's transmit list in CheckTransmit.
/// </summary>
public static class Visibility
{
    private static readonly Dictionary<uint, (CEntityInstance Entity, Func<CCSPlayerController, bool> VisibleTo)> Restricted = new();

    public static int Count => Restricted.Count;

    public static void OnlyFor(CEntityInstance? entity, Func<CCSPlayerController, bool> visibleTo)
    {
        if (entity == null || !entity.IsValid) return;
        Restricted[entity.EntityHandle.Raw] = (entity, visibleTo);
    }

    public static void OnlyFor(CEntityInstance? entity, int slot) => OnlyFor(entity, p => p.Slot == slot);

    public static void Release(CEntityInstance? entity)
    {
        if (entity != null) Restricted.Remove(entity.EntityHandle.Raw);
    }

    internal static void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (Restricted.Count == 0) return;
        List<uint>? dead = null;
        foreach (var (key, value) in Restricted)
        {
            if (!value.Entity.IsValid) (dead ??= new List<uint>()).Add(key);
        }

        if (dead != null)
            foreach (var k in dead) Restricted.Remove(k);

        foreach (var (info, player) in infoList)
        {
            if (player == null || !player.IsValid) continue;
            foreach (var (_, value) in Restricted)
            {
                bool visible;
                try { visible = value.VisibleTo(player); }
                catch { visible = false; }
                if (!visible) info.TransmitEntities.Remove(value.Entity);
            }
        }
    }

    internal static void Reset() => Restricted.Clear();
}
