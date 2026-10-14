using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "LastRequestFeatures": the LR quality-of-life features added in 0.15.</summary>
public sealed class LrFeaturesConfig
{
    /// <summary>The !lr menu lists your last RecentCount picks first.</summary>
    public bool RecentPicks { get; set; } = true;
    public int RecentCount { get; set; } = 3;

    /// <summary>Dead players and spectators see the health of both duelists and the LR name.</summary>
    public bool SpectatorPanel { get; set; } = true;

    /// <summary>!lrlog keeps this many last requests of the current map.</summary>
    public int LogSize { get; set; } = 30;
}

/// <summary>A prisoner's most recent LR picks (data/lrrecent.json), newest first.</summary>
internal static class RecentLastRequests
{
    private const string Ns = "lrrecent";

    private static LrFeaturesConfig? Cfg => JailbreakPlugin.Instance?.Config.Section<LrFeaturesConfig>("LastRequestFeatures");

    public static IReadOnlyList<string> Of(JbPlayer p)
    {
        if (Cfg is not { RecentPicks: true } || p.IsBot || p.SteamId64 == 0) return Array.Empty<string>();
        return Storage.Get<List<string>>(Ns, p.SteamId64.ToString()) ?? new List<string>();
    }

    public static void Record(JbPlayer p, string name)
    {
        var cfg = Cfg;
        if (cfg is not { RecentPicks: true } || p.IsBot || p.SteamId64 == 0) return;
        Storage.Set(Ns, p.SteamId64.ToString(), Push(Storage.Get<List<string>>(Ns, p.SteamId64.ToString()), name, Math.Clamp(cfg.RecentCount, 1, 6)));
    }

    /// <summary>Engine-free (unit-tested): put the name first, without duplicates, keeping at most <paramref name="max"/>.</summary>
    internal static List<string> Push(List<string>? list, string name, int max)
    {
        var result = new List<string> { name };
        result.AddRange((list ?? new List<string>()).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        return result.Take(Math.Max(1, max)).ToList();
    }
}

/// <summary>
/// LR features: !lrlog (the last requests of this map: who played which LR and who won), the spectator panel (dead
/// players and spectators see the LR name and both duelists' health - the duel they spectate, else the first one
/// running) and the storage behind the recent picks at the top of the !lr menu.
/// </summary>
public sealed class LrExtrasModule : JbModule
{
    private LrFeaturesConfig Cfg => Section<LrFeaturesConfig>("LastRequestFeatures");

    public override string Name => "LastRequests.Features";
    public override int Order => 46;

    private readonly List<string> _log = new();
    private readonly Dictionary<int, (string Lr, float Started)> _running = new();
    private readonly HashSet<int> _decided = new();

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        JbEvents.LastRequestStarted += OnStarted;
        JbEvents.LastRequestDecided += OnDecided;
        JbEvents.LastRequestEnded += OnEnded;
        Plugin.AddTimer(0.5f, SpectatorPanel, TimerFlags.REPEAT);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.LastRequestStarted -= OnStarted;
        JbEvents.LastRequestDecided -= OnDecided;
        JbEvents.LastRequestEnded -= OnEnded;
    }

    public override void OnMapStart(string mapName)
    {
        _log.Clear();
        _running.Clear();
        _decided.Clear();
    }

    private void OnStarted(JbPlayer prisoner, LrFlags flags)
    {
        var lr = Jb.LastRequests.GetActive(prisoner);
        if (lr == null) return;
        _running[prisoner.Slot] = (lr.Name, Server.CurrentTime);
        _decided.Remove(prisoner.Slot);
    }

    private void OnDecided(JbPlayer winner, JbPlayer loser, JbPlayer prisoner, LastRequestDefinition lr)
    {
        _decided.Add(prisoner.Slot);
        Add(Lang.T("lrlog.won", lr.Name, winner.Name, loser.Name));
    }

    private void OnEnded(JbPlayer prisoner, JbPlayer? opponent)
    {
        if (!_running.Remove(prisoner.Slot, out var r)) return;
        if (_decided.Remove(prisoner.Slot)) return;
        Add(opponent != null ? Lang.T("lrlog.no_winner", r.Lr, prisoner.Name, opponent.Name) : Lang.T("lrlog.solo", r.Lr, prisoner.Name));
    }

    private void Add(string line)
    {
        _log.Add($"[{DateTime.Now:HH:mm}] {Colors.Strip(line)}");
        var max = Math.Clamp(Cfg.LogSize, 5, 200);
        if (_log.Count > max) _log.RemoveRange(0, _log.Count - max);
    }

    [ConsoleCommand("css_lrlog", "sm_lrlog [count] - the last requests of this map and who won")]
    public void CmdLrLog(CCSPlayerController? caller, CommandInfo info)
    {
        var count = 10;
        if (info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var n)) count = Math.Clamp(n, 1, 30);
        if (_log.Count == 0) { Chat.ReplyT(caller, info, "lrlog.empty"); return; }
        Chat.ReplyT(caller, info, "lrlog.title", Math.Min(count, _log.Count));
        foreach (var line in _log.TakeLast(count)) Chat.Reply(caller, info, line);
    }

    // ====================================================================== spectator panel

    private void SpectatorPanel()
    {
        if (!Cfg.SpectatorPanel || _running.Count == 0) return;
        var duels = new List<(JbPlayer Prisoner, JbPlayer Guard, string Lr)>();
        foreach (var (slot, r) in _running)
        {
            var p = Players.Get(slot);
            if (p.Controller is not { IsValid: true } pc || !pc.IsAliveP()) continue;
            var o = Jb.LastRequests.GetOpponent(p);
            if (o?.Controller is not { IsValid: true } oc || !oc.IsAliveP()) continue;
            duels.Add((p, o, r.Lr));
        }

        if (duels.Count == 0) return;
        foreach (var c in Players.Humans())
        {
            if (c.IsAliveP()) continue;
            var watched = Watched(c);
            var duel = duels.FirstOrDefault(d => d.Prisoner.Slot == watched || d.Guard.Slot == watched);
            if (duel.Prisoner == null) duel = duels[0];
            var html = Lang.T("lr.spec.panel", Hud.Escape(duel.Lr), Hud.Escape(duel.Prisoner.Name), duel.Prisoner.Controller!.GetHp(),
                Hud.Escape(duel.Guard.Name), duel.Guard.Controller!.GetHp());
            Hud.Set(c, "lr.spec", html, 15, 0.8f);
        }
    }

    /// <summary>Slot of the player this spectator watches, or -1.</summary>
    private static int Watched(CCSPlayerController c)
    {
        try
        {
            var index = c.ObserverPawn.Value?.ObserverServices?.ObserverTarget.Value?.Index ?? 0;
            if (index == 0) return -1;
            foreach (var x in Players.Alive())
                if (x.PlayerPawn.Value?.Index == index) return x.Slot;
        }
        catch
        {
            // observer data is best effort
        }

        return -1;
    }
}
