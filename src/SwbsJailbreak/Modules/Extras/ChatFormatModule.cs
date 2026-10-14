using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>
/// Prints chat lines that carry decorations (<see cref="ChatDecor"/>: crew tags, store titles, store chat colours) the way
/// the game would - dead / team markers, team-only for say_team - and suppresses the game's own line. Lines without
/// decorations, commands, spam-blocked lines, gagged players and chat-input answers are left alone.
/// </summary>
public sealed class ChatFormatModule : JbModule
{
    public override string Name => "ChatFormat";
    public override int Order => 4; // after ChatSpam (3)

    public override void OnLoad(bool hotReload)
    {
        Plugin.AddCommandListener("say", (c, i) => OnChat(c, i, false), HookMode.Pre);
        Plugin.AddCommandListener("say_team", (c, i) => OnChat(c, i, true), HookMode.Pre);
    }

    private static HookResult OnChat(CCSPlayerController? player, CommandInfo info, bool team)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        var text = info.GetArg(1).Trim();
        if (text.Length == 0 || text[0] is '!' or '/' or '@') return HookResult.Continue;
        if (ChatInput.IsWaiting(player) || player.Jb() is { AdminChatGagged: true }) return HookResult.Continue;
        if (ChatSpamModule.Blocks(player, text)) return HookResult.Continue; // the spam listener blocks it
        if (ChatDecor.IsTaken(player, text)) return HookResult.Continue;   // e.g. a Math Race answer

        var (prefix, color) = ChatDecor.For(player);
        if (prefix.Length == 0 && color.Length == 0) return HookResult.Continue;

        var head = prefix;
        if (!player.IsAliveP() && player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) head = Lang.T("crews.chattag.dead") + head;
        if (team) head = Lang.T(player.Team switch
        {
            CsTeam.Terrorist => "crews.chattag.team_t",
            CsTeam.CounterTerrorist => "crews.chattag.team_ct",
            _ => "crews.chattag.team_spec"
        }) + head;
        var line = ChatDecor.Format(head, Chat.Name(player), color, text);

        foreach (var c in Players.Humans())
        {
            if (team && c.Team != player.Team) continue;
            Chat.Print(c, line, false);
        }

        Server.PrintToConsole(Colors.Strip($"{(team ? "(team) " : "")}{prefix}{player.PlayerName}: {text}"));
        return HookResult.Handled;
    }
}
