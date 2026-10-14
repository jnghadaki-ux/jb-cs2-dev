using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Store item kinds, in menu order. One item of each kind can be equipped.</summary>
public enum StoreItemType
{
    Tracer,
    Paintball,
    KillEffect,
    Trail,
    Aura,
    Sparkles,
    Halo,
    ChatColor,
    ChatTitle,
    Transparency,
    BodySize,
    KillSound,
    GrenadeSound,
    Hat
}

public sealed class StoreItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public StoreItemType Type { get; set; }

    /// <summary>"R,G,B", or "rainbow" for a random colour every time (tracers, effects, hat tint).</summary>
    public string Color { get; set; } = "255,255,255";

    /// <summary>
    /// Per type: KillEffect "ring" / "fire" / "explosion"; ChatColor a chat colour tag ("{red}"); BodySize a scale
    /// ("0.85"); Transparency an alpha 0-255; KillSound a sound event; GrenadeSound "bounce event|explode event"; Hat a
    /// .vmdl path from your workshop pack.
    /// </summary>
    public string Data { get; set; } = "";

    public int Price { get; set; }
}

/// <summary>
/// Section "Store" (SMPlugins Store library + StoreItems, and the old donator items Colored Chat, Titles, Body
/// Transparency and Body Size, now sold here for jailbreak points instead of being donator perks).
/// </summary>
public sealed class StoreConfig
{
    public bool Enabled { get; set; } = true;

    public List<StoreItem> Items { get; set; } = DefaultItems();

    /// <summary>Bumped when new default items ship; older configs get the new defaults appended once.</summary>
    public int DefaultsVersion { get; set; }

    /// <summary>Seconds a tracer / paint splat / trail segment stays.</summary>
    public float TracerSeconds { get; set; } = 0.15f;
    public float PaintSeconds { get; set; } = 15f;
    public int PaintMaxPerPlayer { get; set; } = 12;
    public float TrailSeconds { get; set; } = 1.2f;

    /// <summary>
    /// See-through body and body size change how easy a player is to see and hit (CS2 cannot scale hitboxes). Off: they
    /// only apply in warmup, on freedays and in freeday LRs. On: always.
    /// </summary>
    public bool BodyItemsAnytime { get; set; } = false;

    public int TitleMaxLength { get; set; } = 16;

    /// <summary>Kill and grenade sounds are heard by players this close (and always by the killer and victim).</summary>
    public float SoundRange { get; set; } = 1200f;

    /// <summary>Player model attachment hats are parented to.</summary>
    public string HatAttachment { get; set; } = "facemask";

    public const int CurrentDefaultsVersion = 2;

