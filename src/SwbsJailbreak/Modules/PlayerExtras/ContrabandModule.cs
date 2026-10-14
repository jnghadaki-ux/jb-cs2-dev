using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "Contraband": SWBS CS2 addition, !search (guards) and !snitch (prisoners).</summary>
public sealed class ContrabandConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>!search: how close the guard must be (units), searches per guard per round, seconds before the same
    /// prisoner can be searched again.</summary>
    public float SearchRange { get; set; } = 150f;
    public int SearchesPerRound { get; set; } = 3;
    public float SearchSamePrisonerCooldown { get; set; } = 20f;

    /// <summary>Points for a guard who finds contraband (0 = none).</summary>
    public int SearchFoundPoints { get; set; } = 10;

    /// <summary>!snitch: tips per prisoner per round, points for a correct tip, points lost for a wrong one.</summary>
    public int SnitchesPerRound { get; set; } = 1;
    public int SnitchCorrectPoints { get; set; } = 20;
    public int SnitchWrongPoints { get; set; } = 10;

    /// <summary>Glow the tipped prisoner for guards for this many seconds (0 = no glow).</summary>
    public float SnitchGlowSeconds { get; set; } = 6f;
}

/// <summary>
/// Contraband = any weapon a prisoner carries besides knives and the Jihad bomb, outside last requests and days.
/// !search: an alive guard frisks the prisoner they look at (or name) from close range; contraband is taken away and
/// announced. !snitch &lt;prisoner&gt;: an alive prisoner tips the guards anonymously; the tip is checked at once, a correct
/// one earns points and a wrong one costs points. Both feed !stats and the !rank contraband / snitch boards.
/// </summary>
public sealed class ContrabandModule : JbModule
{
    private ContrabandConfig Cfg => Section<ContrabandConfig>("Contraband");

    public override string Name => "Contraband";
    public override int Order => 46;
    public override bool Enabled => Cfg.Enabled;

    private readonly int[] _searches = new int[Players.MaxSlots];
    private readonly int[] _snitches = new int[Players.MaxSlots];
    private readonly float[] _searchedAt = new float[Players.MaxSlots];
    private readonly Dictionary<int, GlowHandle> _tipGlows = new();

