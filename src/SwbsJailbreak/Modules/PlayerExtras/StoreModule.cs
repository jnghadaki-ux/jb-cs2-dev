using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>The kinds of store items that work in CS2 with stock effects only (no custom files).</summary>
public enum StoreItemType
{
    Tracer,
    Paintball,
    KillEffect,
    Trail
}

public sealed class StoreItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public StoreItemType Type { get; set; }

    /// <summary>"R,G,B", or "rainbow" for a random colour every time.</summary>
    public string Color { get; set; } = "255,255,255";

    public int Price { get; set; }
}

/// <summary>Section "Store" (SMPlugins Store library + StoreItems: BulletTracers, Paintballs, KillEffects, PlayerEffects).</summary>
public sealed class StoreConfig
{
    public bool Enabled { get; set; } = true;

    public List<StoreItem> Items { get; set; } = new()
    {
        new() { Id = "tracer_red", Name = "Red tracers", Type = StoreItemType.Tracer, Color = "255,40,40", Price = 300 },
        new() { Id = "tracer_blue", Name = "Blue tracers", Type = StoreItemType.Tracer, Color = "60,120,255", Price = 300 },
        new() { Id = "tracer_green", Name = "Green tracers", Type = StoreItemType.Tracer, Color = "60,230,90", Price = 300 },
        new() { Id = "tracer_gold", Name = "Gold tracers", Type = StoreItemType.Tracer, Color = "255,200,40", Price = 500 },
        new() { Id = "tracer_rainbow", Name = "Rainbow tracers", Type = StoreItemType.Tracer, Color = "rainbow", Price = 900 },
        new() { Id = "paint_random", Name = "Paintballs", Type = StoreItemType.Paintball, Color = "rainbow", Price = 600 },
        new() { Id = "kill_ring_red", Name = "Red kill shockwave", Type = StoreItemType.KillEffect, Color = "255,40,40", Price = 700 },
        new() { Id = "kill_ring_gold", Name = "Gold kill shockwave", Type = StoreItemType.KillEffect, Color = "255,200,40", Price = 900 },
        new() { Id = "trail_white", Name = "White trail", Type = StoreItemType.Trail, Color = "255,255,255", Price = 400 },
        new() { Id = "trail_purple", Name = "Purple trail", Type = StoreItemType.Trail, Color = "170,90,230", Price = 500 },
        new() { Id = "trail_rainbow", Name = "Rainbow trail", Type = StoreItemType.Trail, Color = "rainbow", Price = 1200 }
    };

    /// <summary>Seconds a tracer / paint splat / trail segment stays.</summary>
    public float TracerSeconds { get; set; } = 0.15f;
    public float PaintSeconds { get; set; } = 15f;
    public int PaintMaxPerPlayer { get; set; } = 12;
    public float TrailSeconds { get; set; } = 1.2f;
}

/// <summary>A player's store inventory (data/store.json, keyed by SteamID64).</summary>
public sealed class StoreInventory
{
    public List<string> Owned { get; set; } = new();
    public Dictionary<string, string> Equipped { get; set; } = new();
}

/// <summary>
/// Port of the SMPlugins Store with the item types CS2 can show without custom files: bullet tracers, paintballs, kill
/// effects (a shockwave where your victim died) and trails. The original sold items for UserPoints through the website;
/// here they are bought in game with jailbreak points (!points) from !store / !shop, kept forever, and one item per
/// type can be equipped. Custom models, hats, grenade / kill sounds need workshop content and are not included.
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

    public override void OnLoad(bool hotReload) =>
        Plugin.AddTimer(0.15f, DrawTrails, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

    public override void OnPlayerConnected(JbPlayer player)
    {
        _inv[player.Slot] = player.IsBot || player.SteamId64 == 0 ? null : Storage.Get<StoreInventory>(Ns, player.SteamId64.ToString()) ?? new StoreInventory();
        _trailLast[player.Slot] = null;
        _paint[player.Slot].Clear();
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _inv[player.Slot] = null;
        _trailLast[player.Slot] = null;
        ClearPaint(player.Slot);
    }

    public override void OnMapStart(string mapName)
    {
        foreach (var q in _paint) q.Clear();
        Array.Clear(_trailLast);
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

    private StoreItem? Find(string id) => Cfg.Items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private StoreItem? Equipped(int slot, StoreItemType type) =>
        _inv[slot] is { } inv && inv.Equipped.TryGetValue(type.ToString(), out var id) ? Find(id) : null;

    private void Save(CCSPlayerController c)
    {
        if (_inv[c.Slot] is { } inv && c.SteamID != 0) Storage.Set(Ns, c.SteamID.ToString(), inv);
    }

    // ================================================================= menus

    [ConsoleCommand("css_store", "sm_store - spend jailbreak points on tracers, paintballs, kill effects and trails")]
    [ConsoleCommand("css_shop", "sm_shop - spend jailbreak points on tracers, paintballs, kill effects and trails")]
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

    // ================================================================= effects

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
            Fx.Beam(start, impact, ColorOf(tracer.Color, Random.Shared), 1f, Math.Max(0.05f, Cfg.TracerSeconds));
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
        var b1 = Fx.Beam(new Vector(impact.X - half, impact.Y, impact.Z), new Vector(impact.X + half, impact.Y, impact.Z), color, 4f, Cfg.PaintSeconds);
        var b2 = Fx.Beam(new Vector(impact.X, impact.Y - half, impact.Z), new Vector(impact.X, impact.Y + half, impact.Z), color, 4f, Cfg.PaintSeconds);
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

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        _trailLast[victim.Slot] = null;
        if (attacker == null || ReferenceEquals(attacker, victim)) return;
        if (Equipped(attacker.Slot, StoreItemType.KillEffect) is not { } fx) return;
        var o = victim.Controller?.Origin();
        if (o == null) return;
        Fx.PulseAt(new Vector(o.X, o.Y, o.Z + 8f), ColorOf(fx.Color, Random.Shared), 10f, 220f, 0.6f, 4f);
    }

    private void DrawTrails()
    {
        foreach (var c in Players.Alive())
        {
            if (c.IsBot) continue;
            if (Equipped(c.Slot, StoreItemType.Trail) is not { } trail)
            {
                _trailLast[c.Slot] = null;
                continue;
            }

            var o = c.Origin();
            if (o == null) continue;
            var here = new Vector(o.X, o.Y, o.Z + 6f);
            var last = _trailLast[c.Slot];
            _trailLast[c.Slot] = here;
            if (last == null || VectorUtil.Distance(last, here) < 12f || VectorUtil.Distance(last, here) > 300f) continue;
            Fx.Beam(last, here, ColorOf(trail.Color, Random.Shared), 3f, Math.Max(0.2f, Cfg.TrailSeconds));
        }
    }
}
