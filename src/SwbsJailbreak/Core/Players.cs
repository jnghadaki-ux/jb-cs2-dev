using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace SwbsJailbreak.Core;

/// <summary>Registry of <see cref="JbPlayer"/> wrappers plus common player queries.</summary>
public static class Players
{
    public const int MaxSlots = 64;

    private static readonly JbPlayer[] Slots = Enumerable.Range(0, MaxSlots).Select(i => new JbPlayer(i)).ToArray();

    public static JbPlayer Get(int slot) => Slots[Math.Clamp(slot, 0, MaxSlots - 1)];

    public static JbPlayer? Get(CCSPlayerController? controller)
    {
        if (controller == null || !controller.IsValid || controller.IsHLTV) return null;
        var slot = controller.Slot;
        return slot is >= 0 and < MaxSlots ? Slots[slot] : null;
    }

    /// <summary>Every JbPlayer whose slot is currently occupied by a connected player (bots included).</summary>
    public static IEnumerable<JbPlayer> Connected() => Slots.Where(p => p.Connected && p.Controller != null);

    /// <summary>Valid, connected, non-HLTV controllers (bots included).</summary>
    public static IEnumerable<CCSPlayerController> All() =>
        Utilities.GetPlayers().Where(c => c is { IsValid: true, IsHLTV: false, Connected: PlayerConnectedState.Connected });

    /// <summary>Valid, connected, non-bot controllers.</summary>
    public static IEnumerable<CCSPlayerController> Humans() => All().Where(c => !c.IsBot);

    public static IEnumerable<CCSPlayerController> OnTeam(CsTeam team) => All().Where(c => c.Team == team);

    public static IEnumerable<CCSPlayerController> Alive() => All().Where(IsAlive);

    public static IEnumerable<CCSPlayerController> AliveOnTeam(CsTeam team) => All().Where(c => c.Team == team && IsAlive(c));

    public static IEnumerable<CCSPlayerController> Prisoners() => OnTeam(CsTeam.Terrorist);
    public static IEnumerable<CCSPlayerController> Guards() => OnTeam(CsTeam.CounterTerrorist);
    public static IEnumerable<CCSPlayerController> AlivePrisoners() => AliveOnTeam(CsTeam.Terrorist);
    public static IEnumerable<CCSPlayerController> AliveGuards() => AliveOnTeam(CsTeam.CounterTerrorist);

    public static int CountAlive(CsTeam team) => AliveOnTeam(team).Count();
    public static int Count(CsTeam team) => OnTeam(team).Count();

    public static bool IsAlive(CCSPlayerController? c)
    {
        if (c == null || !c.IsValid || !c.PawnIsAlive) return false;
        var pawn = c.PlayerPawn.Value;
        return pawn is { IsValid: true } && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;
    }

    public static CCSPlayerController? FindByName(string partial)
    {
        var matches = All().Where(c => c.PlayerName.Contains(partial, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : matches.FirstOrDefault(c => string.Equals(c.PlayerName, partial, StringComparison.OrdinalIgnoreCase));
    }

    internal static void OnConnect(CCSPlayerController c)
    {
        var p = Get(c.Slot);
        p.ResetAll();
        p.Connected = true;
        p.IsBot = c.IsBot;
        p.SteamId64 = c.SteamID;
        p.CachedName = c.PlayerName;
        p.ConnectTime = Server.CurrentTime;
    }

    internal static void OnDisconnect(int slot)
    {
        if (slot is < 0 or >= MaxSlots) return;
        Slots[slot].ResetAll();
    }

    internal static void ResetRound()
    {
        foreach (var p in Slots)
            if (p.Connected)
                p.ResetRound();
    }

    internal static void ResetAllSlots()
    {
        foreach (var p in Slots) p.ResetAll();
    }
}
