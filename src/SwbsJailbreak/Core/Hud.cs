using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>
/// Centre-screen HTML compositor. CS2 only shows one centre HTML message at a time, so every module that wants
/// centre text (warden info, LR/day timers, countdowns, menus) registers a named layer here and the compositor
/// merges the highest priority layers into a single PrintToCenterHtml call per player.
///
/// Priorities: menus are drawn exclusively by <see cref="MenuService"/>. Suggested layer priorities:
/// 100 countdowns / alerts, 50 LR / day status, 10 warden info, 0 misc.
/// </summary>
public static class Hud
{
    private sealed class Layer
    {
        public required string Html;
        public int Priority;
        public float ExpiresAt; // 0 = never
    }

    private static readonly Dictionary<string, Layer>[] PlayerLayers =
        Enumerable.Range(0, Players.MaxSlots).Select(_ => new Dictionary<string, Layer>()).ToArray();

    private static readonly Dictionary<string, (Layer Layer, Func<CCSPlayerController, bool>? Filter)> GlobalLayers = new();

    private static readonly string[] LastSent = new string[Players.MaxSlots];
    private static readonly float[] LastSentAt = new float[Players.MaxSlots];

    /// <summary>Max simultaneous non-menu layers shown.</summary>
    public static int MaxLayers { get; set; } = 3;

    public static void Set(CCSPlayerController? player, string key, string html, int priority = 0, float seconds = 0)
    {
        if (player == null || !player.IsValid) return;
        PlayerLayers[player.Slot][key] = new Layer
        {
            Html = html,
            Priority = priority,
            ExpiresAt = seconds > 0 ? Server.CurrentTime + seconds : 0
        };
    }

    public static void Clear(CCSPlayerController? player, string key)
    {
        if (player == null || !player.IsValid) return;
        PlayerLayers[player.Slot].Remove(key);
    }

    public static void Clear(int slot, string key)
    {
        if (slot is >= 0 and < Players.MaxSlots) PlayerLayers[slot].Remove(key);
    }

    /// <summary>A layer shown to every player for whom <paramref name="filter"/> returns true (all if null).</summary>
    public static void SetGlobal(string key, string html, int priority = 0, float seconds = 0, Func<CCSPlayerController, bool>? filter = null)
    {
        GlobalLayers[key] = (new Layer
        {
            Html = html,
            Priority = priority,
            ExpiresAt = seconds > 0 ? Server.CurrentTime + seconds : 0
        }, filter);
    }

    public static void ClearGlobal(string key) => GlobalLayers.Remove(key);

    /// <summary>Clear every layer whose key starts with prefix (all players + global).</summary>
    public static void ClearPrefix(string prefix)
    {
        foreach (var dict in PlayerLayers)
            foreach (var k in dict.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                dict.Remove(k);
        foreach (var k in GlobalLayers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            GlobalLayers.Remove(k);
    }

    internal static void ResetSlot(int slot)
    {
        if (slot is < 0 or >= Players.MaxSlots) return;
        PlayerLayers[slot].Clear();
        LastSent[slot] = "";
        LastSentAt[slot] = 0;
    }

    internal static void ResetAll()
    {
        for (var i = 0; i < Players.MaxSlots; i++) ResetSlot(i);
        GlobalLayers.Clear();
    }

    /// <summary>Compose the non-menu content for a player ("" if nothing).</summary>
    internal static string Compose(CCSPlayerController player)
    {
        var now = Server.CurrentTime;
        var dict = PlayerLayers[player.Slot];
        List<Layer>? active = null;

        foreach (var (key, layer) in dict.ToList())
        {
            if (layer.ExpiresAt > 0 && layer.ExpiresAt < now)
            {
                dict.Remove(key);
                continue;
            }

            (active ??= new()).Add(layer);
        }

        foreach (var (key, (layer, filter)) in GlobalLayers.ToList())
        {
            if (layer.ExpiresAt > 0 && layer.ExpiresAt < now)
            {
                GlobalLayers.Remove(key);
                continue;
            }

            if (filter != null && !filter(player)) continue;
            (active ??= new()).Add(layer);
        }

        if (active == null || active.Count == 0) return "";
        return string.Join("<br>", active.OrderByDescending(l => l.Priority).Take(MaxLayers).Select(l => l.Html));
    }

    /// <summary>Called by the HUD/menu service. Sends html if changed or the refresh interval elapsed.</summary>
    internal static void Send(CCSPlayerController player, string html, bool force = false)
    {
        var slot = player.Slot;
        var now = Server.CurrentTime;
        var last = LastSent[slot] ?? "";
        if (html.Length == 0)
        {
            if (last.Length == 0) return;
            LastSent[slot] = "";
            player.PrintToCenterHtml(" ", 1);
            return;
        }

        if (!force && html == last && now - LastSentAt[slot] < 0.5f) return;
        LastSent[slot] = html;
        LastSentAt[slot] = now;
        player.PrintToCenterHtml(html, 1);
    }

    // ---------------- small HTML helpers ----------------

    public static string Color(string text, string color) => $"<font color='{color}'>{text}</font>";

    public static string Small(string text) => $"<font class='fontSize-s'>{text}</font>";

    public static string Big(string text) => $"<font class='fontSize-l'>{text}</font>";

    public static string Bold(string text) => $"<b>{text}</b>";

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Time(float seconds)
    {
        if (seconds < 0) seconds = 0;
        var s = (int)MathF.Ceiling(seconds);
        return $"{s / 60}:{s % 60:00}";
    }
}
