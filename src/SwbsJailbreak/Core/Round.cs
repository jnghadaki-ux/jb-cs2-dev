using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Core;

public enum RoundPhase
{
    /// <summary>Map loading / warmup / between rounds.</summary>
    Inactive,

    /// <summary>round_start fired, freeze time running.</summary>
    FreezeTime,

    /// <summary>Freeze time over, round being played.</summary>
    Active,

    /// <summary>round_end fired, waiting for next round.</summary>
    Ended
}

/// <summary>Round state + game rules helpers + round-scoped timers.</summary>
public static class Round
{
    private static readonly List<Timer> RoundTimers = new();

    public static RoundPhase Phase { get; internal set; } = RoundPhase.Inactive;

    /// <summary>Incremented on every round_start since map start.</summary>
    public static int Number { get; internal set; }

    /// <summary>Server.CurrentTime at round_start.</summary>
    public static float StartTime { get; internal set; }

    /// <summary>Server.CurrentTime at round_freeze_end (or round start if no freeze time).</summary>
    public static float FreezeEndTime { get; internal set; }

    public static bool IsActive => Phase == RoundPhase.Active;

    public static bool IsOver => Phase is RoundPhase.Ended or RoundPhase.Inactive;

    /// <summary>Seconds since freeze time ended (0 before).</summary>
    public static float Elapsed => Phase == RoundPhase.Active ? Server.CurrentTime - FreezeEndTime : 0f;

    public static CCSGameRules? GameRules
    {
        get
        {
            try
            {
                return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool IsWarmup => GameRules?.WarmupPeriod ?? false;

    /// <summary>Seconds left on the round clock.</summary>
    public static float TimeLeft
    {
        get
        {
            var gr = GameRules;
            if (gr == null) return 0;
            return gr.RoundStartTime + gr.RoundTime - Server.CurrentTime;
        }
    }

    /// <summary>Change the remaining round time (seconds from now).</summary>
    public static void SetTimeLeft(float seconds)
    {
        var gr = GameRules;
        if (gr == null) return;
        gr.RoundTime = (int)Math.Ceiling(Server.CurrentTime - gr.RoundStartTime + seconds);
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy != null) Utilities.SetStateChanged(proxy, "CCSGameRulesProxy", "m_pGameRules");
    }

    /// <summary>End the round with the given winner (TerminateRound).</summary>
    public static void End(CsTeam winner, float delay = 5f)
    {
        var reason = winner switch
        {
            CsTeam.Terrorist => RoundEndReason.TerroristsWin,
            CsTeam.CounterTerrorist => RoundEndReason.CTsWin,
            _ => RoundEndReason.RoundDraw
        };
        End(reason, delay);
    }

    public static void End(RoundEndReason reason, float delay = 5f)
    {
        try
        {
            GameRules?.TerminateRound(delay, reason);
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "TerminateRound failed");
        }
    }

    /// <summary>Timer that is killed automatically on round end / map change.</summary>
    public static Timer? Timer(float interval, Action callback, bool repeat = false)
    {
        var plugin = JailbreakPlugin.Instance;
        if (plugin == null) return null;
        var flags = TimerFlags.STOP_ON_MAPCHANGE | (repeat ? TimerFlags.REPEAT : 0);
        Timer? t = null;
        t = plugin.AddTimer(interval, () =>
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                plugin.Logger.LogError(ex, "Round timer callback failed");
            }
            finally
            {
                if (!repeat && t != null) RoundTimers.Remove(t);
            }
        }, flags);
        RoundTimers.Add(t);
        return t;
    }

    internal static void KillRoundTimers()
    {
        foreach (var t in RoundTimers.ToList())
        {
            try { t.Kill(); }
            catch { /* already dead */ }
        }

        RoundTimers.Clear();
    }

    public static int CvarInt(string name, int fallback = 0)
    {
        try
        {
            var cv = ConVar.Find(name);
            if (cv == null) return fallback;
            return int.TryParse(cv.StringValue, out var v) ? v : (int)(float.TryParse(cv.StringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback);
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>
/// Temporary server cvar overrides (e.g. a day sets sv_gravity), restored with <see cref="RestoreAll"/> or by key.
/// </summary>
public static class Cvars
{
    private static readonly Dictionary<string, string> Originals = new(StringComparer.OrdinalIgnoreCase);

    public static string? Get(string name)
    {
        try { return ConVar.Find(name)?.StringValue; }
        catch { return null; }
    }

    /// <summary>Set a cvar, remembering its previous value the first time.</summary>
    public static void SetTemp(string name, string value)
    {
        if (!Originals.ContainsKey(name))
        {
            var cur = Get(name);
            if (cur != null) Originals[name] = cur;
        }

        Server.ExecuteCommand($"{name} {value}");
    }

    public static void Restore(string name)
    {
        if (Originals.Remove(name, out var v)) Server.ExecuteCommand($"{name} {v}");
    }

    public static void RestoreAll()
    {
        foreach (var (k, v) in Originals.ToList()) Server.ExecuteCommand($"{k} {v}");
        Originals.Clear();
    }

    public static void Set(string name, string value) => Server.ExecuteCommand($"{name} {value}");
}
