using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Admin;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Teams;

/// <summary>Section "Teams" (UltJB_TeamRatio.sp cvars + UltJB_Command_CTBan.sp).</summary>
public sealed class TeamsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_prisoners_per_guard (min 1): 5 = one guard for every five prisoners.</summary>
    public float PrisonersPerGuard { get; set; } = 5f;

    /// <summary>ultjb_pre_guard_queue_time (min 1): seconds of the opening guard queue when the CT team is empty.</summary>
    public int PreGuardQueueSeconds { get; set; } = 30;

    /// <summary>The original force-opened the cells while the first guards were being picked.</summary>
    public bool PreQueueOpensCells { get; set; } = true;

    /// <summary>Guard play-time requirement in seconds (original 18000 = 5 h, only enforced with the stats plugins; 0 = off).</summary>
    public int RequiredGuardPlaytimeSeconds { get; set; } = 0;

    /// <summary>Sound event played on a refused team join / queue request (original buttons/button11.wav).</summary>
    public string RestrictedSound { get; set; } = "UIPanorama.buymenu_failure";

    /// <summary>Force mp_force_pick_time / mp_autoteambalance 0 / mp_limitteams 0 at map start so CS2's balancer does not fight the ratio.</summary>
    public bool ForceTeamCvars { get; set; } = true;
}

internal sealed class CtBanEntry
{
    public long Expires { get; set; }     // unix seconds, 0 = permanent
    public string Reason { get; set; } = "";
    public string Name { get; set; } = "";
    public string By { get; set; } = "";
    public long Created { get; set; }
}

/// <summary>
/// Port of UltJB_TeamRatio.sp (v1.18: guard queue, pre-guard queue, prisoners-per-guard ratio, jointeam rules) and
/// UltJB_Command_CTBan.sp (v1.8) with the TimedPunishments CT ban storage replaced by plugin JSON storage
/// (real-time expiry like the original). !g (UltJB_Command_CTMessage) lives in the Commands module.
/// </summary>
public sealed class TeamsModule : JbModule, ITeamService
{
    private const string BanNs = "ctbans";

    private TeamsConfig Cfg => Section<TeamsConfig>("Teams");

    public override string Name => "Teams";
    public override int Order => 15;
    public override bool Enabled => Cfg.Enabled;

    private readonly List<int> _queue = new(); // slots
    private bool _preQueueFinished;
    private int _countdown;
    private Timer? _preQueueTimer;
    private bool _blockTeamMsg;

    private float Ratio => Math.Max(1f, Cfg.PrisonersPerGuard);

    public override void OnLoad(bool hotReload)
    {
        Jb.Teams = this;
        Plugin.AddCommandListener("jointeam", OnJoinTeam, HookMode.Pre);
        Plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamPre, HookMode.Pre);
        Plugin.AddTimer(5f, CheckExpiredBans, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        if (hotReload)
        {
            _preQueueFinished = Players.All().Any(c => c.Team == CsTeam.CounterTerrorist);
            ApplyCvars();
        }
    }

    public override void OnUnload(bool hotReload) => StopPreQueue();

    public override void OnMapStart(string mapName)
    {
        _preQueueFinished = false;
        StopPreQueue();
        Server.NextFrame(ApplyCvars);
    }

    public override void OnMapEnd() => StopPreQueue();

    private void ApplyCvars()
    {
        if (!Cfg.ForceTeamCvars) return;
        if (ConVar.Find("mp_force_pick_time") != null) Cvars.Set("mp_force_pick_time", "999999");
        Cvars.Set("mp_autoteambalance", "0");
        Cvars.Set("mp_limitteams", "0");
    }

    // ================================================================== !guard