    public static List<StoreItem> DefaultItems() => new()
    {
        new() { Id = "tracer_red", Name = "Red tracers", Type = StoreItemType.Tracer, Color = "255,40,40", Price = 300 },
        new() { Id = "tracer_blue", Name = "Blue tracers", Type = StoreItemType.Tracer, Color = "60,120,255", Price = 300 },
        new() { Id = "tracer_green", Name = "Green tracers", Type = StoreItemType.Tracer, Color = "60,230,90", Price = 300 },
        new() { Id = "tracer_gold", Name = "Gold tracers", Type = StoreItemType.Tracer, Color = "255,200,40", Price = 500 },
        new() { Id = "tracer_rainbow", Name = "Rainbow tracers", Type = StoreItemType.Tracer, Color = "rainbow", Price = 900 },
        new() { Id = "paint_random", Name = "Paintballs", Type = StoreItemType.Paintball, Color = "rainbow", Price = 600 },
        new() { Id = "kill_ring_red", Name = "Red kill shockwave", Type = StoreItemType.KillEffect, Color = "255,40,40", Data = "ring", Price = 700 },
        new() { Id = "kill_ring_gold", Name = "Gold kill shockwave", Type = StoreItemType.KillEffect, Color = "255,200,40", Data = "ring", Price = 900 },
        new() { Id = "trail_white", Name = "White trail", Type = StoreItemType.Trail, Color = "255,255,255", Price = 400 },
        new() { Id = "trail_purple", Name = "Purple trail", Type = StoreItemType.Trail, Color = "170,90,230", Price = 500 },
        new() { Id = "trail_rainbow", Name = "Rainbow trail", Type = StoreItemType.Trail, Color = "rainbow", Price = 1200 },
        // ---- version 2: the rest of SMPlugins StoreItems and the old donator items
        new() { Id = "kill_fire", Name = "Burning kill", Type = StoreItemType.KillEffect, Data = "fire", Price = 900 },
        new() { Id = "kill_explosion", Name = "Explosive kill", Type = StoreItemType.KillEffect, Data = "explosion", Price = 1100 },
        new() { Id = "aura_blue", Name = "Blue aura", Type = StoreItemType.Aura, Color = "60,140,255", Price = 600 },
        new() { Id = "aura_gold", Name = "Gold aura", Type = StoreItemType.Aura, Color = "255,200,40", Price = 800 },
        new() { Id = "sparkles_white", Name = "White sparkles", Type = StoreItemType.Sparkles, Color = "255,255,255", Price = 600 },
        new() { Id = "sparkles_rainbow", Name = "Rainbow sparkles", Type = StoreItemType.Sparkles, Color = "rainbow", Price = 1000 },
        new() { Id = "halo_red", Name = "Red swirl", Type = StoreItemType.Halo, Color = "255,50,50", Price = 700 },
        new() { Id = "halo_rainbow", Name = "Rainbow swirl", Type = StoreItemType.Halo, Color = "rainbow", Price = 1100 },
        new() { Id = "chat_olive", Name = "Olive chat", Type = StoreItemType.ChatColor, Data = "{olive}", Price = 400 },
        new() { Id = "chat_green", Name = "Green chat", Type = StoreItemType.ChatColor, Data = "{green}", Price = 400 },
        new() { Id = "chat_lightgreen", Name = "Light green chat", Type = StoreItemType.ChatColor, Data = "{lime}", Price = 400 },
        new() { Id = "chat_red", Name = "Red chat", Type = StoreItemType.ChatColor, Data = "{red}", Price = 400 },
        new() { Id = "chat_lightred", Name = "Light red chat", Type = StoreItemType.ChatColor, Data = "{lightred}", Price = 400 },
        new() { Id = "chat_blue", Name = "Blue chat", Type = StoreItemType.ChatColor, Data = "{blue}", Price = 400 },
        new() { Id = "chat_yellow", Name = "Yellow chat", Type = StoreItemType.ChatColor, Data = "{yellow}", Price = 400 },
        new() { Id = "chat_purple", Name = "Purple chat", Type = StoreItemType.ChatColor, Data = "{purple}", Price = 400 },
        new() { Id = "chat_gold", Name = "Gold chat", Type = StoreItemType.ChatColor, Data = "{gold}", Price = 600 },
        new() { Id = "chat_title", Name = "Chat title (!title)", Type = StoreItemType.ChatTitle, Color = "{gold}", Price = 1500 },
        new() { Id = "body_transparent", Name = "See-through body", Type = StoreItemType.Transparency, Data = "165", Price = 1500 },
        new() { Id = "body_skinny", Name = "Skinny", Type = StoreItemType.BodySize, Data = "0.85", Price = 1200 },
        new() { Id = "body_fatso", Name = "Fatso", Type = StoreItemType.BodySize, Data = "1.15", Price = 1200 }
    };
}

/// <summary>A player's store inventory (data/store.json, keyed by SteamID64).</summary>
public sealed class StoreInventory
{
    public List<string> Owned { get; set; } = new();
    public Dictionary<string, string> Equipped { get; set; } = new();

    /// <summary>The text after !title (needs the Chat title item).</summary>
    public string Title { get; set; } = "";

    /// <summary>Whose effects this player sees: 0 everyone's, 1 only their team's, 2 only their own.</summary>
    public int OthersView { get; set; }

    /// <summary>Hide this player's own effects from themselves.</summary>
    public bool HideOwn { get; set; }
}

/// <summary>
/// Port of the SMPlugins Store and StoreItems, plus the old donator items, all bought with jailbreak points (!points)
/// from !store / !shop and kept forever (one item per kind equipped):
/// bullet tracers, paintballs, kill effects (shockwave / fire / explosion; the original's ragdoll "force" cannot be done
/// on CS2's client-side ragdolls), trails, auras, sparkles, swirls, chat colours, a chat title (!title), see-through body
/// and body size (fair-play limited, see <see cref="StoreConfig.BodyItemsAnytime"/>), and - once your workshop pack has
/// the files - kill sounds, grenade sounds and hats (EquipmentColors = the hat tint). The original's per-player
/// visibility settings are in !store > Settings. Player effects and hats are off during wardays and non-freeday LRs, so
/// they never give a hiding player away.
/// </summary>
public sealed class StoreModule : JbModule
{
    private const string Ns = "store";

    private StoreConfig Cfg => Section<StoreConfig>("Store");

    public override string Name => "Store";
    public override int Order => 48; // after Points (40)
    public override bool Enabled => Cfg.Enabled;

    private readonly StoreInventory?[] _inv = new StoreInventory?[Players.MaxSlots];
    private readonly double[] _nextTracer = new double[Players.MaxSlots];
    private readonly Vector?[] _trailLast = new Vector?[Players.MaxSlots];
    private readonly Queue<CBeam?[]>[] _paint = Enumerable.Range(0, Players.MaxSlots).Select(_ => new Queue<CBeam?[]>()).ToArray();
    private readonly int[] _alpha = new int[Players.MaxSlots];
    private readonly float[] _scale = new float[Players.MaxSlots];
    private readonly CBaseModelEntity?[] _hat = new CBaseModelEntity?[Players.MaxSlots];
    private readonly string?[] _hatId = new string?[Players.MaxSlots];
    private int _tick;

