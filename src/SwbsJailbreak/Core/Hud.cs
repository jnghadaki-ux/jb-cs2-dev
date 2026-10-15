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
            // Nothing is sent: the last message expires by itself within its 1 s duration. Sending " " to clear it drew
            // an empty white panel on current CS2 builds.
            LastSent[slot] = "";
            return;
        }

        if (!force && html == last && now - LastSentAt[slot] < 0.5f) return;
        LastSent[slot] = html;
        LastSentAt[slot] = now;
        player.PrintToCenterHtml(html, 1);
    }

    // ---------------- flashing fix ----------------

    private static CCSGameRulesProxy? _rulesProxy;
    private static float _nextRulesLookup;

    /// <summary>
    /// CS2 re-fades the centre HTML panel about once a second, so menus and HUD text flash. Holding
    /// CCSGameRules.GameRestart true while no restart is pending keeps the panel steady (workaround found by Poggu, as
    /// used by CS2FlashingHtmlHudFix and eGo). A real restart (mp_restartgame) still clears it until it has run.
    /// Call every tick; General.FixHudFlashing turns it off.
    /// The game rules entity is looked up again every few seconds: a cached proxy could outlive its entity (crash
    /// recovery, hot reload, map change before OnMapStart ran), which silently disabled the fix until a restart.
    /// </summary>
    internal static void KeepPanelSteady()
    {
        if (!(JailbreakPlugin.Instance?.Config.General.FixHudFlashing ?? true)) return;
        try
        {
            var now = Server.CurrentTime;
            if (_rulesProxy == null || !_rulesProxy.IsValid || now >= _nextRulesLookup || now < _nextRulesLookup - 10f)
            {
                _rulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
                _nextRulesLookup = now + 5f;
            }

            var rules = _rulesProxy?.GameRules;
            if (rules == null)
            {
                _rulesProxy = null;
                return;
            }

            var steady = rules.RestartRoundTime < now;
            if (rules.GameRestart == steady) return;
            rules.GameRestart = steady;
            Utilities.SetStateChanged(_rulesProxy!, "CCSGameRulesProxy", "m_pGameRules");
        }
        catch
        {
            _rulesProxy = null;
        }
    }

    internal static void ForgetGameRules()
    {
        _rulesProxy = null;
        _nextRulesLookup = 0;
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
