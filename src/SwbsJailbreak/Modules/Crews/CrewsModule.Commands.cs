using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Crews;

public sealed partial class CrewsModule
{
    private readonly Dictionary<int, (string CrewId, string Inviter, double Expires)> _invites = new();

    // ====================================================================== !crew

    [ConsoleCommand("css_crew", "sm_crew [create|invite|accept|decline|leave|kick|promote|demote|boss|motd|rival|favor|info|disband] - crews")]
    public void CmdCrew(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        var args = Enumerable.Range(1, Math.Max(0, info.ArgCount - 1)).Select(info.GetArg).ToList();
        if (args.Count == 0) { ShowCrewMenu(caller); return; }
        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        switch (sub)
        {
            case "create":
                if (rest.Count < 2) { Chat.Tell(caller, "crews.usage.create"); return; }
                TryCreate(caller, string.Join(' ', rest.Take(rest.Count - 1)), rest[^1], 0);
                break;
            case "invite": WithTarget(caller, rest, Invite); break;
            case "accept": Accept(caller); break;
            case "decline": Decline(caller); break;
            case "leave": ConfirmLeave(caller); break;
            case "kick": WithMember(caller, rest, Kick); break;
            case "promote": WithMember(caller, rest, (c, cr, m) => SetRank(c, cr, m, true)); break;
            case "demote": WithMember(caller, rest, (c, cr, m) => SetRank(c, cr, m, false)); break;
            case "boss": WithMember(caller, rest, Transfer); break;
            case "motd": SetMotd(caller, string.Join(' ', rest)); break;
            case "rival": SetRival(caller, rest.FirstOrDefault() ?? ""); break;
            case "favor": ShowFavorMenu(caller); break;
            case "info": ShowInfo(caller, rest.FirstOrDefault()); break;
            case "disband": ConfirmDisband(caller); break;
            default: ShowCrewMenu(caller); break;
        }
    }

    [ConsoleCommand("css_cr", "sm_cr <message> - crew chat")]
    [ConsoleCommand("css_crewsay", "sm_crewsay <message> - crew chat")]
    public void CmdCrewSay(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        var crew = CrewOf(caller);
        if (crew == null) { Chat.Tell(caller, "crews.err.not_member"); return; }
        if (caller.Jb() is { AdminChatGagged: true }) { Chat.Tell(caller, "crews.err.gagged"); return; }
        var text = info.ArgString.Trim().Trim('"').Trim();
        if (text.Length == 0) { Chat.Tell(caller, "crews.usage.say"); return; }
        if (text.Length > 200) text = text[..200];
        text = text.Replace('{', '(').Replace('}', ')'); // no colour-code injection
        var alive = caller.IsAliveP();
        var line = Lang.T("crews.chat.line", CrewColors.Of(crew.Color).Chat, crew.Tag, alive ? "" : Lang.T("crews.chat.dead"), Chat.Name(caller), text);
        foreach (var c in OnlineMembers(crew))
        {
            // No back channel between teams, and no ghosting: a dead member's crew chat only reaches dead crewmates.
            if (c.Team != caller.Team || (!alive && c.IsAliveP())) continue;
            Chat.Print(c, line, false);
        }

        if (Cfg.LogCrewChat) Logger.LogInformation("[Crews] [{Tag}] {Name}: {Text}", crew.Tag, caller.PlayerName, text);
    }

    [ConsoleCommand("css_crews", "sm_crews - crew leaderboard")]
    public void CmdCrews(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null)
        {
            foreach (var crew in Ranked().Take(20))
                info.ReplyToCommand($"[{crew.Tag}] {crew.Name} - level {LevelOf(crew)}, legacy {crew.Legacy}, respect {crew.Respect}, members {crew.Members.Count}, wars {crew.WarsWon}");
            return;
        }

