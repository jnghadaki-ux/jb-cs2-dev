using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Swbs.Common;

/// <summary>The next-round vote and the rounds commands.</summary>
public sealed partial class RoundsModule
{
    private bool _voteOpen, _voteDone;
    private float _voteEnds;

    /// <summary>The ballot; a null entry is "Normal round".</summary>
    private readonly List<RoundVariation?> _ballot = new();

    private readonly Dictionary<int, int> _votes = new();

    private void OpenVote()
    {
        _voteDone = true;
        if (Cfg.Schedule != RoundSchedule.Vote || _next != null || _nextNormal) return;
        // A vote only decides variation rounds: when the next round is a normal one there is nothing to choose.
        if (!RoundLogic.NextIsVariation(Cfg.NormalRoundsBetween, _normalSince)) return;

        var options = _all.Where(v => Playable(v, true) && !OnCooldown(v) && !ReferenceEquals(v, Active)).ToList();
        if (options.Count == 0) options = _all.Where(v => Playable(v, true)).ToList();
        if (options.Count == 0) return;
        var keys = RoundLogic.Ballot(options.Select(v => (v.Key, SettingsOf(v).Weight)).ToList(), Math.Clamp(Cfg.VoteOptions, 1, 5), Random.Shared.Next);
        _ballot.Clear();
        _ballot.AddRange(keys.Select(k => _all.First(v => v.Key == k)));
        if (Cfg.VoteIncludesNormal) _ballot.Add(null);
        if (_ballot.Count < 2) { _ballot.Clear(); return; }

        _votes.Clear();
        _voteOpen = true;
        _voteEnds = Server.CurrentTime + Math.Max(5, Cfg.VoteSeconds);
        Chat.TellAll("rounds.vote_open", Math.Max(5, Cfg.VoteSeconds));
        foreach (var c in Players.Humans().ToList()) ShowBallot(c);
        if (Cfg.VoteSound.Length > 0) Sound.PlayAll(Cfg.VoteSound);
    }

    private string BallotName(RoundVariation? v) => v == null ? Lang.T("rounds.normal_name") : v.Name;

    private void ShowBallot(CCSPlayerController c)
    {
        if (!_voteOpen) return;
        var menu = new SwbsMenu(Lang.T("rounds.vote_title"))
        {
            Subtitle = Lang.T("rounds.vote_subtitle", (int)Math.Max(0, _voteEnds - Server.CurrentTime)),
            Timeout = Math.Max(5, Cfg.VoteSeconds)
        };
        for (var i = 0; i < _ballot.Count; i++)
        {
            var index = i;
            var v = _ballot[i];
            var tag = v == null ? "" : " " + Lang.T("rounds.kind_tag." + v.Kind.ToString().ToLowerInvariant());
            menu.Add(BallotName(v) + tag, p => CastVote(p, index));
        }

        menu.Open(c);
    }

    private void CastVote(CCSPlayerController c, int index)
    {
        if (!_voteOpen || index < 0 || index >= _ballot.Count) return;
        var changed = _votes.ContainsKey(c.Slot);
        _votes[c.Slot] = index;
        Chat.Tell(c, changed ? "rounds.vote_changed" : "rounds.vote_cast", BallotName(_ballot[index]));
        // Everyone voted: no need to wait.
        if (Players.Humans().All(h => _votes.ContainsKey(h.Slot))) CloseVote(true);
    }

    private void CloseVote(bool announce)
    {
        if (!_voteOpen)
        {
            if (!announce) _voteDone = false;
            return;
        }

        _voteOpen = false;
        if (!announce)
        {
            _voteDone = false;
            _ballot.Clear();
            return;
        }

        var winner = RoundLogic.VoteWinner(_votes.Values, _ballot.Count);
        if (winner < 0)
        {
            Chat.TellAll("rounds.vote_none");
        }
        else
        {
            var pick = _ballot[winner];
            var count = _votes.Values.Count(v => v == winner);
            if (pick == null) _nextNormal = true;
            else _next = pick;
            Chat.TellAll("rounds.vote_result", BallotName(pick), count, _votes.Count);
        }

        _ballot.Clear();
        _votes.Clear();
    }

    // ================================================================= commands

    private string Describe(RoundVariation v) =>
        Lang.T("rounds.list_line", v.Name, Lang.T("rounds.kind." + v.Kind.ToString().ToLowerInvariant()), v.Description);

