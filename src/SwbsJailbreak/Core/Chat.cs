using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace SwbsJailbreak.Core;

/// <summary>
/// Chat / console / centre-text output. All chat output goes through here so the configured prefix and colour
/// tags are applied consistently. Text passed in is either a raw string (may contain {colour} tags) or the
/// result of <see cref="Lang.T"/>.
/// </summary>
public static class Chat
{
    public static string Prefix => JailbreakPlugin.Instance?.Config.General.ChatPrefix ?? "{green}[SWBS JB]{default}";

    private static string Format(string message, bool prefix)
    {
        // A leading space + colour code is required for the first colour in CS2 chat to render.
        var text = prefix ? $" {Prefix} {message}" : $" {{default}}{message}";
        return Colors.Apply(text);
    }

    public static void Print(CCSPlayerController? player, string message, bool prefix = true)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        foreach (var line in message.Split('\n'))
            player.PrintToChat(Format(line, prefix));
    }

    public static void Print(JbPlayer? player, string message, bool prefix = true) => Print(player?.Controller, message, prefix);

    /// <summary>Translate + print.</summary>
    public static void Tell(CCSPlayerController? player, string key, params object?[] args) => Print(player, Lang.T(key, args));

    public static void Tell(JbPlayer? player, string key, params object?[] args) => Print(player?.Controller, Lang.T(key, args));

    public static void PrintAll(string message, bool prefix = true)
    {
        foreach (var p in Players.Humans())
            Print(p, message, prefix);
        Server.PrintToConsole(Colors.Strip(message));
    }

    public static void TellAll(string key, params object?[] args) => PrintAll(Lang.T(key, args));

    public static void PrintTeam(CsTeam team, string message, bool prefix = true)
    {
        foreach (var p in Players.Humans().Where(p => p.Team == team))
            Print(p, message, prefix);
    }

    public static void TellTeam(CsTeam team, string key, params object?[] args) => PrintTeam(team, Lang.T(key, args));

    /// <summary>Print to every admin that has the given permission (e.g. "@css/generic").</summary>
    public static void PrintAdmins(string message, string permission = "@css/generic", bool prefix = true)
    {
        foreach (var p in Players.Humans().Where(p => Perms.Has(p, permission)))
            Print(p, message, prefix);
    }

    public static void PrintToConsole(CCSPlayerController? player, string message)
    {
        var clean = Colors.Strip(message);
        if (player == null) Server.PrintToConsole(clean);
        else if (player.IsValid && !player.IsBot) player.PrintToConsole(clean);
    }

    /// <summary>Reply to whoever ran a command: chat for in-game players (console if the command came from console), server console otherwise.</summary>
    public static void Reply(CCSPlayerController? player, CommandInfo? info, string message)
    {
        if (player == null || !player.IsValid)
        {
            Server.PrintToConsole(Colors.Strip(message));
            return;
        }

        if (info != null && info.CallingContext == CommandCallingContext.Console)
            player.PrintToConsole(Colors.Strip(message));
        else
            Print(player, message);
    }

    public static void ReplyT(CCSPlayerController? player, CommandInfo? info, string key, params object?[] args) =>
        Reply(player, info, Lang.T(key, args));

    /// <summary>Short centre (hint) text.</summary>
    public static void Center(CCSPlayerController? player, string message)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        player.PrintToCenter(Colors.Strip(message));
    }

    public static void CenterAll(string message)
    {
        foreach (var p in Players.Humans()) Center(p, message);
    }

    /// <summary>Big centre alert (round-end style banner).</summary>
    public static void Alert(CCSPlayerController? player, string message)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        player.PrintToCenterAlert(Colors.Strip(message));
    }

    public static void AlertAll(string message)
    {
        foreach (var p in Players.Humans()) Alert(p, message);
    }

    /// <summary>Coloured player name for chat ({gold}Name{default} for T, {lightblue} for CT).</summary>
    public static string Name(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return "{grey}unknown{default}";
        return $"{Colors.TeamColor(player.Team)}{player.PlayerName}{ChatColors.Default}";
    }

    public static string Name(JbPlayer? player)
    {
        if (player == null) return "{grey}unknown{default}";
        var c = player.Controller;
        return c != null ? Name(c) : $"{{grey}}{player.CachedName}{{default}}";
    }
}
