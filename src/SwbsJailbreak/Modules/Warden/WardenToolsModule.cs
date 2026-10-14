using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>Section "WardenTools": SWBS CS2 additions (not in the SMPlugins original) common on other jailbreak servers.</summary>
public sealed class WardenToolsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>!rand: seconds between picks; seconds the picked prisoner is tinted (0 = no tint).</summary>
    public float RandCooldown { get; set; } = 3f;
    public float RandHighlightSeconds { get; set; } = 5f;
    public string RandHighlightColor { get; set; } = "255,215,0";

    /// <summary>!ff: prisoners can hurt each other (guards still cannot hurt guards). Turned off at round end, LR and days.</summary>
    public bool FriendlyFireEnabled { get; set; } = true;

    /// <summary>!extend [seconds]: default / maximum seconds added per use, and uses per round.</summary>
    public int ExtendSeconds { get; set; } = 60;
    public int ExtendMaxSeconds { get; set; } = 180;
    public int ExtendPerRound { get; set; } = 2;

    /// <summary>!deputy: one guard helps the warden (may use !rand, !ff) and takes over when the warden dies.</summary>
    public bool DeputyEnabled { get; set; } = true;
    public bool DeputySucceedsWarden { get; set; } = true;
    public string DeputyColor { get; set; } = "120,180,255";

    /// <summary>!repeat: per-prisoner cooldown and a global cooldown so the warden is not spammed.</summary>
    public float RepeatCooldown { get; set; } = 30f;
    public float RepeatGlobalCooldown { get; set; } = 8f;
    public string RepeatSound { get; set; } = "UIPanorama.generic_button_press";

    /// <summary>!votewarden: share of human prisoners + guards (excluding the warden) needed, the minimum votes, and the
    /// seconds a new warden is safe from votes.</summary>
    public bool VoteWardenEnabled { get; set; } = true;
    public double VoteWardenRatio { get; set; } = 0.6;
    public int VoteWardenMinVotes { get; set; } = 3;
    public float VoteWardenGraceSeconds { get; set; } = 20f;

    /// <summary>!split [2-4]: colour-coded prisoner teams for warden games.</summary>
    public bool SplitEnabled { get; set; } = true;
    public float SplitHudSeconds { get; set; } = 6f;
}

/// <summary>
/// Warden helpers added for SWBS CS2: !rand (random prisoner), !ff (prisoner friendly fire), !extend (round time),
/// !deputy (assistant warden and successor) and !repeat (prisoners ask the warden to repeat the last order).
/// </summary>
public sealed class WardenToolsModule : JbModule
{
    private WardenToolsConfig Cfg => Section<WardenToolsConfig>("WardenTools");

    public override string Name => "WardenTools";
    public override int Order => 82;
    public override bool Enabled => Cfg.Enabled;

    private const string FfCvar = "mp_teammates_are_enemies";

    private bool _ffOn;
    private int _extendsThisRound;
    private float _nextRand;
    private float _nextRepeatGlobal;
    private readonly float[] _nextRepeat = new float[Players.MaxSlots];
    private JbPlayer? _deputy;
    private Color? _deputySavedColor;

    private readonly HashSet<int> _wardenVotes = new();
    private float _wardenSince;

    /// <summary>!split team index per prisoner slot.</summary>
    private readonly Dictionary<int, int> _splitTeam = new();

    private static readonly Color[] SplitColors =
    {
        Color.FromArgb(255, 255, 60, 60), Color.FromArgb(255, 70, 120, 255), Color.FromArgb(255, 60, 220, 90), Color.FromArgb(255, 255, 220, 40)
    };

    private static readonly string[] SplitHex = { "#FF3C3C", "#4678FF", "#3CDC5A", "#FFDC28" };

    public override void OnLoad(bool hotReload)
    {
        DamageService.Register(Name, 40, OnDamage);
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.WardenDeath += OnWardenDeath;
        JbEvents.LastRequestInitialized += OnLastRequestInitialized;
        JbEvents.DayStarted += OnDayStarted;
        JbEvents.WardenSelected += OnWardenSelected;
        JbEvents.WardenRemoved += OnWardenRemoved;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.WardenDeath -= OnWardenDeath;
        JbEvents.LastRequestInitialized -= OnLastRequestInitialized;
        JbEvents.DayStarted -= OnDayStarted;
        JbEvents.WardenSelected -= OnWardenSelected;
        JbEvents.WardenRemoved -= OnWardenRemoved;
        DamageService.Unregister(OnDamage);
        SetFriendlyFire(false, null);
        ClearDeputy(false);
    }

    // ================================================================= lifecycle

