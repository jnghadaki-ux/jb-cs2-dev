using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>Stored timed communication punishment (SMPlugins TimedMuteGag).</summary>
public sealed class CommRecord
{
    public string Name { get; set; } = "";
    public long VoiceUntil { get; set; }   // unix seconds, -1 = permanent, 0 = none
    public long ChatUntil { get; set; }
    public string Reason { get; set; } = "";
    public string Admin { get; set; } = "";
}

/// <summary>
/// SourceMod basecomm (mute/gag/silence) + SMPlugins TimedMuteGag (timed + persistent) + basechat (say/csay/hsay/psay/chat).
/// Without a time a punishment lasts until the player disconnects (basecomm); with minutes it is stored and re-applied
/// on reconnect until it expires (TimedMuteGag). Voice muting sets VoiceFlags.Muted and the JbPlayer flag the jailbreak
/// voice rules honour.
/// </summary>
public sealed partial class AdminModule
{
    private const string CommNs = "comms";

    private enum Comm { Mute, Gag, Silence }

    [ConsoleCommand("css_mute", "sm_mute <player> [minutes] [reason] - remove voice")]
    [ConsoleCommand("css_timed_mute", "Timed voice mute")]
    public void CmdMute(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Mute, true);

    [ConsoleCommand("css_gag", "sm_gag <player> [minutes] [reason] - remove text chat")]
    [ConsoleCommand("css_timed_gag", "Timed chat gag")]
    public void CmdGag(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Gag, true);

    [ConsoleCommand("css_silence", "sm_silence <player> [minutes] [reason] - remove voice and chat")]
    [ConsoleCommand("css_timed_silence", "Timed silence")]
    public void CmdSilence(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Silence, true);

    [ConsoleCommand("css_unmute", "sm_unmute <player>")]
    [ConsoleCommand("css_timed_unmute", "Remove a timed mute")]
    public void CmdUnmute(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Mute, false);

    [ConsoleCommand("css_ungag", "sm_ungag <player>")]
    [ConsoleCommand("css_timed_ungag", "Remove a timed gag")]
    public void CmdUngag(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Gag, false);

    [ConsoleCommand("css_unsilence", "sm_unsilence <player>")]
    [ConsoleCommand("css_timed_unsilence", "Remove a timed silence")]
    public void CmdUnsilence(CCSPlayerController? caller, CommandInfo info) => CommCommand(caller, info, Comm.Silence, false);

    private void CommCommand(CCSPlayerController? caller, CommandInfo info, Comm kind, bool apply)
    {
        if (!Require(caller, info, "mute")) return;
        var name = kind.ToString().ToLowerInvariant();
        if (info.ArgCount < 2)
        {
            Chat.ReplyT(caller, info, "core.usage", apply ? $"!{name} <target> [minutes] [reason]" : $"!un{name} <target>");
            return;
        }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        var minutes = -1; // session only
        var reasonIndex = 2;
        if (apply && info.ArgCount > 2 && int.TryParse(info.GetArg(2), out var m))
        {
            minutes = Math.Max(0, m);
            reasonIndex = 3;
        }

        var reason = apply ? ArgsFrom(info, reasonIndex) : "";
        foreach (var c in t.Players)
        {
            var p = c.Jb();
            if (p == null) continue;
            var voice = kind is Comm.Mute or Comm.Silence;
            var chat = kind is Comm.Gag or Comm.Silence;
            if (voice) p.AdminVoiceMuted = apply;
            if (chat) p.AdminChatGagged = apply;
            ApplyVoice(p);
            StoreComm(p, voice, chat, apply, minutes, reason, AdminName(caller));
            Chat.Tell(c, apply ? $"admin.comm.{name}.you" : $"admin.comm.un{name}.you", minutes > 0 ? FormatDuration(minutes) : minutes == 0 ? FormatDuration(0) : Lang.T("admin.time.session"));
        }

        var duration = minutes > 0 ? FormatDuration(minutes) : minutes == 0 ? FormatDuration(0) : Lang.T("admin.time.session");
        Activity(caller, apply ? $"admin.act.{name}" : $"admin.act.un{name}", t.Description, duration);
    }

    private static void ApplyVoice(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (p.AdminVoiceMuted) c.VoiceFlags = CounterStrikeSharp.API.VoiceFlags.Muted;
        else if (c.VoiceFlags.HasFlag(CounterStrikeSharp.API.VoiceFlags.Muted)) c.VoiceFlags = CounterStrikeSharp.API.VoiceFlags.Normal;
        Jb.Mute.Refresh(p); // let the jailbreak voice rules re-evaluate (they keep AdminVoiceMuted players muted)
    }