    [ConsoleCommand("css_guard", "sm_guard - join the guard queue")]
    public void CmdGuard(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        var p = caller.Jb()!;
        if (caller.Team == CsTeam.CounterTerrorist) { TellBoth(caller, Lang.T("teams.guard.already")); return; }
        if (caller.Team != CsTeam.Terrorist) { Restricted(caller); TellBoth(caller, Lang.T("teams.guard.must_be_prisoner")); return; }
        if (IsCtBanned(p.SteamId64)) { Restricted(caller); TellBoth(caller, Lang.T("teams.guard.ctbanned")); return; }
        if (!HasGuardPlaytime(p)) { Restricted(caller); TellBoth(caller, Lang.T("teams.guard.hours", HoursText())); return; }
        if (!_queue.Contains(p.Slot)) _queue.Add(p.Slot);
        TellBoth(caller, _preQueueFinished ? Lang.T("teams.guard.position", _queue.IndexOf(p.Slot) + 1) : Lang.T("teams.guard.queued"));
    }

    private static void TellBoth(CCSPlayerController c, string text)
    {
        Chat.Print(c, text);
        Chat.PrintToConsole(c, Colors.Strip(text));
    }

    private void Restricted(CCSPlayerController c)
    {
        if (Cfg.RestrictedSound.Length > 0) Sound.Play(c, Cfg.RestrictedSound);
    }

    private string HoursText() => (Cfg.RequiredGuardPlaytimeSeconds / 3600f).ToString("0.#");

    private bool HasGuardPlaytime(JbPlayer p)
    {
        if (Cfg.RequiredGuardPlaytimeSeconds <= 0 || p.IsBot || p.SteamId64 == 0) return true;
        var played = Storage.Get<long>("playtime", p.SteamId64.ToString());
        return played >= Cfg.RequiredGuardPlaytimeSeconds;
    }