        ShowLeaderboard(caller);
    }

    private IEnumerable<Crew> Ranked() => _crews.Values.OrderByDescending(c => c.Legacy).ThenByDescending(c => c.Respect).ThenBy(c => c.Name);

    // ====================================================================== menus

    private void ShowCrewMenu(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null)
        {
            var m = new JbMenu(Lang.T("crews.menu.title_none"));
            if (_invites.TryGetValue(c.Slot, out var inv) && Server.EngineTime < inv.Expires && _crews.TryGetValue(inv.CrewId, out var invCrew))
            {
                m.Subtitle = Hud.Escape(Lang.T("crews.menu.invited", $"[{invCrew.Tag}] {invCrew.Name}", inv.Inviter));
                m.Add(Lang.T("crews.menu.accept"), Accept);
                m.Add(Lang.T("crews.menu.decline"), Decline);
            }

            m.Add(Lang.T("crews.menu.create"), AskCreateName);
            m.Add(Lang.T("crews.menu.leaderboard"), ShowLeaderboard);
            m.Open(c);
            return;
        }

        var me = crew.Member(Steam(c))!;
        var level = LevelOf(crew);
        var menu = new JbMenu(Hud.Escape($"[{crew.Tag}] {crew.Name}") + $" <font color='#AAAAAA'>· Lv {level}</font>");
        var next = CrewRules.ToNextLevel(crew.Legacy);
        var turf = TurfOf(crew).Select(b => b.Name.Length > 0 ? b.Name : b.Title).ToList();
        menu.Subtitle = Hud.Escape(Lang.T("crews.menu.summary", crew.Respect, crew.Legacy, next > 0 ? Lang.T("crews.menu.next_level", next) : Lang.T("crews.menu.max_level"),
                            crew.Members.Count, SlotsOf(crew), turf.Count > 0 ? string.Join(", ", turf) : "-", crew.WarsWon, RankName(me.Rank)))
                        + (crew.Motd.Length > 0 ? "<br>" + Hud.Escape("“" + crew.Motd + "”") : "");
        menu.Add(Lang.T("crews.menu.roster"), ShowRoster);
        menu.Add(Lang.T("crews.menu.invite"), ShowInviteMenu, CrewRules.CanInvite(me.Rank) && crew.Members.Count < SlotsOf(crew));
        menu.Add(Lang.T("crews.menu.favor"), ShowFavorMenu, Cfg.FavorsEnabled && CrewRules.CanUseFavors(me.Rank));
        menu.Add(Lang.T("crews.menu.motd"), p => ChatInput.Ask(p, Lang.T("crews.ask.motd"), (x, t) => { SetMotd(x, t); ShowCrewMenu(x); }), CrewRules.CanSetMotd(me.Rank));
        menu.Add(Lang.T("crews.menu.color"), ShowColorMenu, CrewRules.CanManageCrew(me.Rank));
        menu.Add(Lang.T("crews.menu.rival"), ShowRivalMenu, CrewRules.CanManageCrew(me.Rank));
        menu.Add(Lang.T("crews.menu.leaderboard"), ShowLeaderboard);
        if (me.Rank == CrewRank.Boss) menu.Add(Lang.T("crews.menu.disband"), ConfirmDisband);
        menu.Add(Lang.T("crews.menu.leave"), ConfirmLeave);
        menu.Open(c);
    }

    private static string RankName(CrewRank r) => Lang.T(r switch
    {
        CrewRank.Boss => "crews.rank.boss",
        CrewRank.Lieutenant => "crews.rank.lieutenant",
        _ => "crews.rank.member"
    });

    private void ShowRoster(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) return;
        var me = crew.Member(Steam(c))!;
        var online = OnlineMembers(crew).ToDictionary(Steam);
        var m = new JbMenu(Lang.T("crews.menu.roster_title", crew.Tag));
        foreach (var member in crew.Members.OrderByDescending(x => x.Rank).ThenByDescending(x => x.Earned))
        {
            var target = member;
            var status = online.TryGetValue(target.SteamId, out var oc) ? (oc.IsAliveP() ? "●" : "○") : "·";
            var canAct = target.SteamId != me.SteamId && (CrewRules.CanKick(me.Rank, target.Rank) || CrewRules.CanManageCrew(me.Rank));
            m.Add(Hud.Escape($"{status} {target.Name} — {RankName(target.Rank)} · {target.Earned}"), p => ShowMemberActions(p, target.SteamId), canAct);
        }

        m.Add(Lang.T("crews.menu.back"), ShowCrewMenu);
        m.Open(c);
    }

    private void ShowMemberActions(CCSPlayerController c, string steam)
    {
        var crew = CrewOf(c);
        var target = crew?.Member(steam);
        if (crew == null || target == null) return;
        var me = crew.Member(Steam(c))!;
        var m = new JbMenu(Hud.Escape(target.Name));
        if (CrewRules.CanManageCrew(me.Rank) && target.Rank == CrewRank.Member)
            m.Add(Lang.T("crews.menu.promote"), p => { SetRank(p, crew, target, true); ShowRoster(p); });
        if (CrewRules.CanManageCrew(me.Rank) && target.Rank == CrewRank.Lieutenant)
            m.Add(Lang.T("crews.menu.demote"), p => { SetRank(p, crew, target, false); ShowRoster(p); });
        if (CrewRules.CanManageCrew(me.Rank))
            m.Add(Lang.T("crews.menu.transfer"), p => ConfirmTransfer(p, target.SteamId));
        if (CrewRules.CanKick(me.Rank, target.Rank))
            m.Add(Lang.T("crews.menu.kick"), p => { Kick(p, crew, target); ShowRoster(p); });
        m.Add(Lang.T("crews.menu.back"), ShowRoster);
        m.Open(c);
    }

    private void ShowInviteMenu(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) return;
        var m = new JbMenu(Lang.T("crews.menu.invite_title"));
        foreach (var target in Players.Humans().Where(x => x.Slot != c.Slot && CrewOf(x) == null).OrderBy(x => x.PlayerName))
        {
            var slot = target.Slot;
            m.Add(Hud.Escape(target.PlayerName), p =>
            {
                var t = Players.Get(slot).Controller;
                if (t != null && t.IsValid) Invite(p, t);
                ShowCrewMenu(p);
            });
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("crews.menu.nobody_to_invite"));
        m.Add(Lang.T("crews.menu.back"), ShowCrewMenu);
        m.Open(c);
    }

    private void ShowColorMenu(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) return;
        var unlocked = CrewColors.Unlocked(LevelOf(crew));
        var m = new JbMenu(Lang.T("crews.menu.color_title"));
        for (var i = 0; i < CrewColors.All.Length; i++)
        {
            var idx = i;
            var col = CrewColors.All[i].Rgb;
            var mark = crew.Color == i ? "&#9658; " : "";
            m.Add($"{mark}<font color='#{col.R:X2}{col.G:X2}{col.B:X2}'>{CrewColors.All[i].Name}</font>" + (i >= unlocked ? $" ({Lang.T("crews.menu.locked")})" : ""), p =>
            {
                if (CrewOf(p) is { } cr && cr.Member(Steam(p))?.Rank == CrewRank.Boss)
                {
                    cr.Color = idx;
                    Save(cr);
                    RemoveTurfBanners();
                    DrawTurfBanners();
                    TellCrew(cr, "crews.color_changed", Label(cr));
                }

                ShowCrewMenu(p);
            }, i < unlocked);
        }

        m.Add(Lang.T("crews.menu.back"), ShowCrewMenu);
        m.Open(c);
    }

    private void ShowRivalMenu(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) return;
        var m = new JbMenu(Lang.T("crews.menu.rival_title"));
        m.Add(Lang.T("crews.menu.rival_none"), p => { SetRival(p, "none"); ShowCrewMenu(p); });
        foreach (var other in Ranked().Where(o => o.Id != crew.Id).Take(30))
        {
            var tag = other.Tag;
            m.Add(Hud.Escape($"{(crew.RivalId == other.Id ? "► " : "")}[{other.Tag}] {other.Name}"), p => { SetRival(p, tag); ShowCrewMenu(p); });
        }

        m.Open(c);
    }

    private void ShowLeaderboard(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("crews.menu.leaderboard_title"));
        var rank = 0;
        foreach (var crew in Ranked().Take(30))
        {
            rank++;
            var id = crew.Id;
            m.Add(Hud.Escape($"{rank}. [{crew.Tag}] {crew.Name} — Lv {LevelOf(crew)} · {crew.Legacy} · {crew.Members.Count}p · {crew.WarsWon}W"), p => ShowInfo(p, _crews.TryGetValue(id, out var x) ? x.Tag : null));
        }

        if (rank == 0) m.AddInfo(Lang.T("crews.menu.no_crews"));
        m.Open(c);
    }

    private void ShowInfo(CCSPlayerController c, string? tag)
    {
        var crew = string.IsNullOrWhiteSpace(tag) ? CrewOf(c) : _crews.Values.FirstOrDefault(x => x.Tag.Equals(CrewRules.NormalizeTag(tag), StringComparison.Ordinal));
        if (crew == null) { Chat.Tell(c, "crews.err.no_such_crew"); return; }
        var rival = crew.RivalId.Length > 0 && _crews.TryGetValue(crew.RivalId, out var r) ? $"[{r.Tag}] {r.Name}" : "-";
        var turf = TurfOf(crew).Select(b => b.Name.Length > 0 ? b.Name : b.Title).ToList();
        Chat.Tell(c, "crews.info.line1", Label(crew), LevelOf(crew), crew.Members.Count, SlotsOf(crew), crew.Legacy, crew.Respect);
        Chat.Tell(c, "crews.info.line2", crew.Boss?.Name ?? "?", crew.WarsWon, turf.Count > 0 ? string.Join(", ", turf) : "-", rival,
            DateTimeOffset.FromUnixTimeSeconds(crew.FoundedUnix).ToString("yyyy-MM-dd"));
        if (crew.Motd.Length > 0) Chat.Tell(c, "crews.info.motd", crew.Motd);
    }

    // ====================================================================== create

    private void AskCreateName(CCSPlayerController c)
    {
        if (!CanCreate(c)) return;
        ChatInput.Ask(c, Lang.T("crews.ask.name"), (p, name) =>
        {
            if (!CrewRules.ValidName(name, Cfg.BannedWords, out var why)) { Chat.Tell(p, why); AskCreateName(p); return; }
            ChatInput.Ask(p, Lang.T("crews.ask.tag"), (q, tag) => TryCreate(q, name, tag, Random.Shared.Next(CrewColors.Unlocked(1))));
        });
    }

    private bool CanCreate(CCSPlayerController c)
    {
        if (c.SteamID == 0) { Chat.Tell(c, "crews.err.no_steam"); return false; }
        if (CrewOf(c) != null) { Chat.Tell(c, "crews.err.already_member"); return false; }
        if (Jb.Teams.IsCtBanned(c.SteamID)) { Chat.Tell(c, "crews.err.ctbanned"); return false; }
        var until = CooldownUntil(c.SteamID);
        if (until > Now) { Chat.Tell(c, "crews.err.cooldown", Math.Ceiling((until - Now) / 60.0)); return false; }
        if (_crews.Count >= Cfg.MaxCrews) { Chat.Tell(c, "crews.err.too_many"); return false; }
        return true;
    }

    private void TryCreate(CCSPlayerController c, string name, string tag, int color)
    {
        if (!CanCreate(c)) return;
        name = name.Trim();
        tag = CrewRules.NormalizeTag(tag);
        if (!CrewRules.ValidName(name, Cfg.BannedWords, out var why) || !CrewRules.ValidTag(tag, Cfg.BannedWords, out why))
        {
            Chat.Tell(c, why);
            return;
        }

        if (_crews.Values.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Chat.Tell(c, "crews.err.name_taken"); return; }
        if (_crews.Values.Any(x => x.Tag == tag)) { Chat.Tell(c, "crews.err.tag_taken"); return; }

        string id;
        do id = CrewRules.NewId(Random.Shared); while (_crews.ContainsKey(id));
        var now = Now;
        var crew = new Crew
        {
            Id = id, Name = name, Tag = tag, Color = Math.Clamp(color, 0, CrewColors.Unlocked(1) - 1), FoundedUnix = now, LastDecayUnix = now,
            Members = { new CrewMember { SteamId = Steam(c), Name = c.PlayerName, Rank = CrewRank.Boss, JoinedUnix = now } }
        };
        _crews[id] = crew;
        _memberOf[Steam(c)] = id;
        Save(crew);
        Chat.TellAll("crews.created", Chat.Name(c), Label(crew));
        Logger.LogInformation("[Crews] {Player} created [{Tag}] {Name}", c.PlayerName, tag, name);
        if (c.Jb() is { } jp) ApplyTag(jp);
    }

    // ====================================================================== invites

    private void WithTarget(CCSPlayerController c, List<string> args, Action<CCSPlayerController, CCSPlayerController> action)
    {
        if (args.Count == 0) { ShowInviteMenu(c); return; }
        var t = Targets.Find(c, string.Join(' ', args), singleOnly: true, respectImmunity: false, allowBots: false);
        if (!t.Ok || t.Players.Count != 1) { Chat.Tell(c, "crews.err.no_target"); return; }
        action(c, t.Players[0]);
    }

    private void Invite(CCSPlayerController c, CCSPlayerController target)
    {
        var crew = CrewOf(c);
        if (crew == null) { Chat.Tell(c, "crews.err.not_member"); return; }
        if (!CrewRules.CanInvite(crew.Member(Steam(c))!.Rank)) { Chat.Tell(c, "crews.err.rank"); return; }
        if (target.IsBot || target.SteamID == 0 || CrewOf(target) != null) { Chat.Tell(c, "crews.err.target_in_crew"); return; }
        if (crew.Members.Count >= SlotsOf(crew)) { Chat.Tell(c, "crews.err.full", SlotsOf(crew)); return; }
        if (CooldownUntil(target.SteamID) > Now) { Chat.Tell(c, "crews.err.target_cooldown"); return; }
        _invites[target.Slot] = (crew.Id, c.PlayerName, Server.EngineTime + Math.Max(10, Cfg.InviteSeconds));
        Chat.Tell(target, "crews.invite.received", Label(crew), Chat.Name(c));
        Chat.Tell(c, "crews.invite.sent", Chat.Name(target));
    }

    private void Accept(CCSPlayerController c)
    {
        if (!_invites.Remove(c.Slot, out var inv) || Server.EngineTime > inv.Expires || !_crews.TryGetValue(inv.CrewId, out var crew))
        {
            Chat.Tell(c, "crews.err.no_invite");
            return;
        }

        if (c.SteamID == 0) { Chat.Tell(c, "crews.err.no_steam"); return; }
        if (CrewOf(c) != null) { Chat.Tell(c, "crews.err.already_member"); return; }
        if (crew.Members.Count >= SlotsOf(crew)) { Chat.Tell(c, "crews.err.full", SlotsOf(crew)); return; }
        crew.Members.Add(new CrewMember { SteamId = Steam(c), Name = c.PlayerName, Rank = CrewRank.Member, JoinedUnix = Now });
        _memberOf[Steam(c)] = crew.Id;
        Save(crew);
        TellCrew(crew, "crews.joined", Chat.Name(c), Label(crew));
        if (c.Jb() is { } jp) ApplyTag(jp);
    }

    private void Decline(CCSPlayerController c)
    {
        if (_invites.Remove(c.Slot)) Chat.Tell(c, "crews.invite.declined");
    }

    // ====================================================================== roster changes

    private void WithMember(CCSPlayerController c, List<string> args, Action<CCSPlayerController, Crew, CrewMember> action)
    {
        var crew = CrewOf(c);
        if (crew == null) { Chat.Tell(c, "crews.err.not_member"); return; }
        if (args.Count == 0) { ShowRoster(c); return; }
        var q = string.Join(' ', args);
        var matches = crew.Members.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) { Chat.Tell(c, "crews.err.no_target"); return; }
        action(c, crew, matches[0]);
    }

    private void Kick(CCSPlayerController c, Crew crew, CrewMember target)
    {
        if (!IsLiveMember(crew, target)) { Chat.Tell(c, "crews.err.no_target"); return; }
        var me = crew.Member(Steam(c));
        if (me == null || target.SteamId == me.SteamId || !CrewRules.CanKick(me.Rank, target.Rank)) { Chat.Tell(c, "crews.err.rank"); return; }
        RemoveMember(crew, target);
        TellCrew(crew, "crews.kicked", target.Name, Chat.Name(c));
        if (Players.Humans().FirstOrDefault(x => Steam(x) == target.SteamId) is { } kicked) Chat.Tell(kicked, "crews.kicked_you", Label(crew));
    }

    private void SetRank(CCSPlayerController c, Crew crew, CrewMember target, bool promote)
    {
        if (!IsLiveMember(crew, target)) { Chat.Tell(c, "crews.err.no_target"); return; }
        if (crew.Member(Steam(c))?.Rank != CrewRank.Boss) { Chat.Tell(c, "crews.err.rank"); return; }
        if (promote)
        {
            if (target.Rank != CrewRank.Member) return;
            if (crew.Members.Count(m => m.Rank == CrewRank.Lieutenant) >= CrewRules.MaxLieutenants(LevelOf(crew))) { Chat.Tell(c, "crews.err.max_lieutenants"); return; }
            target.Rank = CrewRank.Lieutenant;
        }
        else
        {
            if (target.Rank != CrewRank.Lieutenant) return;
            target.Rank = CrewRank.Member;
        }

        Save(crew);
        TellCrew(crew, promote ? "crews.promoted" : "crews.demoted", target.Name);
    }

    private void ConfirmTransfer(CCSPlayerController c, string steam)
    {
        var crew = CrewOf(c);
        var target = crew?.Member(steam);
        if (crew == null || target == null) return;
        var m = new JbMenu(Lang.T("crews.menu.transfer_confirm", Hud.Escape(target.Name)));
        m.Add(Lang.T("crews.menu.no"), ShowRoster);
        m.Add(Lang.T("crews.menu.yes"), p => Transfer(p, crew, target));
        m.Open(c);
    }

    private void Transfer(CCSPlayerController c, Crew crew, CrewMember target)
    {
        if (!IsLiveMember(crew, target)) { Chat.Tell(c, "crews.err.no_target"); return; }
        var me = crew.Member(Steam(c));
        if (me?.Rank != CrewRank.Boss || target.SteamId == me.SteamId) { Chat.Tell(c, "crews.err.rank"); return; }
        if (IsCtBanned(target)) { Chat.Tell(c, "crews.err.target_ctbanned"); return; }
        target.Rank = CrewRank.Boss;
        // The old Boss becomes a Lieutenant while there is room under the level's cap, else a member.
        me.Rank = crew.Members.Count(m => m.Rank == CrewRank.Lieutenant) < CrewRules.MaxLieutenants(LevelOf(crew)) ? CrewRank.Lieutenant : CrewRank.Member;
        Save(crew);
        TellCrew(crew, "crews.new_boss", target.Name, Label(crew));
    }

    private void SetMotd(CCSPlayerController c, string text)
    {
        var crew = CrewOf(c);
        if (crew == null || !CrewRules.CanSetMotd(crew.Member(Steam(c))!.Rank)) { Chat.Tell(c, "crews.err.rank"); return; }
        text = text.Trim().Replace('{', '(').Replace('}', ')');
        crew.Motd = text.Equals("-", StringComparison.Ordinal) ? "" : text.Length > 120 ? text[..120] : text;
        Save(crew);
        TellCrew(crew, "crews.motd_set", crew.Motd.Length > 0 ? crew.Motd : "-");
    }

    private void SetRival(CCSPlayerController c, string tag)
    {
        var crew = CrewOf(c);
        if (crew == null || crew.Member(Steam(c))?.Rank != CrewRank.Boss) { Chat.Tell(c, "crews.err.rank"); return; }
        if (tag.Length == 0 || tag.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            crew.RivalId = "";
            Save(crew);
            TellCrew(crew, "crews.rival_cleared");
            return;
        }

        var other = _crews.Values.FirstOrDefault(x => x.Tag == CrewRules.NormalizeTag(tag));
        if (other == null || other.Id == crew.Id) { Chat.Tell(c, "crews.err.no_such_crew"); return; }
        crew.RivalId = other.Id;
        Save(crew);
        TellCrew(crew, "crews.rival_set", Label(other));
        TellCrew(other, "crews.rival_declared", Label(crew));
    }

    private void ConfirmLeave(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) { Chat.Tell(c, "crews.err.not_member"); return; }
        var m = new JbMenu(Lang.T("crews.menu.leave_confirm", Hud.Escape(crew.Name)));
        m.Add(Lang.T("crews.menu.no"), ShowCrewMenu);
        m.Add(Lang.T("crews.menu.yes"), Leave);
        m.Open(c);
    }

    private void Leave(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        var me = crew?.Member(Steam(c));
        if (crew == null || me == null) return;
        RemoveMember(crew, me);
        SetCooldown(c.SteamID, Cfg.LeaveCooldownMinutes);
        Chat.Tell(c, "crews.left", Label(crew));
        if (_crews.ContainsKey(crew.Id)) TellCrew(crew, "crews.member_left", Chat.Name(c));
    }

    private void ConfirmDisband(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null || crew.Member(Steam(c))?.Rank != CrewRank.Boss) { Chat.Tell(c, "crews.err.rank"); return; }
        var m = new JbMenu(Lang.T("crews.menu.disband_confirm", Hud.Escape(crew.Name)));
        m.Add(Lang.T("crews.menu.no"), ShowCrewMenu);
        m.AddInfo(" ");
        m.Add(Lang.T("crews.menu.yes"), p =>
        {
            if (CrewOf(p) is not { } cr || cr.Member(Steam(p))?.Rank != CrewRank.Boss) return;
            var online = OnlineMembers(cr).ToList();
            DeleteCrew(cr);
            SetCooldown(p.SteamID, Cfg.CreateCooldownMinutes);
            Chat.TellAll("crews.disbanded", Label(cr));
            foreach (var x in online)
                if (x.Jb() is { } jp) ApplyTag(jp);
        });
        m.Open(c);
    }

    /// <summary>Remove a member; the Boss is replaced by the top Lieutenant (else top member), an empty crew disbands.</summary>
    private void RemoveMember(Crew crew, CrewMember member)
    {
        if (!IsLive(crew) || !crew.Members.Remove(member)) return;
        if (_memberOf.TryGetValue(member.SteamId, out var linked) && linked == crew.Id) _memberOf.Remove(member.SteamId);
        if (crew.Members.Count == 0)
        {
            DeleteCrew(crew);
            return;
        }

        if (member.Rank == CrewRank.Boss)
        {
            var heir = crew.Members.OrderBy(IsCtBanned).ThenByDescending(m => m.Rank).ThenByDescending(m => m.Earned).First();
            heir.Rank = CrewRank.Boss;
            TellCrew(crew, "crews.new_boss", heir.Name, Label(crew));
        }

        Save(crew);
        if (Players.Humans().FirstOrDefault(x => Steam(x) == member.SteamId) is { } c && c.Jb() is { } jp) ApplyTag(jp);
    }

    private static bool IsCtBanned(CrewMember m) => ulong.TryParse(m.SteamId, out var s) && Jb.Teams.IsCtBanned(s);

    // ====================================================================== admin

    [ConsoleCommand("css_crewadmin", "sm_crewadmin - manage crews (admin)")]
    public void CmdCrewAdmin(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) { info.ReplyToCommand("[Crews] use css_crews for a list"); return; }
        if (!Perms.Has(caller, "crews.admin")) { Chat.Tell(caller, "core.no_access"); return; }
        ShowAdminList(caller);
    }

    private void ShowAdminList(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("crews.admin.title"));
        foreach (var crew in Ranked())
        {
            var id = crew.Id;
            m.Add(Hud.Escape($"[{crew.Tag}] {crew.Name} · {crew.Members.Count}p · {crew.Respect}/{crew.Legacy}"), p => ShowAdminCrew(p, id));
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("crews.menu.no_crews"));
        m.Open(c);
    }

    private void ShowAdminCrew(CCSPlayerController c, string id)
    {
        if (!_crews.TryGetValue(id, out var crew)) { ShowAdminList(c); return; }
        var m = new JbMenu(Hud.Escape($"[{crew.Tag}] {crew.Name}"));
        m.Subtitle = Hud.Escape($"Boss {crew.Boss?.Name} · respect {crew.Respect} · legacy {crew.Legacy} · {crew.Members.Count} members");
        m.Add(Lang.T("crews.admin.rename"), p => ChatInput.Ask(p, Lang.T("crews.ask.name"), (x, name) =>
        {
            if (!CrewRules.ValidName(name, Cfg.BannedWords, out var why)) Chat.Tell(x, why);
            else if (_crews.Values.Any(o => o.Id != id && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) Chat.Tell(x, "crews.err.name_taken");
            else if (CrewById(id) is { } cr) { cr.Name = name.Trim(); Save(cr); AdminLog(x, "renamed", cr); }
            ShowAdminCrew(x, id);
        }));
        m.Add(Lang.T("crews.admin.retag"), p => ChatInput.Ask(p, Lang.T("crews.ask.tag"), (x, tag) =>
        {
            tag = CrewRules.NormalizeTag(tag);
            if (!CrewRules.ValidTag(tag, Cfg.BannedWords, out var why)) Chat.Tell(x, why);
            else if (_crews.Values.Any(o => o.Id != id && o.Tag == tag)) Chat.Tell(x, "crews.err.tag_taken");
            else if (CrewById(id) is { } cr) { cr.Tag = tag; Save(cr); AdminLog(x, "retagged", cr); }
            ShowAdminCrew(x, id);
        }));
        m.Add(Lang.T("crews.admin.set_respect"), p => ChatInput.Ask(p, Lang.T("crews.admin.ask_respect"), (x, text) =>
        {
            if (int.TryParse(text, out var v) && CrewById(id) is { } cr) { cr.Respect = Math.Max(0, v); Save(cr); AdminLog(x, $"set respect {cr.Respect} for", cr); }
            ShowAdminCrew(x, id);
        }));
        m.Add(Lang.T("crews.admin.kick"), p =>
        {
            var km = new JbMenu(Lang.T("crews.admin.kick"));
            foreach (var mem in crew.Members.ToList())
            {
                var target = mem;
                km.Add(Hud.Escape($"{target.Name} — {RankName(target.Rank)}"), x =>
                {
                    if (!IsLiveMember(crew, target)) { Chat.Tell(x, "crews.err.no_target"); ShowAdminCrew(x, id); return; }
                    RemoveMember(crew, target);
                    AdminLog(x, $"kicked {target.Name} from", crew);
                    if (_crews.ContainsKey(id)) ShowAdminCrew(x, id);
                    else ShowAdminList(x);
                });
            }

            km.Open(p);
        });
        m.Add(Lang.T("crews.admin.disband"), p =>
        {
            var cm = new JbMenu(Lang.T("crews.menu.disband_confirm", Hud.Escape(crew.Name)));
            cm.Add(Lang.T("crews.menu.no"), x => ShowAdminCrew(x, id));
            cm.Add(Lang.T("crews.menu.yes"), x =>
            {
                if (!IsLive(crew)) { ShowAdminList(x); return; }
                var online = OnlineMembers(crew).ToList();
                DeleteCrew(crew);
                AdminLog(x, "disbanded", crew);
                Chat.TellAll("crews.disbanded", Label(crew));
                foreach (var o in online)
                    if (o.Jb() is { } jp) ApplyTag(jp);
                ShowAdminList(x);
            });
            cm.Open(p);
        });
        m.Open(c);
    }

    private void AdminLog(CCSPlayerController admin, string what, Crew crew)
    {
        Logger.LogInformation("[Crews] Admin \"{Admin}\" {What} [{Tag}] {Name} ({Id})", admin.PlayerName, what, crew.Tag, crew.Name, crew.Id);
        Chat.Tell(admin, "crews.admin.done");
    }
}