    public override void OnMapStart(string mapName)
    {
        _ffOn = false;
        _deputy = null;
        _deputySavedColor = null;
        _nextRand = _nextRepeatGlobal = 0;
        Array.Clear(_nextRepeat);
        _wardenVotes.Clear();
        _splitTeam.Clear();
    }

    public override void OnRoundStart() => _extendsThisRound = 0;

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        SetFriendlyFire(false, null);
        ClearDeputy(false);
        _wardenVotes.Clear();
        _splitTeam.Clear();
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (IsDeputy(victim)) ClearDeputy(true);
        _splitTeam.Remove(victim.Slot);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (IsDeputy(player)) ClearDeputy(false);
        _nextRepeat[player.Slot] = 0;
        _wardenVotes.Remove(player.Slot);
        _splitTeam.Remove(player.Slot);
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (IsDeputy(player) && newTeam != CsTeam.CounterTerrorist) ClearDeputy(!disconnect);
        if (newTeam is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) _wardenVotes.Remove(player.Slot);
        _splitTeam.Remove(player.Slot);
    }

    private void OnLastRequestInitialized(JbPlayer prisoner)
    {
        if (_ffOn) SetFriendlyFire(false, Lang.T("wt.ff.off_lr"));
        ClearSplit(false); // LRs tint and beacon players themselves
    }

    private void OnWardenSelected(JbPlayer warden)
    {
        _wardenVotes.Clear();
        _wardenSince = (float)Server.EngineTime;
    }

    private void OnWardenRemoved(JbPlayer warden) => _wardenVotes.Clear();

    private void OnDayStarted(JbPlayer? starter, DayType type, bool freeForAll)
    {
        if (!_ffOn) return;
        if (freeForAll)
        {
            // An FFA day already set mp_teammates_are_enemies (the original value is still the one saved by !ff), and
            // restores it when it ends: hand the cvar over instead of resetting it under the day.
            _ffOn = false;
            return;
        }

        SetFriendlyFire(false, Lang.T("wt.ff.off_day"));
    }

    // ================================================================= access

    private bool IsDeputy(JbPlayer? p) => p != null && _deputy != null && ReferenceEquals(_deputy, p);

    private bool IsDeputy(CCSPlayerController? c) => c != null && _deputy is { Connected: true } d && d.Slot == c.Slot;

    /// <summary>Warden, the warden's deputy, or an admin.</summary>
    private bool CanCommand(CCSPlayerController? c, bool deputyAllowed) =>
        c == null || Jb.Warden.IsWarden(c) || (deputyAllowed && IsDeputy(c) && c.IsAliveP()) || Perms.IsAdmin(c);

    // ================================================================= !rand

    [ConsoleCommand("css_rand", "sm_rand - (warden/deputy) pick a random alive prisoner")]
    [ConsoleCommand("css_random", "sm_random - (warden/deputy) pick a random alive prisoner")]
    public void CmdRand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!CanCommand(caller, true)) { Chat.ReplyT(caller, info, "wt.not_warden"); return; }
        var now = (float)Server.EngineTime;
        if (now < _nextRand) { Chat.ReplyT(caller, info, "wt.wait"); return; }

        var pool = Players.AlivePrisoners()
            .Where(c => c.Jb() is { } p && !Jb.LastRequests.IsInLastRequest(p))
            .ToList();
        if (pool.Count == 0) { Chat.ReplyT(caller, info, "wt.rand.none"); return; }

        _nextRand = now + Math.Max(0f, Cfg.RandCooldown);
        var pick = pool[Random.Shared.Next(pool.Count)];
        Chat.TellAll("wt.rand.result", caller?.PlayerName ?? "Console", pick.PlayerName);
        Hud.SetGlobal("wt.rand", Lang.T("wt.rand.hud", Hud.Escape(pick.PlayerName)), 60, 4f);

        if (Cfg.RandHighlightSeconds > 0 && TryParseColor(Cfg.RandHighlightColor, out var color))
        {
            pick.SetColor(color);
            var slot = pick.Slot;
            Round.Timer(Cfg.RandHighlightSeconds, () =>
            {
                var c = Players.Get(slot).Controller;
                if (c != null && c.IsAliveP() && !IsDeputy(c)) RestoreColor(c);
            });
        }

        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} picked {pick.PlayerName} at random", caller.Jb(), pick.Jb(), LogType.Warden);
    }

    // ================================================================= !ff

    [ConsoleCommand("css_ff", "sm_ff - (warden/deputy) toggle friendly fire between prisoners")]
    [ConsoleCommand("css_friendlyfire", "sm_friendlyfire - (warden/deputy) toggle friendly fire between prisoners")]
    public void CmdFriendlyFire(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Cfg.FriendlyFireEnabled) { Chat.ReplyT(caller, info, "wt.disabled"); return; }
        if (!CanCommand(caller, true)) { Chat.ReplyT(caller, info, "wt.not_warden"); return; }
        if (!_ffOn && Jb.Days.IsInProgress) { Chat.ReplyT(caller, info, "wt.ff.day"); return; }
        if (!_ffOn && Jb.LastRequests.NumInitialized > 0) { Chat.ReplyT(caller, info, "wt.ff.lr"); return; }
        SetFriendlyFire(!_ffOn, Lang.T(_ffOn ? "wt.ff.off" : "wt.ff.on", caller?.PlayerName ?? "Console"));
        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} turned prisoner friendly fire {(_ffOn ? "on" : "off")}", caller.Jb(), null, LogType.Warden);
    }

    private void SetFriendlyFire(bool on, string? announce)
    {
        if (on == _ffOn) return;
        _ffOn = on;
        if (on) Cvars.SetTemp(FfCvar, "1");
        else Cvars.Restore(FfCvar);
        if (announce != null) Chat.PrintAll(announce);
    }

    /// <summary>While !ff is on only prisoners may hurt teammates: guard-on-guard damage stays blocked.</summary>
    private HookResult OnDamage(DamageContext ctx)
    {
        if (!_ffOn || ctx.Attacker == null || ctx.IsSelf) return HookResult.Continue;
        if (ctx.Attacker.Team == CsTeam.CounterTerrorist && ctx.Victim.Team == CsTeam.CounterTerrorist) return HookResult.Handled;
        return HookResult.Continue;
    }

    // ================================================================= !extend

    [ConsoleCommand("css_extend", "sm_extend [seconds] - (warden) add time to the round")]
    public void CmdExtend(CCSPlayerController? caller, CommandInfo info)
    {
        if (!CanCommand(caller, false)) { Chat.ReplyT(caller, info, "wt.not_warden"); return; }
        if (!Round.IsActive) { Chat.ReplyT(caller, info, "wt.extend.not_active"); return; }
        var admin = caller == null || Perms.IsAdmin(caller);
        if (!admin && _extendsThisRound >= Cfg.ExtendPerRound) { Chat.ReplyT(caller, info, "wt.extend.limit", Cfg.ExtendPerRound); return; }

        var seconds = Cfg.ExtendSeconds;
        if (info.ArgCount > 1 && (!int.TryParse(info.GetArg(1), out seconds) || seconds < 1))
        {
            Chat.ReplyT(caller, info, "core.usage", "!extend [seconds]");
            return;
        }

        seconds = Math.Clamp(seconds, 1, Math.Max(1, Cfg.ExtendMaxSeconds));
        Round.SetTimeLeft(Math.Max(0f, Round.TimeLeft) + seconds);
        if (!admin) _extendsThisRound++;
        Chat.TellAll("wt.extend.done", caller?.PlayerName ?? "Console", seconds);
        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} extended the round by {seconds}s", caller.Jb(), null, LogType.Warden);
    }

    // ================================================================= !deputy

    [ConsoleCommand("css_deputy", "sm_deputy [player] - (warden) appoint a guard as deputy; no name removes the deputy")]
    public void CmdDeputy(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Cfg.DeputyEnabled) { Chat.ReplyT(caller, info, "wt.disabled"); return; }
        if (!CanCommand(caller, false)) { Chat.ReplyT(caller, info, "wt.not_warden"); return; }

        if (info.ArgCount < 2)
        {
            if (_deputy == null) { Chat.ReplyT(caller, info, "wt.deputy.usage"); return; }
            Chat.TellAll("wt.deputy.removed", _deputy.Name);
            ClearDeputy(false);
            return;
        }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, aliveOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        var p = target.Jb();
        if (p == null) return;
        if (target.Team != CsTeam.CounterTerrorist) { Chat.ReplyT(caller, info, "wt.deputy.not_guard", target.PlayerName); return; }
        if (Jb.Warden.IsWarden(target)) { Chat.ReplyT(caller, info, "wt.deputy.is_warden"); return; }

        ClearDeputy(false);
        _deputy = p;
        if (TryParseColor(Cfg.DeputyColor, out var color))
        {
            _deputySavedColor = Color.White;
            target.SetColor(color);
        }

        Chat.TellAll("wt.deputy.set", caller?.PlayerName ?? "Console", target.PlayerName);
        Chat.Tell(target, "wt.deputy.you");
        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} made {target.PlayerName} deputy", caller.Jb(), p, LogType.Warden);
    }

    private void ClearDeputy(bool announceDeath)
    {
        var d = _deputy;
        if (d == null) return;
        _deputy = null;
        var c = d.Controller;
        if (_deputySavedColor != null && c != null && c.IsValid && c.IsAliveP()) c.ResetColor();
        _deputySavedColor = null;
        if (announceDeath) Chat.TellAll("wt.deputy.died", d.Name);
    }

    private void OnWardenDeath(JbPlayer warden)
    {
        if (!Cfg.DeputySucceedsWarden || _deputy is not { Connected: true } d || !d.IsAlive || !d.IsGuard) return;
        // Next frame: the warden module removes the dead warden right after this event.
        Server.NextFrame(() =>
        {
            if (!ReferenceEquals(_deputy, d) || !d.IsAlive || !d.IsGuard || Jb.Warden.HasWarden) return;
            ClearDeputy(false);
            if (Jb.Warden.TrySetWarden(d)) Chat.TellAll("wt.deputy.promoted", d.Name);
        });
    }

    // ================================================================= !repeat

    [ConsoleCommand("css_repeat", "sm_repeat - (prisoner) ask the warden to repeat the last order")]
    public void CmdRepeat(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (caller.Team != CsTeam.Terrorist || !caller.IsAliveP()) { Chat.Tell(caller, "wt.repeat.only_prisoners"); return; }
        var warden = Jb.Warden.Warden;
        if (warden == null) { Chat.Tell(caller, "wt.repeat.no_warden"); return; }

        var now = (float)Server.EngineTime;
        if (now < _nextRepeat[caller.Slot]) { Chat.Tell(caller, "wt.repeat.wait", (int)Math.Ceiling(_nextRepeat[caller.Slot] - now)); return; }
        if (now < _nextRepeatGlobal) { Chat.Tell(caller, "wt.repeat.recent"); return; }
        _nextRepeat[caller.Slot] = now + Math.Max(0f, Cfg.RepeatCooldown);
        _nextRepeatGlobal = now + Math.Max(0f, Cfg.RepeatGlobalCooldown);

        var line = Lang.T("wt.repeat.line", caller.PlayerName);
        foreach (var c in Players.Humans())
        {
            if (c.Slot != caller.Slot && c.Team != CsTeam.CounterTerrorist && !Perms.IsAdmin(c)) continue;
            Chat.Print(c, line);
        }

        if (warden.Controller is { } wc && !string.IsNullOrWhiteSpace(Cfg.RepeatSound)) Sound.Play(wc, Cfg.RepeatSound);
        Jb.Log.LogEvent($"{caller.PlayerName} asked the warden to repeat", caller.Jb(), warden, LogType.Warden);
    }

    // ================================================================= !votewarden

    [ConsoleCommand("css_votewarden", "sm_votewarden - vote to remove the current warden (type again to take your vote back)")]
    [ConsoleCommand("css_vw", "sm_vw - vote to remove the current warden")]
    public void CmdVoteWarden(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Cfg.VoteWardenEnabled) { Chat.Tell(caller, "wt.disabled"); return; }
        if (caller.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) { Chat.Tell(caller, "wt.vw.not_playing"); return; }
        var warden = Jb.Warden.Warden;
        if (warden == null) { Chat.Tell(caller, "wt.repeat.no_warden"); return; }
        if (warden.Slot == caller.Slot) { Chat.Tell(caller, "wt.vw.self"); return; }

        var since = (float)Server.EngineTime - _wardenSince;
        if (since < Cfg.VoteWardenGraceSeconds) { Chat.Tell(caller, "wt.vw.grace", (int)Math.Ceiling(Cfg.VoteWardenGraceSeconds - since)); return; }

        var added = _wardenVotes.Add(caller.Slot);
        if (!added) _wardenVotes.Remove(caller.Slot);

        var eligible = VoteEligible(warden).ToList();
        _wardenVotes.IntersectWith(eligible.Select(c => c.Slot));
        var needed = VotesNeeded(eligible.Count, Cfg.VoteWardenRatio, Cfg.VoteWardenMinVotes);
        Chat.TellAll(added ? "wt.vw.voted" : "wt.vw.unvoted", caller.PlayerName, warden.Name, _wardenVotes.Count, needed);

        if (_wardenVotes.Count < needed) return;
        _wardenVotes.Clear();
        var removed = Plugin.GetModule<WardenModule>()?.ForceRemoveWarden();
        if (removed == null) return;
        Chat.TellAll("wt.vw.passed", removed.Name);
        Jb.Log.LogEvent($"{removed.Name} was voted out as warden", removed, null, LogType.Warden);
    }

    /// <summary>Humans on prisoners or guards, except the warden.</summary>
    private static IEnumerable<CCSPlayerController> VoteEligible(JbPlayer warden) =>
        Players.Humans().Where(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist && c.Slot != warden.Slot);

    /// <summary>Votes needed: ratio of the eligible players, rounded up, never below the minimum (unit-tested).</summary>
    internal static int VotesNeeded(int eligible, double ratio, int minVotes) =>
        Math.Max(Math.Max(1, minVotes), (int)Math.Ceiling(Math.Max(0, eligible) * Math.Clamp(ratio, 0.0, 1.0)));

    // ================================================================= !split

    [ConsoleCommand("css_split", "sm_split [2-4|off] - (warden/deputy) split the alive prisoners into colour teams")]
    [ConsoleCommand("css_teams", "sm_teams [2-4|off] - (warden/deputy) split the alive prisoners into colour teams")]
    public void CmdSplit(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Cfg.SplitEnabled) { Chat.ReplyT(caller, info, "wt.disabled"); return; }
        if (!CanCommand(caller, true)) { Chat.ReplyT(caller, info, "wt.not_warden"); return; }

        var arg = info.ArgCount > 1 ? info.GetArg(1).Trim() : "2";
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) || arg == "0")
        {
            if (_splitTeam.Count == 0) { Chat.ReplyT(caller, info, "wt.split.none"); return; }
            ClearSplit(true);
            Chat.TellAll("wt.split.cleared", caller?.PlayerName ?? "Console");
            return;
        }

        if (!int.TryParse(arg, out var teams) || teams < 2 || teams > SplitColors.Length)
        {
            Chat.ReplyT(caller, info, "core.usage", "!split [2-4|off]");
            return;
        }

        if (Jb.LastRequests.NumInitialized > 0) { Chat.ReplyT(caller, info, "wt.split.lr"); return; }
        var pool = Players.AlivePrisoners().Where(c => c.Jb() is { } p && !Jb.LastRequests.IsInLastRequest(p)).ToList();
        if (pool.Count < teams) { Chat.ReplyT(caller, info, "wt.split.too_few", teams); return; }

        ClearSplit(true);
        var order = SplitAssign(pool.Count, teams, Random.Shared);
        for (var i = 0; i < pool.Count; i++) _splitTeam[pool[i].Slot] = order[i];

        Chat.TellAll("wt.split.done", caller?.PlayerName ?? "Console", teams);
        for (var t = 0; t < teams; t++)
        {
            var names = pool.Where(c => _splitTeam[c.Slot] == t).Select(c => c.PlayerName);
            Chat.TellAll("wt.split.team", Lang.T("wt.split.color" + t), string.Join(", ", names));
        }

        foreach (var c in pool)
        {
            var t = _splitTeam[c.Slot];
            c.SetColor(SplitColors[t]);
            Hud.Set(c, "wt.split", Lang.T("wt.split.hud", SplitHex[t], Colors.Strip(Lang.T("wt.split.color" + t))), 60, Math.Max(1f, Cfg.SplitHudSeconds));
        }

        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} split the prisoners into {teams} teams", caller.Jb(), null, LogType.Warden);
    }

    /// <summary>Balanced random team per index: sizes differ by at most one (unit-tested).</summary>
    internal static int[] SplitAssign(int count, int teams, Random rng)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++) result[i] = i % teams;
        // Fisher-Yates shuffle of the team labels keeps the counts balanced.
        for (var i = count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    private void ClearSplit(bool resetColors)
    {
        if (_splitTeam.Count == 0) return;
        var slots = _splitTeam.Keys.ToList();
        _splitTeam.Clear();
        if (!resetColors) return;
        foreach (var slot in slots)
        {
            var c = Players.Get(slot).Controller;
            if (c != null && c.IsValid && c.IsAliveP() && c.Team == CsTeam.Terrorist) c.ResetColor();
        }
    }

    /// <summary>Back to the !split team colour if the prisoner has one, otherwise no tint.</summary>
    private void RestoreColor(CCSPlayerController c)
    {
        if (_splitTeam.TryGetValue(c.Slot, out var t)) c.SetColor(SplitColors[t]);
        else c.ResetColor();
    }

    // ================================================================= helpers

    internal static bool TryParseColor(string? rgb, out Color color)
    {
        color = Color.White;
        if (string.IsNullOrWhiteSpace(rgb)) return false;
        var parts = rgb.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var g) || !int.TryParse(parts[2], out var b)) return false;
        color = Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        return true;
    }
}