    public override void OnLoad(bool hotReload)
    {
        MergeNewDefaults();
        Array.Fill(_alpha, 255);
        Array.Fill(_scale, 1f);
        Plugin.AddTimer(0.15f, EffectsTick, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(m =>
        {
            foreach (var hat in Cfg.Items.Where(i => i.Type == StoreItemType.Hat && i.Data.Length > 0)) m.AddResource(hat.Data);
        });
        ChatDecor.AddPrefix(Name, TitlePrefix);
        ChatDecor.AddColor(Name, ChatColor);
    }

    public override void OnUnload(bool hotReload)
    {
        ChatDecor.Remove(Name);
        for (var i = 0; i < Players.MaxSlots; i++) RemoveHat(i);
    }

    /// <summary>Configs written before the new items existed get them once (an item an admin removed later stays removed).</summary>
    private void MergeNewDefaults()
    {
        if (Cfg.DefaultsVersion >= StoreConfig.CurrentDefaultsVersion) return;
        Plugin.Config.Update<StoreConfig>("Store", c =>
        {
            c.Items = Merge(c.Items, StoreConfig.DefaultItems());
            c.DefaultsVersion = StoreConfig.CurrentDefaultsVersion;
        });
    }

    /// <summary>Engine-free (unit-tested): the configured items plus the defaults whose id is not there yet.</summary>
    internal static List<StoreItem> Merge(List<StoreItem>? configured, List<StoreItem> defaults)
    {
        var list = configured?.ToList() ?? new List<StoreItem>();
        foreach (var d in defaults)
            if (!list.Any(i => i.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase)))
                list.Add(d);
        return list;
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        _inv[player.Slot] = player.IsBot || player.SteamId64 == 0 ? null : Storage.Get<StoreInventory>(Ns, player.SteamId64.ToString()) ?? new StoreInventory();
        ResetSlot(player.Slot);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _inv[player.Slot] = null;
        ResetSlot(player.Slot);
    }

    public override void OnPlayerSpawn(JbPlayer player)
    {
        // A new pawn starts opaque and full size: re-apply on the next effects tick.
        _alpha[player.Slot] = 255;
        _scale[player.Slot] = 1f;
        _trailLast[player.Slot] = null;
        RemoveHat(player.Slot);
    }

    public override void OnMapStart(string mapName)
    {
        foreach (var q in _paint) q.Clear();
        Array.Clear(_trailLast);
        Array.Fill(_alpha, 255);
        Array.Fill(_scale, 1f);
        Array.Clear(_hat);
        Array.Clear(_hatId);
    }

    private void ResetSlot(int slot)
    {
        _trailLast[slot] = null;
        _alpha[slot] = 255;
        _scale[slot] = 1f;
        ClearPaint(slot);
        RemoveHat(slot);
    }

    // ================================================================= rules (engine-free, unit-tested)

    internal enum BuyResult { Ok, AlreadyOwned, NotEnoughPoints, UnknownItem }

    internal static BuyResult CanBuy(StoreItem? item, StoreInventory inv, int balance)
    {
        if (item == null) return BuyResult.UnknownItem;
        if (inv.Owned.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) return BuyResult.AlreadyOwned;
        return balance >= Math.Max(0, item.Price) ? BuyResult.Ok : BuyResult.NotEnoughPoints;
    }