    private static void StoreComm(JbPlayer p, bool voice, bool chat, bool apply, int minutes, string reason, string admin)
    {
        if (p.SteamId64 == 0) return;
        var key = p.SteamId64.ToString();
        var rec = Storage.Get<CommRecord>(CommNs, key) ?? new CommRecord();
        var until = !apply ? 0 : minutes < 0 ? 0 : minutes == 0 ? -1 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minutes * 60L;
        if (voice) rec.VoiceUntil = until;
        if (chat) rec.ChatUntil = until;
        rec.Name = p.Name;
        if (apply) { rec.Reason = reason; rec.Admin = admin; }
        if (rec.VoiceUntil == 0 && rec.ChatUntil == 0) Storage.Delete(CommNs, key);
        else Storage.Set(CommNs, key, rec);
    }

    private static bool Active(long until) => until == -1 || until > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Re-apply stored timed punishments when a player joins.</summary>
    private void ApplyStoredComms(JbPlayer p)
    {
        if (p.SteamId64 == 0) return;
        var rec = Storage.Get<CommRecord>(CommNs, p.SteamId64.ToString());
        if (rec == null) return;
        p.AdminVoiceMuted = Active(rec.VoiceUntil);
        p.AdminChatGagged = Active(rec.ChatUntil);
        if (!p.AdminVoiceMuted && !p.AdminChatGagged)
        {
            Storage.Delete(CommNs, p.SteamId64.ToString());
            return;
        }

        var slot = p.Slot;
        Server.NextFrame(() => ApplyVoice(Players.Get(slot)));
    }

    [ConsoleCommand("css_check_mute", "Show a player's mute status")]
    [ConsoleCommand("css_check_gag", "Show a player's gag status")]
    [ConsoleCommand("css_check_silence", "Show a player's silence status")]
    public void CmdCheckComm(CCSPlayerController? caller, CommandInfo info)
    {
        var target = caller;
        if (info.ArgCount > 1)
        {
            if (!Require(caller, info, "mute")) return;
            var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
            if (t == null) return;
            target = t.Players[0];
        }

        if (target == null) return;
        var p = target.Jb();
        if (p == null) return;
        var rec = p.SteamId64 != 0 ? Storage.Get<CommRecord>(CommNs, p.SteamId64.ToString()) : null;
        string Describe(bool on, long until) => !on ? Lang.T("admin.comm.none")
            : until == -1 ? FormatDuration(0)
            : until > 0 ? Lang.T("admin.comm.expires", Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 60.0)))
            : Lang.T("admin.time.session");
        Chat.ReplyT(caller, info, "admin.comm.status", target.PlayerName,
            Describe(p.AdminVoiceMuted, rec?.VoiceUntil ?? 0), Describe(p.AdminChatGagged, rec?.ChatUntil ?? 0));
    }

    // ------------------------------------------------------------------ basechat

    [ConsoleCommand("css_say", "sm_say <message> - admin message to everyone")]
    public void CmdSay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        var msg = ArgsFrom(info, 1);
        if (msg.Length == 0) { Chat.ReplyT(caller, info, "core.usage", "!say <message>"); return; }
        Chat.PrintAll(Lang.T("admin.chat.say", AdminName(caller), msg), false);
        LogActivity(caller, "say: " + msg);
    }

    [ConsoleCommand("css_csay", "sm_csay <message> - centre message to everyone")]
    [ConsoleCommand("css_hsay", "sm_hsay <message> - hint message to everyone")]
    public void CmdCsay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        var msg = ArgsFrom(info, 1);
        if (msg.Length == 0) { Chat.ReplyT(caller, info, "core.usage", "!csay <message>"); return; }
        Hud.SetGlobal("admin.csay", $"<font color='#FF5555'>{Hud.Escape(AdminName(caller))}:</font> {Hud.Escape(msg)}", 120, 6f);
        LogActivity(caller, "csay: " + msg);
    }

    [ConsoleCommand("css_psay", "sm_psay <player> <message> - private message")]
    public void CmdPsay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        if (info.ArgCount < 3) { Chat.ReplyT(caller, info, "core.usage", "!psay <target> <message>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var msg = ArgsFrom(info, 2);
        var target = t.Players[0];
        Chat.Print(target, Lang.T("admin.chat.psay_from", AdminName(caller), msg), false);
        Chat.Reply(caller, info, Lang.T("admin.chat.psay_to", target.PlayerName, msg));
        LogActivity(caller, $"psay to {target.PlayerName}: {msg}");
    }

    [ConsoleCommand("css_chat", "sm_chat <message> - message to all admins")]
    [ConsoleCommand("css_asay", "Message to all admins")]
    public void CmdAdminChat(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        var msg = ArgsFrom(info, 1);
        if (msg.Length == 0) { Chat.ReplyT(caller, info, "core.usage", "!chat <message>"); return; }
        var line = Lang.T("admin.chat.admin", AdminName(caller), msg);
        foreach (var c in Players.Humans().Where(c => Perms.IsAdmin(c))) Chat.Print(c, line, false);
        if (caller != null && !Perms.IsAdmin(caller)) Chat.Print(caller, line, false);
        Server.PrintToConsole(Colors.Strip(line));
        LogActivity(caller, "admin chat: " + msg);
    }
}
