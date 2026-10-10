using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace SwbsJailbreak.Core;

/// <summary>Implement on per-player module state that must be cleared every round_start.</summary>
public interface IRoundScoped
{
    void ResetForRound();
}

/// <summary>
/// Per-slot player wrapper. Controllers/pawns are never cached across frames (Source 2 entities can be
/// re-created), they are looked up from the slot on access. Module specific state lives in typed bags
/// (<see cref="Data{T}"/>) so modules never need to touch this file.
/// </summary>
public sealed class JbPlayer
{
    private readonly Dictionary<Type, object> _data = new();

    public JbPlayer(int slot)
    {
        Slot = slot;
    }

    public int Slot { get; }

    /// <summary>Entity index of the controller (slot + 1).</summary>
    public int Index => Slot + 1;

    public ulong SteamId64 { get; internal set; }

    /// <summary>Name cached at connect / refreshed on access, usable after disconnect.</summary>
    public string CachedName { get; internal set; } = "unknown";

    public bool Connected { get; internal set; }

    public bool IsBot { get; internal set; }

    /// <summary>Server time (Server.CurrentTime) when the player fully joined.</summary>
    public float ConnectTime { get; internal set; }

    // ----- cross-module status flags (owning module noted) -----

    /// <summary>Owned by the Rebel module: prisoner marked as rebel this round.</summary>
    public bool IsRebel { get; set; }

    /// <summary>Owned by the Freeday/LR modules: prisoner has a freeday this round.</summary>
    public bool IsFreeday { get; set; }

    /// <summary>Freeday queued for next round (LR freeday / warden granted).</summary>
    public bool FreedayNextRound { get; set; }

    public CCSPlayerController? Controller
    {
        get
        {
            var c = Utilities.GetPlayerFromSlot(Slot);
            return c is { IsValid: true } ? c : null;
        }
    }

    public bool IsValid => Connected && Controller is { IsValid: true, Connected: PlayerConnectedState.Connected };

    public CCSPlayerPawn? Pawn
    {
        get
        {
            var c = Controller;
            if (c == null) return null;
            var pawn = c.PlayerPawn.Value;
            return pawn is { IsValid: true } ? pawn : null;
        }
    }

    public bool IsAlive
    {
        get
        {
            var c = Controller;
            if (c == null || !c.PawnIsAlive) return false;
            var pawn = c.PlayerPawn.Value;
            return pawn is { IsValid: true } && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;
        }
    }

    public CsTeam Team => Controller?.Team ?? CsTeam.None;

    public bool IsPrisoner => Team == CsTeam.Terrorist;

    public bool IsGuard => Team == CsTeam.CounterTerrorist;

    public string Name
    {
        get
        {
            var c = Controller;
            if (c != null && !string.IsNullOrEmpty(c.PlayerName)) CachedName = c.PlayerName;
            return CachedName;
        }
    }

    /// <summary>Typed per-player state bag for modules.</summary>
    public T Data<T>() where T : class, new()
    {
        if (!_data.TryGetValue(typeof(T), out var obj))
        {
            obj = new T();
            _data[typeof(T)] = obj;
        }

        return (T)obj;
    }

    internal void ResetRound()
    {
        IsRebel = false;
        IsFreeday = false;
        foreach (var obj in _data.Values)
            if (obj is IRoundScoped rs)
                rs.ResetForRound();
    }

    internal void ResetAll()
    {
        _data.Clear();
        IsRebel = false;
        IsFreeday = false;
        FreedayNextRound = false;
        SteamId64 = 0;
        CachedName = "unknown";
        IsBot = false;
        Connected = false;
    }

    public override string ToString() => $"{CachedName}<{Slot}>";
}