    /// <summary>Equip (or unequip with the same item) and return whether it is now equipped.</summary>
    internal static bool ToggleEquip(StoreInventory inv, StoreItem item)
    {
        var type = item.Type.ToString();
        if (inv.Equipped.TryGetValue(type, out var cur) && cur.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
        {
            inv.Equipped.Remove(type);
            return false;
        }

        inv.Equipped[type] = item.Id;
        return true;
    }

    internal static Color ColorOf(string spec, Random rng)
    {
        if (spec.Equals("rainbow", StringComparison.OrdinalIgnoreCase))
        {
            var hue = rng.NextDouble() * 360;
            return FromHue(hue);
        }

        var parts = spec.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var g) && int.TryParse(parts[2], out var b))
            return Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        return Color.White;
    }

    private static Color FromHue(double hue)
    {
        var x = 1 - Math.Abs(hue / 60 % 2 - 1);
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x)
        };
        return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    /// <summary>
    /// The SMPlugins visibility settings: does a viewer see an effect? <paramref name="othersView"/> 0 = everyone's,
    /// 1 = only their team's, 2 = only their own; <paramref name="hideOwn"/> hides their own.
    /// </summary>
    internal static bool ShouldShow(int othersView, bool hideOwn, bool isOwner, bool sameTeam)
    {
        if (isOwner) return !hideOwn;
        return othersView switch
        {
            1 => sameTeam,
            2 => false,
            _ => true
        };
    }

    /// <summary>A !title text: colour tags and braces removed, trimmed, at most <paramref name="max"/> characters; empty when nothing is left.</summary>
    internal static string CleanTitle(string? text, int max)
    {
        var t = Colors.Strip(text ?? "").Replace("{", "").Replace("}", "").Replace("\n", " ").Replace("\r", " ").Trim();
        t = new string(t.Where(ch => !char.IsControl(ch)).ToArray());
        if (t.Length > Math.Max(1, max)) t = t[..Math.Max(1, max)].TrimEnd();
        return t;
    }

    /// <summary>Body items (see-through, size) are fair-play limited unless <see cref="StoreConfig.BodyItemsAnytime"/>.</summary>
    internal static bool BodyItemsAllowed(bool anytime, bool warmup, bool freedayDay, bool freedayLr) => anytime || warmup || freedayDay || freedayLr;

    private StoreItem? Find(string id) => Cfg.Items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private StoreItem? Equipped(int slot, StoreItemType type) =>
        _inv[slot] is { } inv && inv.Equipped.TryGetValue(type.ToString(), out var id) ? Find(id) : null;

    private void Save(CCSPlayerController c)
    {
        if (_inv[c.Slot] is { } inv && c.SteamID != 0) Storage.Set(Ns, c.SteamID.ToString(), inv);
    }

    /// <summary>Who may see an effect of this owner, or null when nobody has hidden anything (no per-viewer filtering needed).</summary>
    private Func<CCSPlayerController, bool>? ViewFilter(CCSPlayerController owner, bool hideFromOwner = false)
    {
        var anyone = hideFromOwner || _inv.Any(i => i != null && (i.OthersView != 0 || i.HideOwn));
        if (!anyone) return null;
        var ownerSlot = owner.Slot;
        var ownerTeam = owner.Team;
        return viewer =>
        {
            var isOwner = viewer.Slot == ownerSlot;
            if (isOwner && hideFromOwner) return false;
            var inv = _inv[viewer.Slot];
            return inv == null || ShouldShow(inv.OthersView, inv.HideOwn, isOwner, viewer.Team == ownerTeam);
        };
    }

    private static void Restrict(CEntityInstance? e, Func<CCSPlayerController, bool>? filter)
    {
        if (e != null && filter != null) Visibility.OnlyFor(e, filter);
    }

    /// <summary>Player effects and hats are off during wardays and in LRs other than freedays (they would give hiding players away).</summary>
    private static bool EffectsAllowed(CCSPlayerController c)
    {
        if (Jb.Days.CurrentType == DayType.Warday) return false;
        var p = c.Jb();
        if (p == null) return false;
        var prisoner = c.Team == CsTeam.Terrorist ? p : Jb.LastRequests.GetOpponent(p);
        if (prisoner == null || !Jb.LastRequests.HasStartedLastRequest(prisoner)) return true;
        return (Jb.LastRequests.GetFlags(prisoner) & LrFlags.Freeday) != 0;
    }

    private bool BodyAllowed(CCSPlayerController c)
    {
        var p = c.Jb();
        var freedayLr = p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;
        return BodyItemsAllowed(Cfg.BodyItemsAnytime, Round.IsWarmup, Jb.Days.CurrentType == DayType.Freeday, freedayLr)
               && (Cfg.BodyItemsAnytime || EffectsAllowed(c) || freedayLr);
    }

    // ================================================================= menus

    [ConsoleCommand("css_store", "sm_store - spend jailbreak points on effects, chat colours, titles and more")]
    [ConsoleCommand("css_shop", "sm_shop - spend jailbreak points on effects, chat colours, titles and more")]
    public void CmdStore(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || caller.IsBot) return;
        if (Jb.Points is NullServices) { Chat.Tell(caller, "store.no_points_system"); return; }
        if (_inv[caller.Slot] == null) { Chat.Tell(caller, "store.not_ready"); return; }
        ShowMain(caller);
    }

    private void ShowMain(CCSPlayerController c) => BuildMain(c)?.Open(c);

    private JbMenu? BuildMain(CCSPlayerController c)
    {
        var p = c.Jb();
        if (p == null) return null;
        var m = new JbMenu(Lang.T("store.title")) { Subtitle = Lang.T("store.balance", Jb.Points.GetPoints(p)) };
        foreach (var type in Enum.GetValues<StoreItemType>())
        {
            var t = type;
            if (Cfg.Items.Any(i => i.Type == t)) m.Add(Lang.T("store.cat." + t.ToString().ToLowerInvariant()), x => ShowCategory(x, t));
        }

        m.Add(Lang.T("store.settings"), ShowSettings);
        return m;
    }

    private void ShowCategory(CCSPlayerController c, StoreItemType type)
    {
        var p = c.Jb();
        var inv = _inv[c.Slot];
        if (p == null || inv == null) return;
        var m = new JbMenu(Lang.T("store.cat." + type.ToString().ToLowerInvariant()))
        {
            Subtitle = Lang.T("store.balance", Jb.Points.GetPoints(p)),
            Parent = BuildMain(c) // 7 / R goes back to the categories
        };

        foreach (var item in Cfg.Items.Where(i => i.Type == type))
        {
            var it = item;
            var owned = inv.Owned.Contains(it.Id, StringComparer.OrdinalIgnoreCase);
            var equipped = Equipped(c.Slot, type)?.Id.Equals(it.Id, StringComparison.OrdinalIgnoreCase) == true;
            var label = equipped ? Lang.T("store.item.equipped", Hud.Escape(it.Name))
                : owned ? Lang.T("store.item.owned", Hud.Escape(it.Name))
                : Lang.T("store.item.price", Hud.Escape(it.Name), it.Price);
            m.Add(label, x => OnItem(x, it));
        }

        m.Open(c);
    }

    private void OnItem(CCSPlayerController c, StoreItem item)
    {
        var p = c.Jb();
        var inv = _inv[c.Slot];
        if (p == null || inv == null) return;

        if (!inv.Owned.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
        {
            var balance = Jb.Points.GetPoints(p);
            switch (CanBuy(item, inv, balance))
            {
                case BuyResult.NotEnoughPoints:
                    Chat.Tell(c, "store.not_enough", item.Price, balance);
                    ShowCategory(c, item.Type);
                    return;
                case BuyResult.Ok:
                    Jb.Points.AddPoints(p, -item.Price, "store");
                    inv.Owned.Add(item.Id);
                    inv.Equipped[item.Type.ToString()] = item.Id;
                    Save(c);
                    Chat.Tell(c, "store.bought", item.Name, item.Price);
                    Jb.Log.LogEvent($"{c.PlayerName} bought {item.Name} for {item.Price} points", p, null, LogType.Item);
                    if (item.Type == StoreItemType.ChatTitle) Chat.Tell(c, "store.title.how", Cfg.TitleMaxLength);
                    if (item.Type is StoreItemType.Transparency or StoreItemType.BodySize && !Cfg.BodyItemsAnytime) Chat.Tell(c, "store.body.when");
                    break;
            }
        }
        else
        {
            var on = ToggleEquip(inv, item);
            Save(c);
            Chat.Tell(c, on ? "store.equipped" : "store.unequipped", item.Name);
        }

        ShowCategory(c, item.Type);
    }

    private void ShowSettings(CCSPlayerController c)
    {
        var inv = _inv[c.Slot];
        if (inv == null) return;
        var m = new JbMenu(Lang.T("store.settings")) { Parent = BuildMain(c) };
        m.Add(Lang.T("store.settings.others", Lang.T("store.view." + Math.Clamp(inv.OthersView, 0, 2))), x =>
        {
            inv.OthersView = (inv.OthersView + 1) % 3;
            Save(x);
            ShowSettings(x);
        });
        m.Add(Lang.T("store.settings.own", Lang.T(inv.HideOwn ? "store.view.hidden" : "store.view.shown")), x =>
        {
            inv.HideOwn = !inv.HideOwn;
            Save(x);
            ShowSettings(x);
        });
        m.Open(c);
    }

    // ================================================================= chat colour + title

    private string? ChatColor(CCSPlayerController c)
    {
        if (c.IsBot || Equipped(c.Slot, StoreItemType.ChatColor) is not { } item) return null;
        return item.Data.StartsWith('{') && item.Data.EndsWith('}') ? item.Data : null;
    }

    private string? TitlePrefix(CCSPlayerController c)
    {
        if (c.IsBot || _inv[c.Slot] is not { } inv || inv.Title.Length == 0) return null;
        if (Equipped(c.Slot, StoreItemType.ChatTitle) is not { } item) return null;
        var color = item.Color.StartsWith('{') ? item.Color : "{gold}";
        return $"{color}[{inv.Title}]{{default}} ";
    }

    [ConsoleCommand("css_title", "sm_title <text|off> - set your chat title (store item)")]
    public void CmdTitle(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || caller.IsBot || _inv[caller.Slot] is not { } inv) return;
        if (!Cfg.Items.Any(i => i.Type == StoreItemType.ChatTitle && inv.Owned.Contains(i.Id, StringComparer.OrdinalIgnoreCase)))
        {
            Chat.Tell(caller, "store.title.not_owned");
            return;
        }

        var arg = info.ArgString.Trim().Trim('"');
        if (arg.Length == 0)
        {
            Chat.Tell(caller, inv.Title.Length == 0 ? "store.title.none" : "store.title.current", inv.Title);
            Chat.Tell(caller, "store.title.how", Cfg.TitleMaxLength);
            return;
        }

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            inv.Title = "";
            Save(caller);
            Chat.Tell(caller, "store.title.removed");
            return;
        }

        var title = CleanTitle(arg, Cfg.TitleMaxLength);
        if (title.Length == 0) { Chat.Tell(caller, "store.title.how", Cfg.TitleMaxLength); return; }
        inv.Title = title;
        if (Cfg.Items.FirstOrDefault(i => i.Type == StoreItemType.ChatTitle && inv.Owned.Contains(i.Id, StringComparer.OrdinalIgnoreCase)) is { } t)
            inv.Equipped[StoreItemType.ChatTitle.ToString()] = t.Id;
        Save(caller);
        Chat.Tell(caller, "store.title.set", title);
        Jb.Log.LogEvent($"{caller.PlayerName} set their chat title to \"{title}\"", caller.Jb(), null, LogType.Any);
    }

    [ConsoleCommand("css_cleartitle", "sm_cleartitle <player> - remove a player's chat title")]
    public void CmdClearTitle(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "chat")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!cleartitle <player>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        var target = t.Players[0];
        if (_inv[target.Slot] is not { } inv) return;
        inv.Title = "";
        Save(target);
        Chat.ReplyT(caller, info, "store.title.cleared", target.PlayerName);
        Chat.Tell(target, "store.title.cleared_you");
        Jb.Log.LogEvent($"{(caller?.PlayerName ?? "Console")} cleared {target.PlayerName}'s chat title", caller?.Jb(), target.Jb(), LogType.Admin);
    }

    // ================================================================= tracers / paintballs

    [GameEventHandler]
    public HookResult OnBulletImpact(EventBulletImpact ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.IsBot) return HookResult.Continue;
        var now = Server.EngineTime;
        if (now < _nextTracer[c.Slot]) return HookResult.Continue;
        _nextTracer[c.Slot] = now + 0.03; // one effect per shot (penetration fires several impacts)
        var impact = new Vector(ev.X, ev.Y, ev.Z);

        if (Equipped(c.Slot, StoreItemType.Tracer) is { } tracer)
        {
            var eye = c.EyePosition();
            var start = new Vector(eye.X, eye.Y, eye.Z - 4f);
            Restrict(Fx.Beam(start, impact, ColorOf(tracer.Color, Random.Shared), 1f, Math.Max(0.05f, Cfg.TracerSeconds)), ViewFilter(c));
        }

        if (Equipped(c.Slot, StoreItemType.Paintball) is { } paint) Paint(c, impact, ColorOf(paint.Color, Random.Shared));
        return HookResult.Continue;
    }

    private void Paint(CCSPlayerController c, Vector impact, Color color)
    {
        foreach (var other in Players.Alive())
        {
            var o = other.Origin();
            if (o != null && VectorUtil.Distance2D(o, impact) <= 24f && impact.Z >= o.Z - 4f && impact.Z <= o.Z + 76f) return; // not on players
        }

        const float half = 3f;
        var filter = ViewFilter(c);
        var b1 = Fx.Beam(new Vector(impact.X - half, impact.Y, impact.Z), new Vector(impact.X + half, impact.Y, impact.Z), color, 4f, Cfg.PaintSeconds);
        var b2 = Fx.Beam(new Vector(impact.X, impact.Y - half, impact.Z), new Vector(impact.X, impact.Y + half, impact.Z), color, 4f, Cfg.PaintSeconds);
        Restrict(b1, filter);
        Restrict(b2, filter);
        var q = _paint[c.Slot];
        q.Enqueue(new[] { b1, b2 });
        while (q.Count > Math.Max(1, Cfg.PaintMaxPerPlayer))
            foreach (var old in q.Dequeue())
                Fx.SafeRemove(old);
    }

    private void ClearPaint(int slot)
    {
        var q = _paint[slot];
        while (q.Count > 0)
            foreach (var b in q.Dequeue())
                Fx.SafeRemove(b);
    }

    // ================================================================= kill effects + kill sounds

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        _trailLast[victim.Slot] = null;
        RemoveHat(victim.Slot);
        if (attacker == null || ReferenceEquals(attacker, victim) || attacker.Controller is not { IsValid: true } ac) return;
        var o = victim.Controller?.Origin();
        if (o == null) return;

        if (Equipped(attacker.Slot, StoreItemType.KillEffect) is { } fx)
        {
            var filter = ViewFilter(ac);
            switch (fx.Data.Trim().ToLowerInvariant())
            {
                case "fire":
                    Restrict(Fx.Particle("particles/burning_fx/burning_character_b.vpcf", new Vector(o.X, o.Y, o.Z + 10f), 3f), filter);
                    break;
                case "explosion":
                    Restrict(Fx.Particle("particles/explosions_fx/explosion_basic.vpcf", new Vector(o.X, o.Y, o.Z + 20f), 2f), filter);
                    break;
                default:
                    Fx.PulseAt(new Vector(o.X, o.Y, o.Z + 8f), ColorOf(fx.Color, Random.Shared), 10f, 220f, 0.6f, 4f, filter);
                    break;
            }
        }

        if (Equipped(attacker.Slot, StoreItemType.KillSound) is { Data.Length: > 0 } ks)
            PlayNear(ac, o, ks.Data, victim.Slot);
    }

    /// <summary>A store sound for the owner, the other player involved and everyone within SoundRange who shows others' effects.</summary>
    private void PlayNear(CCSPlayerController owner, Vector at, string sound, int alsoSlot = -1)
    {
        var range = Math.Max(100f, Cfg.SoundRange);
        foreach (var c in Players.Humans())
        {
            var near = c.Slot == owner.Slot || c.Slot == alsoSlot || (c.Origin() is { } co && VectorUtil.Distance(co, at) <= range);
            if (!near) continue;
            var inv = _inv[c.Slot];
            if (inv != null && !ShouldShow(inv.OthersView, inv.HideOwn, c.Slot == owner.Slot, c.Team == owner.Team)) continue;
            Sound.Play(c, sound, 0.8f);
        }
    }

    // ================================================================= grenade sounds

    private (string Bounce, string Explode) GrenadeSounds(CCSPlayerController c)
    {
        if (Equipped(c.Slot, StoreItemType.GrenadeSound) is not { } item) return ("", "");
        var parts = item.Data.Split('|');
        return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
    }

    [GameEventHandler]
    public HookResult OnGrenadeBounce(EventGrenadeBounce ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.IsBot) return HookResult.Continue;
        var (bounce, _) = GrenadeSounds(c);
        if (bounce.Length == 0) return HookResult.Continue;
        // The event has no position: use the thrower's grenade in flight.
        var pawn = c.PawnOrNull();
        foreach (var name in new[] { "hegrenade_projectile", "flashbang_projectile", "smokegrenade_projectile", "decoy_projectile", "molotov_projectile" })
            foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
                if (e.IsValid && pawn != null && e.OwnerEntity.Raw == pawn.EntityHandle.Raw && e.AbsOrigin != null)
                {
                    PlayNear(c, e.AbsOrigin, bounce);
                    return HookResult.Continue;
                }

        return HookResult.Continue;
    }

    private void GrenadeExploded(CCSPlayerController? c, float x, float y, float z)
    {
        if (c == null || !c.IsValid || c.IsBot) return;
        var (_, explode) = GrenadeSounds(c);
        if (explode.Length > 0) PlayNear(c, new Vector(x, y, z), explode);
    }

    [GameEventHandler]
    public HookResult OnHeDetonate(EventHegrenadeDetonate ev, GameEventInfo info)
    {
        GrenadeExploded(ev.Userid, ev.X, ev.Y, ev.Z);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnFlashDetonate(EventFlashbangDetonate ev, GameEventInfo info)
    {
        GrenadeExploded(ev.Userid, ev.X, ev.Y, ev.Z);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokeDetonate(EventSmokegrenadeDetonate ev, GameEventInfo info)
    {
        GrenadeExploded(ev.Userid, ev.X, ev.Y, ev.Z);
        return HookResult.Continue;
    }

    // ================================================================= continuous effects (0.15 s tick)

    private void EffectsTick()
    {
        _tick++;
        foreach (var c in Players.Alive())
        {
            if (c.IsBot || _inv[c.Slot] == null) continue;
            var o = c.Origin();
            if (o == null) continue;
            var allowed = EffectsAllowed(c);

            if (allowed) Trail(c, o);
            else _trailLast[c.Slot] = null;
            if (allowed && _tick % 2 == 0 && Equipped(c.Slot, StoreItemType.Sparkles) is { } sp) Sparkle(c, o, sp);
            if (allowed && _tick % 4 == 0 && Equipped(c.Slot, StoreItemType.Halo) is { } halo) Swirl(c, o, halo);
            if (allowed && _tick % 7 == 0 && Equipped(c.Slot, StoreItemType.Aura) is { } aura)
                Fx.PulseAt(new Vector(o.X, o.Y, o.Z + 4f), ColorOf(aura.Color, Random.Shared), 22f, 46f, 0.9f, 2f, ViewFilter(c), 12);
            if (_tick % 7 == 0)
            {
                ApplyBody(c);
                UpdateHat(c, allowed);
            }
        }
    }

    private void Trail(CCSPlayerController c, Vector o)
    {
        if (Equipped(c.Slot, StoreItemType.Trail) is not { } trail)
        {
            _trailLast[c.Slot] = null;
            return;
        }

        var here = new Vector(o.X, o.Y, o.Z + 6f);
        var last = _trailLast[c.Slot];
        _trailLast[c.Slot] = here;
        if (last == null || VectorUtil.Distance(last, here) < 12f || VectorUtil.Distance(last, here) > 300f) return;
        Restrict(Fx.Beam(last, here, ColorOf(trail.Color, Random.Shared), 3f, Math.Max(0.2f, Cfg.TrailSeconds)), ViewFilter(c));
    }

    private void Sparkle(CCSPlayerController c, Vector o, StoreItem item)
    {
        var filter = ViewFilter(c);
        for (var i = 0; i < 2; i++)
        {
            var p = new Vector(o.X + Random.Shared.Next(-14, 15), o.Y + Random.Shared.Next(-14, 15), o.Z + Random.Shared.Next(8, 68));
            Restrict(Fx.Beam(p, new Vector(p.X, p.Y, p.Z + 5f), ColorOf(item.Color, Random.Shared), 1.5f, 0.3f), filter);
        }
    }

    /// <summary>A ring that rises from the feet to the head (SMPlugins "kill swirl" / rings).</summary>
    private void Swirl(CCSPlayerController c, Vector o, StoreItem item)
    {
        var start = new Vector(o.X, o.Y, o.Z + 4f);
        var ring = Fx.Ring(start, 20f, ColorOf(item.Color, Random.Shared), 2f, 0.7f, 10);
        var filter = ViewFilter(c);
        foreach (var b in ring) Restrict(b, filter);
        var slot = c.Slot;
        for (var s = 1; s <= 4; s++)
        {
            var step = s;
            Plugin.AddTimer(0.13f * step, () =>
            {
                if (!ring.All(b => b.IsValid)) return;
                var now = Players.Get(slot).Controller?.Origin() ?? start;
                Fx.MoveRing(ring, new Vector(now.X, now.Y, now.Z + 4f + 16f * step), 20f - 2f * step);
            });
        }
    }

    // ================================================================= body items (see-through, size)

    private void ApplyBody(CCSPlayerController c)
    {
        var allowed = BodyAllowed(c);
        var alpha = 255;
        if (allowed && Equipped(c.Slot, StoreItemType.Transparency) is { } t && int.TryParse(t.Data, out var a)) alpha = Math.Clamp(a, 60, 255);
        if (alpha != _alpha[c.Slot])
        {
            c.SetAlpha(alpha);
            _alpha[c.Slot] = alpha;
        }

        var scale = 1f;
        if (allowed && Equipped(c.Slot, StoreItemType.BodySize) is { } bs &&
            float.TryParse(bs.Data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
            scale = Math.Clamp(f, 0.6f, 1.4f);
        if (Math.Abs(scale - _scale[c.Slot]) > 0.001f && SetScale(c, scale)) _scale[c.Slot] = scale;
    }

    /// <summary>Visual size only: CS2 does not scale the hitbox or the collision hull.</summary>
    private static bool SetScale(CCSPlayerController c, float scale)
    {
        var pawn = c.PawnOrNull();
        var node = pawn?.CBodyComponent?.SceneNode;
        if (pawn == null || node == null) return false;
        try
        {
            node.Scale = scale;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================= hats (workshop models)

    private void UpdateHat(CCSPlayerController c, bool allowed)
    {
        var item = allowed ? Equipped(c.Slot, StoreItemType.Hat) : null;
        if (item == null || item.Data.Length == 0)
        {
            RemoveHat(c.Slot);
            return;
        }

        if (_hat[c.Slot] is { IsValid: true } && _hatId[c.Slot] == item.Id) return;
        RemoveHat(c.Slot);
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        try
        {
            var hat = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (hat == null) return;
            hat.SetModel(item.Data);
            hat.Spawnflags = 256; // no collision
            if (!item.Color.Equals("255,255,255", StringComparison.Ordinal)) hat.Render = ColorOf(item.Color, Random.Shared);
            hat.DispatchSpawn();
            hat.Teleport(c.EyePosition(), new QAngle(0, pawn.EyeAngles.Y, 0), new Vector(0, 0, 0));
            hat.AcceptInput("SetParent", pawn, hat, "!activator");
            if (Cfg.HatAttachment.Length > 0) hat.AcceptInput("SetParentAttachment", null, null, Cfg.HatAttachment);
            // Not drawn for the wearer (it would sit in front of their eyes) and honouring everyone's settings.
            Visibility.OnlyFor(hat, ViewFilter(c, true)!);
            _hat[c.Slot] = hat;
            _hatId[c.Slot] = item.Id;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Store] hat {Model} could not be attached", item.Data);
        }
    }

    private void RemoveHat(int slot)
    {
        Fx.SafeRemove(_hat[slot]);
        _hat[slot] = null;
        _hatId[slot] = null;
    }
}
