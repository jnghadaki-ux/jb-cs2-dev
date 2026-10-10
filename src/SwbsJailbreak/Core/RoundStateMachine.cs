namespace SwbsJailbreak.Core;

/// <summary>
/// Authoritative jailbreak round state. Pure logic (no engine calls) so it can be unit tested.
/// The plugin drives it from real CS2 events (round_prestart/round_start/round_freeze_end/round_end, map start/end)
/// and from the cell module (cells opened). Illegal or duplicate transitions are rejected and reported, which is
/// what prevents double round-start/round-end handling.
/// </summary>
public sealed class RoundStateMachine
{
    private static readonly Dictionary<RoundPhase, RoundPhase[]> Legal = new()
    {
        [RoundPhase.Inactive] = new[] { RoundPhase.WaitingForPlayers, RoundPhase.FreezeTime, RoundPhase.Active },
        [RoundPhase.WaitingForPlayers] = new[] { RoundPhase.Inactive, RoundPhase.FreezeTime, RoundPhase.Active, RoundPhase.Ended },
        [RoundPhase.FreezeTime] = new[] { RoundPhase.Inactive, RoundPhase.Active, RoundPhase.Ended },
        [RoundPhase.Active] = new[] { RoundPhase.Inactive, RoundPhase.Ended },
        [RoundPhase.Ended] = new[] { RoundPhase.Inactive, RoundPhase.WaitingForPlayers, RoundPhase.FreezeTime, RoundPhase.Active },
    };

    public RoundPhase Phase { get; private set; } = RoundPhase.Inactive;

    /// <summary>Cells opened during the current round (reset on every new round).</summary>
    public bool CellsOpened { get; private set; }

    /// <summary>Incremented on every accepted round start (FreezeTime, or Active when there is no freeze time).</summary>
    public int RoundNumber { get; private set; }

    /// <summary>Last rejected transition, for diagnostics.</summary>
    public string? LastRejection { get; private set; }

    public static bool IsLegal(RoundPhase from, RoundPhase to) =>
        from != to && Legal.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>Attempt a transition. Returns false (and records why) when it is illegal or a duplicate.</summary>
    public bool TryTransition(RoundPhase to)
    {
        if (!IsLegal(Phase, to))
        {
            LastRejection = Phase == to ? $"duplicate transition to {to}" : $"illegal transition {Phase} -> {to}";
            return false;
        }

        var startsRound = to == RoundPhase.FreezeTime || (to == RoundPhase.Active && Phase != RoundPhase.FreezeTime);
        if (startsRound)
        {
            RoundNumber++;
            CellsOpened = false;
        }

        if (to is RoundPhase.Inactive or RoundPhase.WaitingForPlayers) CellsOpened = false;
        Phase = to;
        LastRejection = null;
        return true;
    }

    /// <summary>Mark the cells opened. Only meaningful (and only accepted once) while a round is in progress.</summary>
    public bool MarkCellsOpened()
    {
        if (CellsOpened || Phase is not (RoundPhase.FreezeTime or RoundPhase.Active))
        {
            LastRejection = CellsOpened ? "cells already opened this round" : $"cells cannot open during {Phase}";
            return false;
        }

        CellsOpened = true;
        return true;
    }

    /// <summary>Map change: back to Inactive and reset the round counter.</summary>
    public void ResetForMap()
    {
        Phase = RoundPhase.Inactive;
        CellsOpened = false;
        RoundNumber = 0;
        LastRejection = null;
    }

    /// <summary>Force a phase without validation (late load / hot reload synchronisation only).</summary>
    public void Synchronise(RoundPhase phase, bool cellsOpened = false)
    {
        Phase = phase;
        CellsOpened = cellsOpened;
        LastRejection = null;
    }
}