    public override void OnRoundStart()
    {
        Array.Clear(_searches);
        Array.Clear(_snitches);
        Array.Clear(_searchedAt);
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => ClearGlows();

    public override void OnMapStart(string mapName) => _tipGlows.Clear();

    public override void OnUnload(bool hotReload) => ClearGlows();

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev) => RemoveGlow(victim.Slot);

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        RemoveGlow(player.Slot);
        _searches[player.Slot] = _snitches[player.Slot] = 0;
        _searchedAt[player.Slot] = 0;
    }

    // ================================================================= rules

    /// <summary>Engine-free: is this weapon contraband for a prisoner? Knives and the Jihad bomb are allowed (unit-tested).</summary>
    internal static bool IsContraband(string designerName) =>
        designerName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
        && !Weapons.IsKnife(designerName)
        && !designerName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase);

    private static List<CBasePlayerWeapon> Contraband(CCSPlayerController c) =>
        c.Weapons().Where(w => w.IsValid && IsContraband(w.DesignerName)).ToList();

    /// <summary>Why contraband rules do not apply right now, or null when they do.</summary>
    private static string? Blocked(JbPlayer? prisoner)
    {
        if (Jb.Days.IsInProgress) return "cb.day";
        if (Jb.LastRequests.NumInitialized > 0 || (prisoner != null && Jb.LastRequests.IsInLastRequest(prisoner))) return "cb.lr";
        return null;
    }

    private bool StatsCount() =>
        Players.Humans().Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) >= Math.Max(1, Section<StatsConfig>("Stats").MinPlayers);

    private void Stat(JbPlayer? p, Action<PlayerStats> change)
    {
        if (StatsModule.Tracked(p) && StatsCount()) StatsModule.Update(p!, change);
    }

    // ================================================================= !search

    [ConsoleCommand("css_search", "sm_search [prisoner] - (guard) frisk the prisoner in front of you for contraband")]
    [ConsoleCommand("css_frisk", "sm_frisk [prisoner] - (guard) frisk the prisoner in front of you for contraband")]
    public void CmdSearch(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (caller.Team != CsTeam.CounterTerrorist || !caller.IsAliveP()) { Chat.Tell(caller, "cb.search.only_guards"); return; }
        if (_searches[caller.Slot] >= Math.Max(1, Cfg.SearchesPerRound)) { Chat.Tell(caller, "cb.search.limit", Cfg.SearchesPerRound); return; }

        CCSPlayerController? target;
        if (info.ArgCount > 1)
        {
            var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, singleOnly: true, respectImmunity: false);
            if (t == null) return;
            target = t.Players[0];
        }
        else
        {
            target = Aim.LookTarget(caller, Cfg.SearchRange, 12f);
        }

        if (target == null) { Chat.Tell(caller, "cb.search.no_target"); return; }
        if (target.Team != CsTeam.Terrorist) { Chat.Tell(caller, "cb.not_prisoner", target.PlayerName); return; }
        if (target.DistanceTo(caller) > Cfg.SearchRange) { Chat.Tell(caller, "cb.search.too_far", target.PlayerName); return; }
        var tp = target.Jb();
        if (Blocked(tp) is { } why) { Chat.Tell(caller, why); return; }

        var now = (float)Server.EngineTime;
        if (now - _searchedAt[target.Slot] < Cfg.SearchSamePrisonerCooldown && _searchedAt[target.Slot] > 0)
        {
            Chat.Tell(caller, "cb.search.recent", target.PlayerName);
            return;
        }

        _searches[caller.Slot]++;
        _searchedAt[target.Slot] = now;
        var guard = caller.Jb();
        Stat(guard, s => s.SearchesMade++);

        var found = Contraband(target);
        if (found.Count == 0)
        {
            Chat.TellAll("cb.search.clean", caller.PlayerName, target.PlayerName);
            Jb.Log.LogEvent($"{caller.PlayerName} searched {target.PlayerName}: clean", guard, tp, LogType.Item);
            return;
        }

        var names = string.Join(", ", found.Select(w => Weapons.Display(w.DesignerName)).Distinct());
        foreach (var w in found) Jb.Settings.StripWeaponFromOwner(w);
        RemoveGlow(target.Slot);

        Stat(guard, s => s.ContrabandFound++);
        Stat(tp, s => s.TimesCaught++);
        if (Cfg.SearchFoundPoints > 0 && guard != null) Jb.Points.AddPoints(guard, Cfg.SearchFoundPoints, "contraband");
        if (tp != null) Jb.Rebels.MarkRebel(tp, "contraband");

        Chat.TellAll("cb.search.found", caller.PlayerName, target.PlayerName, names);
        Jb.Log.LogEvent($"{caller.PlayerName} found {names} on {target.PlayerName}", guard, tp, LogType.Item);
    }

    // ================================================================= !snitch

    [ConsoleCommand("css_snitch", "sm_snitch <prisoner> - (prisoner) anonymously tip the guards that someone carries a weapon")]
    [ConsoleCommand("css_tip", "sm_tip <prisoner> - (prisoner) anonymously tip the guards that someone carries a weapon")]
    public void CmdSnitch(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (caller.Team != CsTeam.Terrorist || !caller.IsAliveP()) { Chat.Tell(caller, "cb.snitch.only_prisoners"); return; }
        if (info.ArgCount < 2) { Chat.Tell(caller, "core.usage", "!snitch <prisoner>"); return; }
        if (_snitches[caller.Slot] >= Math.Max(1, Cfg.SnitchesPerRound)) { Chat.Tell(caller, "cb.snitch.limit"); return; }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        if (target.Slot == caller.Slot) { Chat.Tell(caller, "cb.snitch.self"); return; }
        if (target.Team != CsTeam.Terrorist) { Chat.Tell(caller, "cb.not_prisoner", target.PlayerName); return; }
        var tp = target.Jb();
        if (Blocked(tp) is { } why) { Chat.Tell(caller, why); return; }

        _snitches[caller.Slot]++;
        var snitch = caller.Jb();
        var correct = Contraband(target).Count > 0;
        Stat(snitch, s =>
        {
            s.SnitchTips++;
            if (correct) s.SnitchCorrect++;
        });

        if (snitch != null)
        {
            if (correct && Cfg.SnitchCorrectPoints > 0) Jb.Points.AddPoints(snitch, Cfg.SnitchCorrectPoints, "snitch");
            if (!correct && Cfg.SnitchWrongPoints > 0) Jb.Points.AddPoints(snitch, -Cfg.SnitchWrongPoints, "snitch");
        }

        Chat.Tell(caller, correct ? "cb.snitch.correct" : "cb.snitch.wrong", target.PlayerName, correct ? Cfg.SnitchCorrectPoints : Cfg.SnitchWrongPoints);

        // The guards always hear the tip (they still have to !search); the snitch stays anonymous.
        var line = Lang.T("cb.snitch.tip", target.PlayerName);
        foreach (var g in Players.Humans().Where(c => c.Team == CsTeam.CounterTerrorist || Perms.IsAdmin(c)))
            Chat.Print(g, line);

        if (Cfg.SnitchGlowSeconds > 0)
        {
            RemoveGlow(target.Slot);
            var h = Glow.Attach(target, System.Drawing.Color.FromArgb(255, 255, 140, 0), v => v.Team == CsTeam.CounterTerrorist);
            if (h != null)
            {
                _tipGlows[target.Slot] = h;
                var slot = target.Slot;
                Round.Timer(Cfg.SnitchGlowSeconds, () =>
                {
                    if (_tipGlows.TryGetValue(slot, out var cur) && ReferenceEquals(cur, h)) RemoveGlow(slot);
                });
            }
        }

        Jb.Log.LogEvent($"{caller.PlayerName} snitched on {target.PlayerName} ({(correct ? "correct" : "wrong")})", snitch, tp, LogType.Item);
    }

    private void RemoveGlow(int slot)
    {
        if (_tipGlows.Remove(slot, out var h)) Glow.Remove(h);
    }

    private void ClearGlows()
    {
        foreach (var h in _tipGlows.Values) Glow.Remove(h);
        _tipGlows.Clear();
    }
}