    // ================================================================== jointeam

    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || info.ArgCount < 2) return HookResult.Continue;
        if (!int.TryParse(info.GetArg(1), out var team)) return HookResult.Continue;
        var p = player.Jb();
        if (p == null) return HookResult.Continue;

        if (team == 0)
        {
            Refuse(player, Lang.T("teams.join.auto"));
            return HookResult.Handled;
        }

        if (player.Team <= CsTeam.Spectator && team == (int)CsTeam.Terrorist)
        {
            player.ChangeTeam(CsTeam.Terrorist); // join T even when the engine thinks it is full
            return HookResult.Handled;
        }

        if (team != (int)CsTeam.CounterTerrorist) return HookResult.Continue;
        if (!CanJoinGuards(p, out var reason))
        {
            Refuse(player, reason);
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private void Refuse(CCSPlayerController c, string text)
    {
        Restricted(c);
        c.PrintToChat(" " + Colors.Strip(text));
        c.PrintToConsole(Colors.Strip(text));
    }

    public bool CanJoinGuards(JbPlayer player, out string reason)
    {
        reason = "";
        if (!_preQueueFinished) { reason = Lang.T("teams.join.not_yet"); return false; }
        if (IsCtBanned(player.SteamId64)) { reason = Lang.T("teams.join.ctbanned"); return false; }
        if (!HasGuardPlaytime(player)) { reason = Lang.T("teams.join.hours", HoursText()); return false; }
        if (CanClientJoinGuardsRatio(player)) return true;
        var pos = _queue.IndexOf(player.Slot);
        reason = pos < 0 ? Lang.T("teams.join.full") : Lang.T("teams.join.full_queued", pos + 1);
        return false;
    }

    // ================================================================== ratio maths (pending team)

    private static CsTeam PendingTeam(CCSPlayerController c)
    {
        var t = c.PendingTeamNum;
        return t != 0 ? (CsTeam)t : c.Team;
    }

    private static (int prisoners, int guards) Counts()
    {
        int p = 0, g = 0;
        foreach (var c in Players.All())
        {
            switch (PendingTeam(c))
            {
                case CsTeam.Terrorist: p++; break;
                case CsTeam.CounterTerrorist: g++; break;
            }
        }

        return (p, g);
    }

    private bool ShouldMovePrisonerToGuard()
    {
        var (p, g) = Counts();
        p--;
        g++;
        if (p < 1) return false;
        return (float)p / g >= Ratio;
    }

    private bool ShouldMoveGuardToPrisoner()
    {
        var (p, g) = Counts();
        if (g <= 1) return false;
        var maxG = (int)Math.Floor(p / Ratio);
        return g > maxG;
    }

    private bool CanClientJoinGuardsRatio(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null) return false;
        var (p, g) = Counts();
        g++;
        if (PendingTeam(c) == CsTeam.Terrorist) p--;
        if (g <= 1) return true;
        var r = (float)p / g;
        if (r < Ratio) return false;
        var needed = Math.Max(1, (int)Math.Ceiling(r - Ratio));
        if (needed > _queue.Count) return true;
        var pos = _queue.IndexOf(player.Slot);
        return pos >= 0 && pos < needed;
    }

    private void SetPendingTeam(CCSPlayerController c, CsTeam team)
    {
        var p = c.Jb();
        if (p != null && c.Team == CsTeam.Terrorist) Jb.LastRequests.EndLastRequest(p);
        _blockTeamMsg = true;
        try
        {
            c.SwitchTeam(team);
            c.PendingTeamNum = (byte)team;
            Utilities.SetStateChanged(c, "CCSPlayerController", "m_iPendingTeamNum");
        }
        finally
        {
            _blockTeamMsg = false;
        }
    }

    private HookResult OnPlayerTeamPre(EventPlayerTeam ev, GameEventInfo info)
    {
        if (_blockTeamMsg) info.DontBroadcast = true;
        return HookResult.Continue;
    }

    /// <summary>Random prisoner (by pending team) with the original fallback chain: bans + hours, then hours, then anyone.</summary>
    private CCSPlayerController? RandomPrisoner()
    {
        var pool = Players.All().Where(c => PendingTeam(c) == CsTeam.Terrorist).ToList();
        if (pool.Count == 0) return null;
        var pass1 = pool.Where(c => c.Jb() is { } p && !IsCtBanned(p.SteamId64) && HasGuardPlaytime(p)).ToList();
        if (pass1.Count > 0) return pass1[Random.Shared.Next(pass1.Count)];
        var pass2 = pool.Where(c => c.Jb() is { } p && HasGuardPlaytime(p)).ToList();
        if (pass2.Count > 0) return pass2[Random.Shared.Next(pass2.Count)];
        return pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>FixTeamRatio(): runs at round end and after the pre-queue pick.</summary>
    private void FixTeamRatio()
    {
        var moved = false;
        var guard = 0;
        while (ShouldMovePrisonerToGuard() && guard++ < 64)
        {
            CCSPlayerController? pick = null;
            while (_queue.Count > 0 && pick == null)
            {
                var slot = _queue[0];
                _queue.RemoveAt(0);
                var c = Players.Get(slot).Controller;
                if (c != null && c.IsValid && PendingTeam(c) == CsTeam.Terrorist) pick = c;
            }

            if (pick != null)
            {
                Chat.TellAll("teams.chosen_queue", pick.PlayerName);
            }
            else
            {
                pick = RandomPrisoner();
                if (pick == null)
                {
                    Chat.TellAll("teams.no_valid");
                    break;
                }

                Chat.TellAll("teams.chosen_random", pick.PlayerName);
            }

            SetPendingTeam(pick, CsTeam.CounterTerrorist);
            moved = true;
        }

        if (moved) return;
        guard = 0;
        while (ShouldMoveGuardToPrisoner() && guard++ < 64)
        {
            var guards = Players.All().Where(c => PendingTeam(c) == CsTeam.CounterTerrorist).ToList();
            if (guards.Count == 0) break;
            var pick = guards[Random.Shared.Next(guards.Count)];
            SetPendingTeam(pick, CsTeam.Terrorist);
            _queue.Remove(pick.Slot);
            _queue.Insert(0, pick.Slot); // demoted guards get the first chance to return
        }
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        if (_preQueueTimer != null) return;
        FixTeamRatio();
    }

    // ================================================================== pre-guard queue

    public override void OnRoundStart()
    {
        StopPreQueue();
        if (Players.All().Any(c => c.Team == CsTeam.CounterTerrorist))
        {
            _preQueueFinished = true;
            return;
        }

        if (!Players.All().Any(c => c.Team == CsTeam.Terrorist)) return; // empty server
        StartPreQueue();
    }

    /// <summary>
    /// The first player(s) on an empty server join T mid-round, after round_start already saw an empty server, so the
    /// opening guard pick never ran and nobody could join CT ("not yet") until something else ended the round. Start the
    /// pick as soon as prisoners exist without any guard.
    /// </summary>
    private void CheckNeedFirstGuard()
    {
        if (_preQueueTimer != null) return;
        var all = Players.All().ToList();
        if (all.Any(c => c.Team == CsTeam.CounterTerrorist || PendingTeam(c) == CsTeam.CounterTerrorist)) return;
        if (!all.Any(c => c.Team == CsTeam.Terrorist)) return;
        _preQueueFinished = false;
        StartPreQueue();
    }

    private void StartPreQueue()
    {
        if (Cfg.PreQueueOpensCells) Jb.Cells.ForceOpen();
        _countdown = 0;
        _preQueueFinished = false;
        ShowCountdown();
        _preQueueTimer = Plugin.AddTimer(1f, PreQueueTick, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        Chat.TellAll("teams.prequeue.chat", Math.Max(1, Cfg.PreGuardQueueSeconds));
    }

    private void ShowCountdown()
    {
        var left = Math.Max(1, Cfg.PreGuardQueueSeconds) - _countdown;
        var html = Lang.T("teams.prequeue.hint", left);
        foreach (var c in Players.Humans()) Hud.Set(c, "teams.prequeue", html, 60, 2);
    }

    private void PreQueueTick()
    {
        _countdown++;
        if (_countdown < Math.Max(1, Cfg.PreGuardQueueSeconds))
        {
            ShowCountdown();
            return;
        }

        StopPreQueue();
        _preQueueFinished = true;
        foreach (var c in Players.Humans()) Hud.Set(c, "teams.prequeue", Lang.T("teams.prequeue.done"), 60, 5);

        _queue.RemoveAll(s => Players.Get(s).Controller is not { IsValid: true } c || c.Team != CsTeam.Terrorist);
        if (_queue.Count == 0)
        {
            Chat.TellAll("teams.prequeue.empty");
            var pick = RandomPrisoner();
            if (pick == null)
            {
                Round.End(CsTeam.None, 7f);
                return;
            }

            SetPendingTeam(pick, CsTeam.CounterTerrorist);
            Chat.TellAll("teams.chosen_random", pick.PlayerName);
        }
        else
        {
            // Randomise the queue once; from now on it is first come first serve.
            for (var i = _queue.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
            }

            var c = Players.Get(_queue[0]).Controller!;
            _queue.RemoveAt(0);
            SetPendingTeam(c, CsTeam.CounterTerrorist);
            Chat.TellAll("teams.prequeue.randomized");
            Chat.TellAll("teams.chosen_queue", c.PlayerName);
        }

        FixTeamRatio();
        Round.End(CsTeam.None, 7f);
    }

    private void StopPreQueue()
    {
        try { _preQueueTimer?.Kill(); }
        catch { /* already stopped */ }
        _preQueueTimer = null;
    }

    // ================================================================== queue maintenance

    public override void OnPlayerDisconnect(JbPlayer player) => _queue.Remove(player.Slot);

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (newTeam != CsTeam.Terrorist) _queue.Remove(player.Slot);
        if (!disconnect && newTeam == CsTeam.Terrorist && !_preQueueFinished) Server.NextFrame(CheckNeedFirstGuard);
        if (disconnect || newTeam != CsTeam.CounterTerrorist || !IsCtBanned(player.SteamId64)) return;
        // A CT banned player ended up on CT (admin move, auto-assign): put them back on T next frame.
        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var c = Players.Get(slot).Controller;
            if (c == null || !c.IsValid || c.Team != CsTeam.CounterTerrorist) return;
            MoveBannedToPrisoners(c);
        });
    }

    public void MoveToTeam(JbPlayer player, CsTeam team, bool force = false)
    {
        var c = player.Controller;
        if (c == null) return;
        if (!force && team == CsTeam.CounterTerrorist && !CanJoinGuards(player, out var reason))
        {
            Chat.Print(c, reason);
            return;
        }

        SetPendingTeam(c, team);
    }

    // ================================================================== CT bans

    public bool IsCtBanned(ulong steamId64)
    {
        if (steamId64 == 0) return false;
        var e = Storage.Get<CtBanEntry>(BanNs, steamId64.ToString());
        return e != null && (e.Expires == 0 || e.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void MoveBannedToPrisoners(CCSPlayerController c)
    {
        c.ChangeTeam(CsTeam.Terrorist);
        if (c.IsAliveP()) c.CommitSuicide(false, true);
        c.PrintToChat(" " + Colors.Strip(Lang.T("teams.ctban.moved")));
    }

    private void CheckExpiredBans()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (key, e) in Storage.All<CtBanEntry>(BanNs).ToList())
        {
            if (e.Expires == 0 || e.Expires > now) continue;
            Storage.Delete(BanNs, key);
            var online = Players.Connected().FirstOrDefault(p => p.SteamId64.ToString() == key);
            if (online != null) Chat.PrintAll(Lang.T("teams.ctban.expired", online.Name), false);
        }
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        // Expired bans of a player who was offline are cleaned silently.
        if (player.SteamId64 == 0) return;
        var e = Storage.Get<CtBanEntry>(BanNs, player.SteamId64.ToString());
        if (e != null && e.Expires != 0 && e.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) Storage.Delete(BanNs, player.SteamId64.ToString());
    }

    /// <summary>Resolve a player or an offline SteamID. Returns false after replying when nothing usable was given.</summary>
    private static bool ResolveBanTarget(CCSPlayerController? caller, CommandInfo info, string arg, out CCSPlayerController? online, out ulong steamId)
    {
        online = null;
        steamId = AdminModule.ParseSteamId(arg);
        if (steamId != 0)
        {
            var id = steamId;
            online = Players.All().FirstOrDefault(c => c.SteamID == id);
            return true;
        }

        var r = Targets.FindOrReply(caller, info, arg, singleOnly: true, respectImmunity: true);
        if (r == null) return false;
        online = r.Players[0];
        if (online.IsBot || online.SteamID == 0) { Chat.ReplyT(caller, info, "teams.ctban.bot"); return false; }
        steamId = online.SteamID;
        return true;
    }

    [ConsoleCommand("css_ctban", "sm_ctban <#steamid|#userid|name> <minutes> \"reason\" - ban a player from the CT team")]
    public void CmdCtBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "ctban")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 4) { Chat.ReplyT(caller, info, "core.usage", "css_ctban <#steamid|#userid|name> <minutes> \"reason\""); return; }
        if (!int.TryParse(info.GetArg(2), out var minutes)) { Chat.ReplyT(caller, info, "core.usage", "css_ctban <#steamid|#userid|name> <minutes> \"reason\""); return; }
        minutes = Math.Max(0, minutes);
        if (minutes == 0 && caller != null && !Perms.Has(caller, "ctban.permanent")) { Chat.Tell(caller, "core.no_access"); return; }
        var reason = string.Join(' ', Enumerable.Range(3, info.ArgCount - 3).Select(info.GetArg)).Trim().Trim('"').Trim();
        if (reason.Length == 0) { Chat.ReplyT(caller, info, "teams.ctban.need_reason"); return; }
        if (!ResolveBanTarget(caller, info, info.GetArg(1), out var target, out var steamId)) return;

        Storage.Set(BanNs, steamId.ToString(), new CtBanEntry
        {
            Expires = minutes == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minutes * 60L,
            Reason = reason.Length > 255 ? reason[..255] : reason,
            Name = target?.PlayerName ?? "",
            By = caller == null ? "Console" : $"{caller.PlayerName} ({caller.SteamID})",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        Storage.Flush();

        var label = target?.PlayerName ?? steamId.ToString();
        if (target != null && target.Team == CsTeam.CounterTerrorist) MoveBannedToPrisoners(target);
        if (target != null) _queue.Remove(target.Slot);
        var admin = caller?.PlayerName ?? "Console";
        if (minutes > 0)
        {
            Chat.ReplyT(caller, info, "teams.ctban.done", label, minutes);
            Chat.PrintAll(Lang.T("teams.ctban.done_all", label, minutes, admin), false);
        }
        else
        {
            Chat.ReplyT(caller, info, "teams.ctban.done_perm", label);
            Chat.PrintAll(Lang.T("teams.ctban.done_perm_all", label, admin), false);
        }

        Logger.LogInformation("\"{Admin}\" ct banned \"{Target}\" ({SteamId}) (minutes \"{Minutes}\") reason \"{Reason}\"", admin, label, steamId, minutes, reason);
        Jb.Log.LogEvent($"{admin} CT banned {label} ({(minutes > 0 ? minutes + " minutes" : "permanent")}): {reason}", null, null, LogType.Any);
    }

    [ConsoleCommand("css_unctban", "sm_unctban <#steamid|#userid|name> - remove a CT ban")]
    public void CmdUnCtBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "ctban")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "css_unctban <#steamid|#userid|name>"); return; }
        if (!ResolveBanTarget(caller, info, info.GetArg(1), out var target, out var steamId)) return;
        var key = steamId.ToString();
        var entry = Storage.Get<CtBanEntry>(BanNs, key);
        if (entry == null) { Chat.ReplyT(caller, info, "teams.ctban.not_banned", target?.PlayerName ?? key); return; }
        if (entry.Expires == 0 && caller != null && !Perms.Has(caller, "ctban.permanent")) { Chat.Tell(caller, "core.no_access"); return; }
        Storage.Delete(BanNs, key);
        Storage.Flush();
        var label = target?.PlayerName ?? (entry.Name.Length > 0 ? entry.Name : key);
        var admin = caller?.PlayerName ?? "Console";
        Chat.ReplyT(caller, info, "teams.ctban.lifted", label);
        Chat.PrintAll(Lang.T("teams.ctban.lifted_all", label, admin), false);
        Logger.LogInformation("\"{Admin}\" ct unbanned \"{Target}\" ({SteamId})", admin, label, steamId);
    }

    [ConsoleCommand("css_check_ctban", "sm_check_ctban <#steamid|#userid|name> - check a CT ban")]
    public void CmdCheckCtBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "ctban")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "css_check_ctban <#steamid|#userid|name>"); return; }
        if (!ResolveBanTarget(caller, info, info.GetArg(1), out var target, out var steamId)) return;
        var label = target?.PlayerName ?? steamId.ToString();
        var e = Storage.Get<CtBanEntry>(BanNs, steamId.ToString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (e == null || e.Expires != 0 && e.Expires <= now) Chat.ReplyT(caller, info, "teams.ctban.check_none", label);
        else if (e.Expires == 0) Chat.ReplyT(caller, info, "teams.ctban.check_perm", label, e.Reason);
        else Chat.ReplyT(caller, info, "teams.ctban.check_timed", label, (int)Math.Ceiling((e.Expires - now) / 60.0), e.Reason);
    }
}
