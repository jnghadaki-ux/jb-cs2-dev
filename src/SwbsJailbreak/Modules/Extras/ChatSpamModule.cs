using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "ChatSpam" (SMPlugins NoTextSpam.sp v1.2).</summary>
public sealed class ChatSpamConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Gag (for the rest of the session) after repeated spam, like the original BaseComm gag.</summary>
    public bool GagSpammers { get; set; } = true;

    /// <summary>Admins (capability "chat") are never limited.</summary>
    public bool AdminsExempt { get; set; } = true;
}

/// <summary>
/// Port of NoTextSpam.sp on top of <see cref="SpamGuard"/>. Commands (! and /) are not limited, so a spam block never
/// eats a command. Other chat listeners that re-print lines (crew chat tags) ask <see cref="Blocks"/> so a blocked line
/// is not printed by them either.
/// </summary>
public sealed class ChatSpamModule : JbModule
{
    private ChatSpamConfig Cfg => Section<ChatSpamConfig>("ChatSpam");

    public override string Name => "ChatSpam";
    public override int Order => 3;
    public override bool Enabled => Cfg.Enabled;

    /// <summary>Shared guard; null while the module is disabled or unloaded.</summary>
    internal static SpamGuard? Guard { get; private set; }

    private static bool _adminsExempt = true;

    public override void OnLoad(bool hotReload)
    {
        Guard = new SpamGuard();
        _adminsExempt = Cfg.AdminsExempt;
        Plugin.AddCommandListener("say", OnChat, HookMode.Pre);
        Plugin.AddCommandListener("say_team", OnChat, HookMode.Pre);
    }

    public override void OnUnload(bool hotReload) => Guard = null;

    public override void OnPlayerDisconnect(JbPlayer player) => Guard?.Forget(player.Slot);

    /// <summary>True when the spam guard blocks this chat line (also used by the crew chat-tag listener).</summary>
    internal static bool Blocks(CCSPlayerController player, string text)
    {
        var g = Guard;
        if (g == null || player.IsBot) return false;
        text = text.Trim();
        if (text.Length == 0 || text[0] is '!' or '/') return false;
        if (_adminsExempt && Perms.Has(player, "chat")) return false;
        return g.Check(player.Slot, text, Server.EngineTime).Blocked;
    }

    private HookResult OnChat(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot || Guard == null) return HookResult.Continue;
        var text = info.GetArg(1).Trim();
        if (text.Length == 0 || text[0] is '!' or '/') return HookResult.Continue;
        if (_adminsExempt && Perms.Has(player, "chat")) return HookResult.Continue;
        if (player.Jb() is { AdminChatGagged: true }) return HookResult.Continue; // the gag listener answers

        var v = Guard.Check(player.Slot, text, Server.EngineTime);
        if (v.Gag && Cfg.GagSpammers && player.Jb() is { } p)
        {
            p.AdminChatGagged = true;
            Chat.Tell(player, "spam.gagged");
            Jb.Log.LogEvent($"{player.PlayerName} was gagged for chat spam", p, null, LogType.Admin);
        }
        else if (v.WarnKey != null)
        {
            Chat.Tell(player, v.WarnKey);
        }

        return v.Blocked ? HookResult.Handled : HookResult.Continue;
    }
}
