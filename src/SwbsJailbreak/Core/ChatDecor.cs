using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>
/// What modules add to a player's chat lines (crew tag, store title, store chat colour). When anything applies, the chat
/// format module re-prints the line itself; otherwise the game prints it normally.
/// </summary>
public static class ChatDecor
{
    /// <summary>Pieces shown before the name, in registration order (e.g. "{gold}[Crew]{default} ").</summary>
    private static readonly List<(string Owner, Func<CCSPlayerController, string?> Prefix)> Prefixes = new();

    /// <summary>Colour tag of the message text (the first provider that answers wins).</summary>
    private static readonly List<(string Owner, Func<CCSPlayerController, string?> Color)> ColorProviders = new();

    /// <summary>Listeners that take a line for themselves (e.g. Math Race answers): the formatter leaves those lines alone.</summary>
    private static readonly List<(string Owner, Func<CCSPlayerController, string, bool> Takes)> Takers = new();

    public static void AddTaker(string owner, Func<CCSPlayerController, string, bool> takes) => Takers.Add((owner, takes));

    public static bool IsTaken(CCSPlayerController player, string text)
    {
        foreach (var (_, f) in Takers)
        {
            try
            {
                if (f(player, text)) return true;
            }
            catch
            {
                // ignore a broken taker
            }
        }

        return false;
    }

    public static void AddPrefix(string owner, Func<CCSPlayerController, string?> prefix) => Prefixes.Add((owner, prefix));

    public static void AddColor(string owner, Func<CCSPlayerController, string?> color) => ColorProviders.Add((owner, color));

    public static void Remove(string owner)
    {
        Prefixes.RemoveAll(p => p.Owner == owner);
        ColorProviders.RemoveAll(c => c.Owner == owner);
        Takers.RemoveAll(t => t.Owner == owner);
    }

    internal static void Clear()
    {
        Prefixes.Clear();
        ColorProviders.Clear();
        Takers.Clear();
    }

    /// <summary>The decorations of this player's lines; both empty when nothing applies.</summary>
    public static (string Prefix, string Color) For(CCSPlayerController player)
    {
        var prefix = "";
        foreach (var (_, f) in Prefixes)
        {
            try { prefix += f(player) ?? ""; }
            catch { /* a broken provider must not eat chat */ }
        }

        var color = "";
        foreach (var (_, f) in ColorProviders)
        {
            string? c;
            try { c = f(player); }
            catch { c = null; }
            if (string.IsNullOrEmpty(c)) continue;
            color = c;
            break;
        }

        return (prefix, color);
    }

    /// <summary>Engine-free (unit-tested): the full line, with player text stripped of colour tags.</summary>
    public static string Format(string prefix, string name, string color, string text)
    {
        text = Colors.Strip(text).Replace("{", "(").Replace("}", ")");
        return $"{prefix}{name}{{default}}: {color}{text}";
    }
}