    [ConsoleCommand("css_round", "sm_round - what this round is and what comes next")]
    public void CmdRound(CCSPlayerController? caller, CommandInfo info)
    {
        if (Active != null)
        {
            Chat.ReplyT(caller, info, "rounds.now_variation", Active.Name, RoundLogic.Clock(RoundClock.TimeLeft));
            Chat.ReplyT(caller, info, "rounds.intro_desc", Active.Description);
        }
        else Chat.ReplyT(caller, info, "rounds.now_normal", RoundLogic.Clock(RoundClock.TimeLeft));

        if (_nextNormal) Chat.ReplyT(caller, info, "rounds.next_is", Lang.T("rounds.normal_name"));
        else if (_next != null) Chat.ReplyT(caller, info, "rounds.next_is", _next.Name);
        else if (Cfg.Schedule == RoundSchedule.Vote) Chat.ReplyT(caller, info, "rounds.next_vote", Math.Max(0, Cfg.VoteAtSecondsLeft));
        if (caller != null && _voteOpen) ShowBallot(caller);
    }

    [ConsoleCommand("css_rounds", "sm_rounds - list the round variations")]
    public void CmdRounds(CCSPlayerController? caller, CommandInfo info)
    {
        var list = _all.Where(v => Suits(v) && SettingsOf(v).Enabled).ToList();
        if (caller == null)
        {
            foreach (var v in list) Chat.Reply(caller, info, $"{v.Key}: {Colors.Strip(Describe(v))}");
            return;
        }

        var menu = new SwbsMenu(Lang.T("rounds.list_title"));
        foreach (var v in list)
        {
            var variation = v;
            menu.Add($"{v.Name} {Lang.T("rounds.kind_tag." + v.Kind.ToString().ToLowerInvariant())}", p => Chat.Print(p, Describe(variation)), keepOpen: true);
        }

        menu.Open(caller);
    }

    [ConsoleCommand("css_roundvote", "sm_roundvote - reopen the next-round vote")]
    [ConsoleCommand("css_rv", "sm_rv - reopen the next-round vote")]
    public void CmdRoundVote(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!_voteOpen) { Chat.Tell(caller, "rounds.vote_closed"); return; }
        ShowBallot(caller);
    }

    private RoundVariation? Find(string key) =>
        _all.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? _all.FirstOrDefault(v => Colors.Strip(v.Name).Replace(" ", "").Equals(key.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

    [ConsoleCommand("css_setround", "sm_setround <variation|normal> - choose the next round (admin)")]
    public void CmdSetRound(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "admin")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2)
        {
            if (caller == null) { Chat.ReplyT(caller, info, "core.usage", "!setround <variation|normal>"); return; }
            var menu = new SwbsMenu(Lang.T("rounds.set_title"));
            menu.Add(Lang.T("rounds.normal_name"), p => SetNext(p, null));
            foreach (var v in _all.Where(Suits))
            {
                var variation = v;
                menu.Add(v.Name, p => SetNext(p, variation), Playable(v, false));
            }

            menu.Open(caller);
            return;
        }

        var arg = info.GetArg(1);
        if (arg.Equals("normal", StringComparison.OrdinalIgnoreCase)) { SetNext(caller, null); return; }
        var found = Find(arg);
        if (found == null || !Suits(found)) { Chat.ReplyT(caller, info, "rounds.unknown", arg); return; }
        if (!Playable(found, false)) { Chat.ReplyT(caller, info, "rounds.not_playable", found.Name); return; }
        SetNext(caller, found);
    }

    private void SetNext(CCSPlayerController? admin, RoundVariation? v)
    {
        CloseVote(false);
        _voteDone = true;
        _next = v;
        _nextNormal = v == null;
        Chat.TellAll("rounds.set_by", Chat.Name(admin), BallotName(v));
        CommonHost.Log($"{admin?.PlayerName ?? "Console"} set the next round to {v?.Key ?? "normal"}", admin?.Swbs(), null, CommonLogType.Admin);
    }

    [ConsoleCommand("css_endround", "sm_endround - end the round now (admin)")]
    public void CmdEndRound(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "admin")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (_ending || RoundClock.IsWarmup) { Chat.ReplyT(caller, info, "rounds.cannot_end"); return; }
        CommonHost.Log($"{caller?.PlayerName ?? "Console"} ended the round", caller?.Swbs(), null, CommonLogType.Admin);
        Finish("rounds.ended_by", Chat.Name(caller));
    }

    [ConsoleCommand("css_roundnow", "sm_roundnow <variation|normal> - end this round and play that one next (admin)")]
    public void CmdRoundNow(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "admin")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!roundnow <variation|normal>"); return; }
        var arg = info.GetArg(1);
        RoundVariation? v = null;
        if (!arg.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            v = Find(arg);
            if (v == null || !Suits(v)) { Chat.ReplyT(caller, info, "rounds.unknown", arg); return; }
            if (!Playable(v, false)) { Chat.ReplyT(caller, info, "rounds.not_playable", v.Name); return; }
        }

        if (_ending || RoundClock.IsWarmup) { Chat.ReplyT(caller, info, "rounds.cannot_end"); return; }
        SetNext(caller, v);
        _ending = true;
        RoundClock.End(3f);
    }
}
