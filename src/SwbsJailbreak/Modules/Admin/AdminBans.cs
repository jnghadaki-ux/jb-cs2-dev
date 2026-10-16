using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

public sealed class BanRecord
{
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Admin { get; set; } = "";
    public string AdminSteamId { get; set; } = "";
    public long Created { get; set; }
    public long Expires { get; set; } // unix seconds, 0 = permanent
    public string Ip { get; set; } = "";
}

public sealed class WarnRecord
{
    public string Reason { get; set; } = "";
    public string Admin { get; set; } = "";
    public long Time { get; set; }
}

/// <summary>
/// SourceMod basebans (ban / unban / addban / banip), basecommands kick, and the owner's SimpleAdmin ban list and
/// warning system. CS2 servers keep no ban list of their own here, so bans are stored by this plugin
/// (data/bans.json, data/ipbans.json) and enforced when a player is authorised / connects.
/// </summary>
public sealed partial class AdminModule
{
    private const string BanNs = "bans";
    private const string IpBanNs = "ipbans";
    private const string WarnNs = "warnings";

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool IsActive(BanRecord b) => b.Expires == 0 || b.Expires > Now;

    private void OnClientAuthorized(int slot, SteamID steamId)
    {
        var id = steamId.SteamId64;
        var ban = Storage.Get<BanRecord>(BanNs, id.ToString());
        if (ban == null) return;
        if (!IsActive(ban))
        {
            Storage.Delete(BanNs, id.ToString());
            return;
        }

        Server.NextFrame(() =>
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c is { IsValid: true }) Kick(c, BanMessage(ban));
        });
    }

    private void OnClientConnect(int slot, string name, string ipAddress)
    {
        var ip = ipAddress.Split(':')[0];
        if (ip.Length == 0) return;
        var ban = Storage.Get<BanRecord>(IpBanNs, ip);
        if (ban == null) return;
        if (!IsActive(ban))
        {
            Storage.Delete(IpBanNs, ip);
            return;
        }

        Plugin.AddTimer(1.0f, () =>
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c is { IsValid: true }) Kick(c, BanMessage(ban));
        });
    }

    private static string BanMessage(BanRecord b) =>
        b.Expires == 0
            ? Colors.Strip(Lang.T("admin.ban.kick_permanent", b.Reason))
            : Colors.Strip(Lang.T("admin.ban.kick_timed", b.Reason, Math.Max(1, (int)Math.Ceiling((b.Expires - Now) / 60.0))));

    [ConsoleCommand("css_kick", "sm_kick <#userid|name> [reason]")]
    public void CmdKick(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "kick")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!kick <target> [reason]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        var reason = ArgsFrom(info, 2);
        if (reason.Length == 0) reason = Lang.T("admin.kick.default_reason");
        Activity(caller, "admin.act.kick", t.Description, reason);
        foreach (var c in t.Players.ToList()) Kick(c, reason);
    }

    [ConsoleCommand("css_ban", "sm_ban <#userid|name> <minutes|0> [reason]")]
    public void CmdBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "ban")) return;
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var minutes) || minutes < 0)
        {
            Chat.ReplyT(caller, info, "core.usage", "!ban <target> <minutes|0> [reason]");
            return;
        }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        var target = t.Players[0];
        if (target.IsBot) { Chat.ReplyT(caller, info, "admin.ban.bot"); return; }
        var reason = ArgsFrom(info, 3);
        if (reason.Length == 0) reason = Lang.T("admin.ban.default_reason");
        AddBan(target.SteamID, target.PlayerName, minutes, reason, caller, "");
        Activity(caller, "admin.act.ban", target.PlayerName, FormatDuration(minutes), reason);
        Kick(target, BanMessage(Storage.Get<BanRecord>(BanNs, target.SteamID.ToString())!));
    }

    [ConsoleCommand("css_addban", "sm_addban <minutes|0> <steamid> [reason]")]
    public void CmdAddBan(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "ban")) return; // Level 2+, like !ban
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(1), out var minutes) || minutes < 0)
        {
            Chat.ReplyT(caller, info, "core.usage", "!addban <minutes|0> <steamid> [reason]");
            return;
        }

        var id = ParseSteamId(info.GetArg(2));
        if (id == 0) { Chat.ReplyT(caller, info, "admin.ban.bad_steamid", info.GetArg(2)); return; }
        var reason = ArgsFrom(info, 3);
        if (reason.Length == 0) reason = Lang.T("admin.ban.default_reason");
        AddBan(id, "", minutes, reason, caller, "");
        Activity(caller, "admin.act.addban", id, FormatDuration(minutes), reason);
        var online = Players.All().FirstOrDefault(c => c.SteamID == id);
        if (online != null) Kick(online, BanMessage(Storage.Get<BanRecord>(BanNs, id.ToString())!));
    }

    [ConsoleCommand("css_banip", "sm_banip <ip|#userid|name> <minutes|0> [reason]")]
    public void CmdBanIp(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "ban")) return;
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var minutes) || minutes < 0)
        {
            Chat.ReplyT(caller, info, "core.usage", "!banip <ip|target> <minutes|0> [reason]");
            return;
        }

        var arg = info.GetArg(1);
        string ip;
        CCSPlayerController? target = null;
        if (System.Net.IPAddress.TryParse(arg, out _)) ip = arg;
        else
        {
            var t = Targets.FindOrReply(caller, info, arg, singleOnly: true);
            if (t == null) return;
            target = t.Players[0];
            ip = (target.IpAddress ?? "").Split(':')[0];
            if (ip.Length == 0) { Chat.ReplyT(caller, info, "admin.ban.no_ip"); return; }
        }

        var reason = ArgsFrom(info, 3);
        if (reason.Length == 0) reason = Lang.T("admin.ban.default_reason");
        var rec = new BanRecord
        {
            Name = target?.PlayerName ?? "", Reason = reason, Admin = AdminName(caller), AdminSteamId = caller?.SteamID.ToString() ?? "",
            Created = Now, Expires = minutes == 0 ? 0 : Now + minutes * 60L, Ip = ip
        };
        Storage.Set(IpBanNs, ip, rec);
        Activity(caller, "admin.act.banip", target?.PlayerName ?? ip, FormatDuration(minutes), reason);
        foreach (var c in Players.All().Where(c => (c.IpAddress ?? "").Split(':')[0] == ip).ToList()) Kick(c, BanMessage(rec));
    }

    [ConsoleCommand("css_unban", "sm_unban <steamid|ip>")]
    public void CmdUnban(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "unban")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!unban <steamid|ip>"); return; }
        var arg = info.GetArg(1);
        if (System.Net.IPAddress.TryParse(arg, out _))
        {
            if (!Storage.Has(IpBanNs, arg)) { Chat.ReplyT(caller, info, "admin.ban.not_found", arg); return; }
            Storage.Delete(IpBanNs, arg);
            Activity(caller, "admin.act.unban", arg);
            return;
        }

        var id = ParseSteamId(arg);
        if (id == 0) { Chat.ReplyT(caller, info, "admin.ban.bad_steamid", arg); return; }
        if (!Storage.Has(BanNs, id.ToString())) { Chat.ReplyT(caller, info, "admin.ban.not_found", arg); return; }
        Storage.Delete(BanNs, id.ToString());
        Activity(caller, "admin.act.unban", id);
    }

    [ConsoleCommand("css_banlist", "List active bans")]
    public void CmdBanList(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "ban")) return;
        var bans = Storage.All<BanRecord>(BanNs).Where(kv => IsActive(kv.Value)).ToList();
        var ipBans = Storage.All<BanRecord>(IpBanNs).Where(kv => IsActive(kv.Value)).ToList();
        if (bans.Count == 0 && ipBans.Count == 0) { Chat.ReplyT(caller, info, "admin.ban.none"); return; }
        foreach (var (id, b) in bans.Concat(ipBans).Take(50))
        {
            var left = b.Expires == 0 ? FormatDuration(0) : FormatDuration((int)Math.Ceiling((b.Expires - Now) / 60.0));
            Chat.PrintToConsole(caller, $"{id}  {b.Name}  [{left}]  by {b.Admin}: {b.Reason}");
        }

        Chat.ReplyT(caller, info, "admin.ban.list_console", bans.Count + ipBans.Count);
    }

    internal void AddBan(ulong steamId, string name, int minutes, string reason, CCSPlayerController? admin, string ip)
    {
        Storage.Set(BanNs, steamId.ToString(), new BanRecord
        {
            Name = name, Reason = reason, Admin = AdminName(admin), AdminSteamId = admin?.SteamID.ToString() ?? "",
            Created = Now, Expires = minutes == 0 ? 0 : Now + minutes * 60L, Ip = ip
        });
        Storage.Flush();
    }

    // ------------------------------------------------------------------ warnings (owner's SimpleAdmin)

    [ConsoleCommand("css_warn", "Warn a player: !warn <target> <reason>")]
    public void CmdWarn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!warn <target> <reason>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        var target = t.Players[0];
        var reason = ArgsFrom(info, 2);
        if (reason.Length == 0) reason = Lang.T("admin.warn.default_reason");
        var list = AddWarning(target, reason, caller);
        Chat.Tell(target, "admin.warn.you", reason, list.Count);
        Activity(caller, "admin.act.warn", target.PlayerName, reason, list.Count);
    }

    /// <summary>
    /// Warnings are counted per warned player. Bots and players without a verified SteamID (insecure / LAN servers) all
    /// report SteamID 0, which made every one of them share a single warning list, so those fall back to their name.
    /// </summary>
    internal static string WarnKey(CCSPlayerController c) => c.SteamID != 0 ? c.SteamID.ToString() : "name:" + c.PlayerName;

    internal List<WarnRecord> AddWarning(CCSPlayerController target, string reason, CCSPlayerController? admin)
    {
        var key = WarnKey(target);
        var list = Storage.Get<List<WarnRecord>>(WarnNs, key) ?? new List<WarnRecord>();
        list.Add(new WarnRecord { Reason = reason, Admin = AdminName(admin), Time = Now });
        Storage.Set(WarnNs, key, list);
        Storage.Flush();
        return list;
    }

    [ConsoleCommand("css_warns", "Show a player's warnings: !warns <target>")]
    public void CmdWarns(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!warns <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        var list = Storage.Get<List<WarnRecord>>(WarnNs, WarnKey(target)) ?? new List<WarnRecord>();
        if (list.Count == 0) { Chat.ReplyT(caller, info, "admin.warn.none", target.PlayerName); return; }
        for (var i = 0; i < list.Count; i++)
            Chat.Reply(caller, info, $"{{grey}}#{i + 1}{{default}} {DateTimeOffset.FromUnixTimeSeconds(list[i].Time):yyyy-MM-dd} {{lightred}}{list[i].Admin}{{default}}: {list[i].Reason}");
    }

    [ConsoleCommand("css_unwarn", "Clear a player's warnings: !unwarn <target>")]
    public void CmdUnwarn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!unwarn <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        Storage.Delete(WarnNs, WarnKey(t.Players[0]));
        Activity(caller, "admin.act.unwarn", t.Players[0].PlayerName);
    }
}
