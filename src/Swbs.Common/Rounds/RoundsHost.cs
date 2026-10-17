using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;

namespace Swbs.Common;

/// <summary>
/// Implemented by a game-mode plugin that runs round variations (the DM and SurfRPGDM plugins). Plugins without it never
/// load the rounds system, so nothing changes on the other servers.
/// </summary>
public interface IRoundsHost
{
    /// <summary>Variation tags this mode runs ("dm", "surf").</summary>
    IReadOnlyCollection<string> RoundTags { get; }

    /// <summary>Add to the player's persistent stats (shown in !stats), e.g. "round_mvps".</summary>
    void AddRoundStat(SwbsPlayer player, string counter, double amount);

    /// <summary>Outline a player for everyone (the bounty, the juggernaut); null removes it.</summary>
    void HighlightPlayer(CCSPlayerController player, Color? color);
}

/// <summary>The round clock on the game rules (the same in every game mode).</summary>
public static class RoundClock
{
    private static CCSGameRulesProxy? Proxy()
    {
        try { return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(); }
        catch { return null; }
    }

    public static bool IsWarmup => Proxy()?.GameRules?.WarmupPeriod ?? false;

    public static float TimeLeft => Proxy()?.GameRules is { } gr ? gr.RoundStartTime + gr.RoundTime - Server.CurrentTime : 0f;

    public static void SetTimeLeft(float seconds)
    {
        var proxy = Proxy();
        if (proxy?.GameRules is not { } gr) return;
        gr.RoundTime = (int)Math.Ceiling(Server.CurrentTime - gr.RoundStartTime + seconds);
        Utilities.SetStateChanged(proxy, "CCSGameRulesProxy", "m_pGameRules");
    }

    public static void End(float delay)
    {
        try { Proxy()?.GameRules?.TerminateRound(delay, RoundEndReason.RoundDraw); }
        catch { /* game rules gone (map change) */ }
    }

    public static bool FreeForAll => Cvars.Get("mp_teammates_are_enemies") == "1";
}
