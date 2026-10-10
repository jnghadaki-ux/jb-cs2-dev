using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace SwbsJailbreak.Core;

/// <summary>
/// "Type it in chat" prompts (the original menus' OnClientSayCommand_Post pattern: zone names, trigger names, crew names).
/// While a prompt is pending, the player's next plain chat line is captured and not shown to anyone; lines starting with
/// ! or / still run as commands, and "cancel" aborts. Prompts expire and are dropped on disconnect / map change.
/// </summary>
public static class ChatInput
{
    private sealed record Pending(Action<CCSPlayerController, string> OnText, Action<CCSPlayerController>? OnCancel, double Expires);

    private static readonly Dictionary<int, Pending> Prompts = new();

    public static bool IsWaiting(CCSPlayerController? player) =>
        player != null && Prompts.TryGetValue(player.Slot, out var p) && Server.EngineTime < p.Expires;

    /// <summary>Ask the player for one line of text. A new prompt replaces an older one.</summary>
    public static void Ask(CCSPlayerController player, string prompt, Action<CCSPlayerController, string> onText,
        Action<CCSPlayerController>? onCancel = null, float timeoutSeconds = 60f)
    {
        if (!player.IsValid || player.IsBot) return;
        Prompts[player.Slot] = new Pending(onText, onCancel, Server.EngineTime + Math.Max(5f, timeoutSeconds));
        Chat.Print(player, prompt);
        Chat.Tell(player, "core.input.hint");
    }

    public static void Cancel(CCSPlayerController? player)
    {
        if (player == null || !Prompts.Remove(player.Slot, out var p)) return;
        try { p.OnCancel?.Invoke(player); }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>"say" / "say_team" listener, registered by the plugin before any module listener.</summary>
    internal static HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || !Prompts.TryGetValue(player.Slot, out var p)) return HookResult.Continue;
        if (Server.EngineTime >= p.Expires)
        {
            Prompts.Remove(player.Slot);
            return HookResult.Continue;
        }

        var text = info.GetArg(1).Trim();
        if (text.Length == 0 || text[0] == '!' || text[0] == '/') return HookResult.Continue;
        Prompts.Remove(player.Slot);
        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            try { p.OnCancel?.Invoke(player); }
            catch (Exception ex) { Log(ex); }
            Chat.Tell(player, "core.input.cancelled");
            return HookResult.Handled;
        }

        // Run on the next frame: the callback may open menus or change entities, which must not happen inside the hook.
        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;
            try { p.OnText(player, text.Length > 255 ? text[..255] : text); }
            catch (Exception ex) { Log(ex); }
        });
        return HookResult.Handled;
    }

    internal static void Forget(int slot) => Prompts.Remove(slot);

    internal static void Reset() => Prompts.Clear();

    private static void Log(Exception ex) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogError(JailbreakPlugin.Instance!.Logger, ex, "[ChatInput] callback failed");
}
