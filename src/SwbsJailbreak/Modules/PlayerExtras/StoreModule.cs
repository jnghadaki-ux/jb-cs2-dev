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
    Hat,
    Face,
    Back,
    PlayerModel
}

public sealed class StoreItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public StoreItemType Type { get; set; }

    /// <summary>
    /// "R,G,B", or "rainbow" for a random colour every time (tracers, effects, hat tint). Several separated by "|" pick
    /// one at random each time (e.g. the classic paintballs "0,128,255|0,220,0|255,0,170").
    /// </summary>
    public string Color { get; set; } = "255,255,255";

    /// <summary>
    /// Per type: KillEffect "ring" / "fire" / "explosion"; ChatColor a chat colour tag ("{red}"); BodySize a scale
    /// ("0.85"); Transparency an alpha 0-255; KillSound a sound event; GrenadeSound "bounce event|explode event"; Hat,
    /// Face and Back a .vmdl path from your workshop pack (Face / Back: store pack 2, see WorkshopPack.Store2Equipment);
    /// PlayerModel a player .vmdl path (the store pack, see WorkshopPack.StoreModels).
    /// </summary>
    public string Data { get; set; } = "";

    public int Price { get; set; }

    /// <summary>
    /// Player models only: who may wear it. "ct" = guards only (a CT agent on a prisoner would pass as a guard),
    /// "t" = prisoners only, empty = the PlayerModelsForGuards rule.
    /// </summary>
    public string Team { get; set; } = "";
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

    /// <summary>
    /// Player model attachment hats are parented to. clip_limit sits on the head bone of every CS2 agent (and of the
    /// SWBS models rebuilt on the agent skeleton), so the hat follows the head.
    /// </summary>
    public string HatAttachment { get; set; } = "clip_limit";

    /// <summary>Hat height above the eyes when it is put on (the hat keeps that offset from the head).</summary>
    public float HatHeight { get; set; } = 7.5f;

    /// <summary>Attachment face items (glasses, masks, mustaches) follow: the head, like hats.</summary>
    public string FaceAttachment { get; set; } = "clip_limit";

    /// <summary>Face items are put this far in front of the eyes (raise it if glasses sink into a model's face).</summary>
    public float FaceForward { get; set; } = 4.5f;

    /// <summary>Face item height relative to the eyes (negative moves it down).</summary>
    public float FaceHeight { get; set; } = 0f;

    /// <summary>
    /// Attachment back items (wings, jetpack, backpack, katana) follow. c4 sits on the upper spine (spine_3) of every CS2
    /// agent and of the SWBS models rebuilt on the agent skeleton, where the bomb is carried.
    /// </summary>
    public string BackAttachment { get; set; } = "c4";

    /// <summary>Back items are put this far below the eyes (follows crouching) ...</summary>
    public float BackBelowEye { get; set; } = 17f;

    /// <summary>... and this far behind them, on the upper back.</summary>
    public float BackDepth { get; set; } = 6f;

    /// <summary>
    /// Player model items can be worn by both teams (default). False keeps them for prisoners only, so guards always
    /// look like guards. An item's own Team ("ct" / "t") still wins.
    /// </summary>
    public bool PlayerModelsForGuards { get; set; } = true;

    public const int CurrentDefaultsVersion = 9;

    public static List<StoreItem> DefaultItems() => new()
    {
        new() { Id = "tracer_red", Name = "Red tracers", Type = StoreItemType.Tracer, Color = "255,40,40", Price = 300 },
        new() { Id = "tracer_blue", Name = "Blue tracers", Type = StoreItemType.Tracer, Color = "60,120,255", Price = 300 },
        new() { Id = "tracer_green", Name = "Green tracers", Type = StoreItemType.Tracer, Color = "60,230,90", Price = 300 },
        new() { Id = "tracer_gold", Name = "Gold tracers", Type = StoreItemType.Tracer, Color = "255,200,40", Price = 500 },
        new() { Id = "tracer_rainbow", Name = "Rainbow tracers", Type = StoreItemType.Tracer, Color = "rainbow", Price = 900 },
        new() { Id = "paint_random", Name = "Rainbow paintballs", Type = StoreItemType.Paintball, Color = "rainbow", Price = PaintRainbowPrice },
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
        new() { Id = "body_transparent", Name = "Ghost skin", Type = StoreItemType.Transparency, Color = "170,210,255", Data = "165", Price = 1500 },
        new() { Id = "body_skinny", Name = "Skinny", Type = StoreItemType.BodySize, Data = "0.85", Price = 1200 },
        new() { Id = "body_fatso", Name = "Fatso", Type = StoreItemType.BodySize, Data = "1.15", Price = 1200 },
        // ---- version 3: paintball colours (the cheapest items; splat models from the workshop pack)
        new() { Id = "paint_classic", Name = "Classic paintballs", Type = StoreItemType.Paintball, Color = ClassicPaint, Price = 150 },
        new() { Id = "paint_red", Name = "Red paintballs", Type = StoreItemType.Paintball, Color = "230,25,25", Price = 100 },
        new() { Id = "paint_orange", Name = "Orange paintballs", Type = StoreItemType.Paintball, Color = "255,130,0", Price = 100 },
        new() { Id = "paint_yellow", Name = "Yellow paintballs", Type = StoreItemType.Paintball, Color = "255,225,30", Price = 100 },
        new() { Id = "paint_green", Name = "Green paintballs", Type = StoreItemType.Paintball, Color = "0,220,0", Price = 100 },
        new() { Id = "paint_blue", Name = "Blue paintballs", Type = StoreItemType.Paintball, Color = "0,128,255", Price = 100 },
        new() { Id = "paint_purple", Name = "Purple paintballs", Type = StoreItemType.Paintball, Color = "150,60,255", Price = 100 },
        new() { Id = "paint_pink", Name = "Pink paintballs", Type = StoreItemType.Paintball, Color = "255,0,170", Price = 100 },
        new() { Id = "paint_white", Name = "White paintballs", Type = StoreItemType.Paintball, Color = "245,245,245", Price = 100 },
        new() { Id = "paint_black", Name = "Black paintballs", Type = StoreItemType.Paintball, Color = "30,30,30", Price = 100 },
        // ---- version 4: SWBS model trails (pieces from the workshop pack, StoreTrails; beam trails without it)
        new() { Id = "trail_footprints", Name = "Footprints", Type = StoreItemType.Trail, Data = "footprints", Color = "35,35,35", Price = 500 },
        new() { Id = "trail_footprints_paint", Name = "Paint footprints", Type = StoreItemType.Trail, Data = "footprints", Color = ClassicPaint, Price = 600 },
        new() { Id = "trail_footprints_rainbow", Name = "Rainbow footprints", Type = StoreItemType.Trail, Data = "footprints", Color = "rainbow", Price = 800 },
        new() { Id = "trail_stars", Name = "Gold stars", Type = StoreItemType.Trail, Data = "stars", Color = "255,200,40", Price = 700 },
        new() { Id = "trail_hearts", Name = "Pink hearts", Type = StoreItemType.Trail, Data = "hearts", Color = "255,80,160", Price = 700 },
        new() { Id = "trail_skulls", Name = "Skulls", Type = StoreItemType.Trail, Data = "skulls", Color = "235,235,235", Price = 700 },
        new() { Id = "trail_lightning", Name = "Lightning bolts", Type = StoreItemType.Trail, Data = "lightning", Color = "90,170,255", Price = 700 },
        new() { Id = "trail_snowflakes", Name = "Snowflakes", Type = StoreItemType.Trail, Data = "snowflakes", Color = "200,240,255", Price = 700 },
        new() { Id = "trail_notes", Name = "Music notes", Type = StoreItemType.Trail, Data = "notes", Color = "rainbow", Price = 800 },
        new() { Id = "trail_coins", Name = "Gold coins", Type = StoreItemType.Trail, Data = "coins", Color = "255,190,40", Price = 900 },
        new() { Id = "trail_orbs", Name = "Rainbow orbs", Type = StoreItemType.Trail, Data = "orbs", Color = "rainbow", Price = 900 },
        new() { Id = "trail_bubbles", Name = "Bubbles", Type = StoreItemType.Trail, Data = "bubbles", Color = "180,230,255", Price = 900 },
        new() { Id = "trail_fire", Name = "Fire trail", Type = StoreItemType.Trail, Data = "fire", Price = 1200 },
        new() { Id = "trail_ice", Name = "Ice trail", Type = StoreItemType.Trail, Data = "ice", Price = 1200 },
        new() { Id = "trail_galaxy", Name = "Galaxy trail", Type = StoreItemType.Trail, Data = "galaxy", Price = 1500 },
        // ---- version 6: player models that exist on the current CS2 build (agents/models/..., checked against the game
        // files), wearable by both teams. More (T agents, the SWBS store pack) can be added in the config.
        Diver("a", "Diver", 3000),
        Diver("b", "Diver (red)", 3000),
        Diver("c", "Diver (green)", 3000),
        // ---- version 7: more kill effects (beam-drawn, always visible) and kill sounds. The sounds are the SWBS workshop
        // pack's own files, played straight from the .vsnd (no sound event needed); players get them with the pack.
        new() { Id = "kill_lightning", Name = "Lightning strike", Type = StoreItemType.KillEffect, Data = "lightning", Price = 1200 },
        new() { Id = "kill_souls", Name = "Rising souls", Type = StoreItemType.KillEffect, Data = "souls", Price = 1000 },
        new() { Id = "kill_ring_rainbow", Name = "Rainbow kill shockwave", Type = StoreItemType.KillEffect, Color = "rainbow", Data = "ring", Price = 1100 },
        new() { Id = "killsound_cowbell", Name = "Cowbell", Type = StoreItemType.KillSound, Data = PackSounds.Cowbell, Price = 800 },
        new() { Id = "killsound_slap", Name = "Slap", Type = StoreItemType.KillSound, Data = "sounds/swbs/misc/slap2.vsnd", Price = 700 },
        new() { Id = "killsound_zap", Name = "Teleport zap", Type = StoreItemType.KillSound, Data = "sounds/swbs/misc/teleport.vsnd", Price = 800 },
        new() { Id = "killsound_boom", Name = "Rocket boom", Type = StoreItemType.KillSound, Data = "sounds/swbs/rpg/explode1.vsnd", Price = 900 },
        new() { Id = "killsound_scream", Name = "Death scream", Type = StoreItemType.KillSound, Data = "sounds/swbs/rpg/death.vsnd", Price = 900 },
        new() { Id = "killsound_alarm", Name = "Freekill alarm", Type = StoreItemType.KillSound, Data = "sounds/swbs/jailbreak/freekill.vsnd", Price = 1000 },
        // ---- version 8: hats (workshop pack models/swbs/hats, white and tinted here; shown once WorkshopPack.HatModels is on)
        Hat("top_hat", "Top hat", "30,30,30", 1500),
        Hat("crown", "Gold crown", "255,200,40", 3000),
        Hat("party_hat", "Party hat", "rainbow", 1200),
        Hat("halo", "Halo", "255,235,140", 2500),
        Hat("beanie", "Red beanie", "200,40,40", 1000),
        Hat("cowboy_hat", "Cowboy hat", "125,82,45", 1500),
        // ---- version 9: face and back equipment (the SMPlugins Item_Equipment face and torso slots), store pack 2
        // (tools/store-pack-2), white and tinted here; shown once WorkshopPack.Store2Equipment is on
        Gear(StoreItemType.Face, "sunglasses", "Sunglasses", "25,25,25", 1200),
        Gear(StoreItemType.Face, "round_glasses", "Round glasses", "200,160,60", 1000),
        Gear(StoreItemType.Face, "bandit_mask", "Bandit mask", "20,20,20", 1500),
        Gear(StoreItemType.Face, "mustache", "Mustache", "70,40,20", 1000),
        Gear(StoreItemType.Face, "clown_nose", "Clown nose", "230,25,25", 800),
        Gear(StoreItemType.Face, "eyepatch", "Pirate eyepatch", "20,20,20", 1000),
        Gear(StoreItemType.Back, "angel_wings", "Angel wings", "255,255,255", 4000),
        Gear(StoreItemType.Back, "bat_wings", "Devil wings", "150,20,20", 4000),
        Gear(StoreItemType.Back, "jetpack", "Jetpack", "150,155,165", 3000),
        Gear(StoreItemType.Back, "backpack", "Backpack", "60,90,40", 1500),
        Gear(StoreItemType.Back, "katana", "Katana", "35,35,40", 2500)
    };

    /// <summary>Path prefix of store pack 2's face and back equipment.</summary>
    internal const string PackGearFolder = "models/swbs/store2/";

    private static StoreItem Gear(StoreItemType type, string name, string title, string color, int price)
    {
        var slot = type == StoreItemType.Face ? "face" : "back";
        return new()
        {
            Id = slot + "_" + name, Name = title, Type = type, Color = color, Price = price, Data = $"{PackGearFolder}{slot}/{name}.vmdl"
        };
    }

    /// <summary>Path prefix of the workshop pack's store hats.</summary>
    internal const string PackHatFolder = "models/swbs/hats/";

    private static StoreItem Hat(string name, string title, string color, int price) => new()
    {
        Id = "hat_" + name, Name = title, Type = StoreItemType.Hat, Color = color, Price = price, Data = PackHatFolder + name + ".vmdl"
    };

    /// <summary>Workshop pack sound files used by store items (played with the client "play" command).</summary>
    internal static class PackSounds
    {
        public const string Cowbell = "sounds/swbs/jailbreak/cowbell.vsnd";
    }

    private static StoreItem Diver(string variant, string name, int price) => new()
    {
        Id = "model_ct_diver_" + variant, Name = name, Type = StoreItemType.PlayerModel, Price = price,
        Data = $"agents/models/ctm_diver/ctm_diver_variant{variant}.vmdl"
    };

    /// <summary>
    /// Ids of the stock agent player models 0.19 added with characters/models/tm_* paths. Those paths no longer exist on
    /// CS2 (agents moved to agents/models/...), so config migration 6 removes them again.
    /// </summary>
    internal static readonly string[] RemovedAgentIds =
    {
        "model_phoenix_soldier", "model_phoenix_slingshot", "model_leet_muhlik", "model_leet_shahmat", "model_balkan_dragomir",
        "model_balkan_maximus", "model_pro_darryl", "model_pro_vypa", "model_guerrilla_solman", "model_guerrilla_elite"
    };

    /// <summary>The original SWBS splat colours (splat_blue, splat_green, splat_pink).</summary>
    public const string ClassicPaint = "0,128,255|0,220,0|255,0,170";

    internal const int PaintRainbowPrice = 200;
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
/// the files - kill sounds, grenade sounds, hats and (store pack 2) the Item_Equipment face and back slots
/// (EquipmentColors = the tint). The original's per-player
/// visibility settings are in !store > Settings. Player effects and equipment are off during wardays and non-freeday LRs, so
/// they never give a hiding player away.
/// </summary>
public sealed partial class StoreModule : JbModule
{
    private const string Ns = "store";

    private StoreConfig Cfg => Section<StoreConfig>("Store");

    public override string Name => "Store";
    public override int Order => 48; // after Points (40)
    public override bool Enabled => Cfg.Enabled;

    private readonly StoreInventory?[] _inv = new StoreInventory?[Players.MaxSlots];
    private readonly double[] _nextTracer = new double[Players.MaxSlots];
    private readonly Vector?[] _trailLast = new Vector?[Players.MaxSlots];

    /// <summary>Last living position per slot (kill effects fall back to it when the dead pawn has no origin).</summary>
    private readonly Vector?[] _lastAlive = new Vector?[Players.MaxSlots];
    private readonly Queue<CEntityInstance?[]>[] _paint = Enumerable.Range(0, Players.MaxSlots).Select(_ => new Queue<CEntityInstance?[]>()).ToArray();
    private readonly int[] _alpha = new int[Players.MaxSlots];
    private readonly float[] _scale = new float[Players.MaxSlots];
    /// <summary>Equipment worn as a model: hat (head), face and back, one entity each per player.</summary>
    private static readonly StoreItemType[] GearTypes = { StoreItemType.Hat, StoreItemType.Face, StoreItemType.Back };
    private readonly Dictionary<StoreItemType, CBaseModelEntity?[]> _gear = GearTypes.ToDictionary(t => t, _ => new CBaseModelEntity?[Players.MaxSlots]);
    private readonly Dictionary<StoreItemType, string?[]> _gearId = GearTypes.ToDictionary(t => t, _ => new string?[Players.MaxSlots]);
    private int _tick;

    public override void OnLoad(bool hotReload)
    {
        MergeNewDefaults();
        Array.Fill(_alpha, 0);
        Array.Fill(_scale, 1f);
        Plugin.AddTimer(0.15f, EffectsTick, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(m =>
        {
            foreach (var it in Cfg.Items.Where(i => i.Type is StoreItemType.Hat or StoreItemType.Face or StoreItemType.Back or StoreItemType.PlayerModel && i.Data.Length > 0 && Available(i)))
                m.AddResource(it.Data.Trim());
            foreach (var particle in Particles) m.AddResource(particle); // kill effect extras: unprecached particles never show
            if (Extras.WorkshopPackModule.PackModelsOn && Cfg.Items.Any(i => i.Type == StoreItemType.Trail && StoreTrails.StyleOf(i) != null))
                foreach (var model in StoreTrails.AllModels()) m.AddResource(model);
        });
        JbEvents.PlayerModelsApplied += ApplyPlayerModel;
        ChatDecor.AddPrefix(Name, TitlePrefix);
        ChatDecor.AddColor(Name, ChatColor);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.PlayerModelsApplied -= ApplyPlayerModel;
        ChatDecor.Remove(Name);
        for (var i = 0; i < Players.MaxSlots; i++) RemoveGear(i);
    }

    /// <summary>Configs written before the new items existed get them once (an item an admin removed later stays removed).</summary>
    private void MergeNewDefaults()
    {
        if (Cfg.DefaultsVersion >= StoreConfig.CurrentDefaultsVersion) return;
        Plugin.Config.Update<StoreConfig>("Store", c =>
        {
            c.Items = Merge(c.Items, StoreConfig.DefaultItems());
            if (c.DefaultsVersion < 3) CheapenPaintballs(c.Items);
            c.DefaultsVersion = StoreConfig.CurrentDefaultsVersion;
        });
    }

    /// <summary>
    /// Version 3: paintballs are the cheapest items. The old default rainbow paintballs (600) get the new price; a price
    /// an admin changed is kept.
    /// </summary>
    internal static void CheapenPaintballs(List<StoreItem> items)
    {
        foreach (var it in items.Where(i => i.Id.Equals("paint_random", StringComparison.OrdinalIgnoreCase) && i.Price == 600))
        {
            it.Price = StoreConfig.PaintRainbowPrice;
            if (it.Name == "Paintballs") it.Name = "Rainbow paintballs";
        }
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
        _alpha[player.Slot] = 0;
        _scale[player.Slot] = 1f;
        _trailLast[player.Slot] = null;
        _dropLast[player.Slot] = null;
        RemoveGear(player.Slot);
        // Without the PlayerModels module nobody raises PlayerModelsApplied: apply the store model from here.
        if (Jb.Models is NullServices) SchedulePlayerModel(player.Slot);
    }

    public override void OnMapStart(string mapName)
    {
        foreach (var q in _paint) q.Clear();
        Array.Clear(_trailLast);
        Array.Fill(_alpha, 0);
        Array.Fill(_scale, 1f);
        foreach (var kind in GearTypes)
        {
            Array.Clear(_gear[kind]);
            Array.Clear(_gearId[kind]);
        }
    }

    private void ResetSlot(int slot)
    {
        ClearPieces(slot);
        _trailLast[slot] = null;
        _alpha[slot] = 0;
        _scale[slot] = 1f;
        ClearPaint(slot);
        RemoveGear(slot);
    }

    /// <summary>After PlayerModels gave the player their team model: the equipped store player model replaces it.</summary>
    private void ApplyPlayerModel(JbPlayer player) => SchedulePlayerModel(player.Slot);

    /// <summary>
    /// Set the equipped store model next frame and once more a moment later: the game (agent loadout) and other modules
    /// set the model during the spawn frames and could overwrite a model set only once.
    /// </summary>
    private void SchedulePlayerModel(int slot)
    {
        Server.NextFrame(() => SetPlayerModelNow(slot));
        Plugin.AddTimer(0.3f, () => SetPlayerModelNow(slot));
    }

    private void SetPlayerModelNow(int slot)
    {
        var c = Players.Get(slot).Controller;
        if (c == null || !c.IsValid || !c.IsAliveP() || c.PawnOrNull() is not { } pawn) return;
        if (Equipped(slot, StoreItemType.PlayerModel) is not { Data.Length: > 0 } item) return;
        if (!WearsPlayerModel(c.Team, Cfg.PlayerModelsForGuards, item.Team)) return;
        var model = item.Data.Trim();
        try
        {
            if (!ModelPaths.UsableOnPlayer(model, "Store item " + item.Id)) return;
            if (pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName == model) return;
            pawn.SetModel(model);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Store] player model {Model} could not be applied", model);
        }
    }

    /// <summary>Engine-free: may a player on this team wear a model item locked to <paramref name="itemTeam"/>?</summary>
    internal static bool WearsPlayerModel(CsTeam team, bool guardsToo, string? itemTeam) => (itemTeam ?? "").Trim().ToLowerInvariant() switch
    {
        "ct" => team == CsTeam.CounterTerrorist,
        "t" => team == CsTeam.Terrorist,
        _ => WearsPlayerModel(team, guardsToo)
    };

    internal static bool WearsPlayerModel(CsTeam team, bool guardsToo) =>
        team == CsTeam.Terrorist || guardsToo && team == CsTeam.CounterTerrorist;

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
        if (spec.Contains('|'))
        {
            var options = spec.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (options.Length > 0) spec = options[rng.Next(options.Length)];
        }

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

    internal static Color FromHue(double hue)
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

    /// <summary>
    /// Pack hats only exist once the pack is compiled with them (WorkshopPack.HatModels), face and back equipment once
    /// store pack 2 is uploaded and mounted (WorkshopPack.Store2Equipment).
    /// </summary>
    internal static bool Available(StoreItem i)
    {
        var data = i.Data.Trim();
        if (i.Type == StoreItemType.Hat && data.StartsWith(StoreConfig.PackHatFolder, StringComparison.OrdinalIgnoreCase))
            return Extras.WorkshopPackModule.HatModelsOn;
        if (i.Type is StoreItemType.Face or StoreItemType.Back && data.StartsWith(StoreConfig.PackGearFolder, StringComparison.OrdinalIgnoreCase))
            return Extras.WorkshopPackModule.Store2EquipmentOn;
        return true;
    }

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
            var items = Cfg.Items.Where(i => i.Type == t && Available(i)).ToList();
            if (items.Count == 0) continue;
            var inv = _inv[c.Slot];
            var owned = inv == null ? 0 : items.Count(i => inv.Owned.Contains(i.Id, StringComparer.OrdinalIgnoreCase));
            m.Add(Lang.T("store.cat.count", Lang.T("store.cat." + t.ToString().ToLowerInvariant()), owned, items.Count), x => ShowCategory(x, t));
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

        foreach (var item in Cfg.Items.Where(i => i.Type == type && Available(i)))
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
                    if (item.Type == StoreItemType.PlayerModel) TellModel(c);
                    break;
            }
        }
        else
        {
            var on = ToggleEquip(inv, item);
            Save(c);
            Chat.Tell(c, on ? "store.equipped" : "store.unequipped", item.Name);
            if (on && item.Type == StoreItemType.PlayerModel) TellModel(c);
        }

        ShowCategory(c, item.Type);
    }

    /// <summary>A newly equipped model is put on right away when the player can wear it now, else from the next spawn.</summary>
    private void TellModel(CCSPlayerController c)
    {
        var itemTeam = (Equipped(c.Slot, StoreItemType.PlayerModel)?.Team ?? "").Trim().ToLowerInvariant();
        if (c.IsAliveP() && WearsPlayerModel(c.Team, Cfg.PlayerModelsForGuards, itemTeam))
        {
            SchedulePlayerModel(c.Slot);
            Chat.Tell(c, "store.model.now");
            return;
        }

        Chat.Tell(c, itemTeam == "ct" ? "store.model.when_guard" : Cfg.PlayerModelsForGuards ? "store.model.when" : "store.model.when_prisoner");
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

        if (Equipped(c.Slot, StoreItemType.Tracer) is { } tracer) Tracer(c, impact, tracer);

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

        var filter = ViewFilter(c);
        CEntityInstance?[] parts;
        var feet = c.Origin();
        if (Core.Paint.Splat(impact, c.EyePosition(), feet?.Z ?? float.NaN, color, Extras.WorkshopPackModule.PaintballModels()) is { } splat)
        {
            Restrict(splat, filter);
            Fx.RemoveLater(splat, Math.Max(1f, Cfg.PaintSeconds));
            parts = new CEntityInstance?[] { splat };
        }
        else
        {
            // No splat models (workshop pack off): a small crossed-beam splat.
            const float half = 3f;
            var b1 = Fx.Beam(new Vector(impact.X - half, impact.Y, impact.Z), new Vector(impact.X + half, impact.Y, impact.Z), color, 4f, Cfg.PaintSeconds);
            var b2 = Fx.Beam(new Vector(impact.X, impact.Y - half, impact.Z), new Vector(impact.X, impact.Y + half, impact.Z), color, 4f, Cfg.PaintSeconds);
            Restrict(b1, filter);
            Restrict(b2, filter);
            parts = new CEntityInstance?[] { b1, b2 };
        }

        var q = _paint[c.Slot];
        q.Enqueue(parts);
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
        RemoveGear(victim.Slot);
        if (attacker == null || ReferenceEquals(attacker, victim) || attacker.Controller is not { IsValid: true } ac) return;
        var o = victim.Controller?.Origin() ?? _lastAlive[victim.Slot];
        if (o == null) return;

        if (Equipped(attacker.Slot, StoreItemType.KillEffect) is { } fx)
            KillEffect(fx, new Vector(o.X, o.Y, o.Z), ViewFilter(ac));

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
            _lastAlive[c.Slot] = new Vector(o.X, o.Y, o.Z);
            var allowed = EffectsAllowed(c);

            if (allowed) Trail(c, o);
            else
            {
                _trailLast[c.Slot] = null;
                _dropLast[c.Slot] = null;
            }
            if (allowed && _tick % 2 == 0 && Equipped(c.Slot, StoreItemType.Sparkles) is { } sp) Sparkle(c, o, sp);
            if (allowed && _tick % 4 == 0 && Equipped(c.Slot, StoreItemType.Halo) is { } halo) Swirl(c, o, halo);
            if (allowed && _tick % 7 == 0 && Equipped(c.Slot, StoreItemType.Aura) is { } aura) Aura(c, o, aura);
            if (_tick % 7 == 0)
            {
                ApplyBody(c);
                foreach (var kind in GearTypes) UpdateGear(c, allowed, kind);
            }
        }
    }

    private void Trail(CCSPlayerController c, Vector o)
    {
        if (Equipped(c.Slot, StoreItemType.Trail) is not { } trail)
        {
            _trailLast[c.Slot] = null;
            _dropLast[c.Slot] = null;
            return;
        }

        // Model trails (footprints, stars, fire, ...); a beam in the trail colour when the pack's models are off.
        var style = StoreTrails.StyleOf(trail);
        if (style != null && ModelTrail(c, o, style, trail)) return;

        var here = new Vector(o.X, o.Y, o.Z + 6f);
        var last = _trailLast[c.Slot];
        _trailLast[c.Slot] = here;
        if (last == null || VectorUtil.Distance(last, here) < 12f || VectorUtil.Distance(last, here) > 300f) return;
        var color = style is { Tinted: false } ? ColorOf(style.FallbackColor, Random.Shared) : TrailColor(c.Slot, trail.Color);
        // Tapered: wide at the player, thin at the old end, with a bright thin core on top.
        var life = Math.Max(0.2f, Cfg.TrailSeconds);
        Restrict(Fx.Beam(here, last, color, 5f, 2f, life), ViewFilter(c));
        Restrict(Fx.Beam(here, last, Mix(color, Color.White, 0.45f), 1.5f, 0.5f, life * 0.7f), ViewFilter(c));
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
        // "See-through" body: CS2 player models cannot be drawn half transparent (any alpha below 255 hides the whole
        // player), so the item is a pale spectral tint at full opacity. _alpha keeps the packed tint (0 = none).
        var tint = 0;
        if (allowed && Equipped(c.Slot, StoreItemType.Transparency) is { } t) tint = GhostTint(t.Color).ToArgb();
        if (tint != _alpha[c.Slot])
        {
            var pawn = c.PawnOrNull();
            if (pawn != null)
            {
                var cur = pawn.Render;
                var wasOurs = _alpha[c.Slot] != 0 && cur.ToArgb() == _alpha[c.Slot];
                var plain = cur.R == 255 && cur.G == 255 && cur.B == 255;
                // Never fight another tint (split teams, LRs, rebels): only paint over white or our own previous tint.
                if (tint != 0 && (plain || wasOurs)) c.SetColor(Color.FromArgb(tint));
                else if (tint == 0 && wasOurs) c.SetColor(Color.FromArgb(255, 255, 255, 255));
                if (cur.A < 255) c.SetAlpha(255); // undo the old see-through alpha (it made players invisible)
            }

            _alpha[c.Slot] = tint;
        }

        var scale = 1f;
        if (allowed && Equipped(c.Slot, StoreItemType.BodySize) is { } bs &&
            float.TryParse(bs.Data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
            scale = Math.Clamp(f, 0.6f, 1.4f);
        if (Math.Abs(scale - _scale[c.Slot]) > 0.001f && SetScale(c, scale)) _scale[c.Slot] = scale;
    }

    /// <summary>Opaque pale tint for the ghost skin ("R,G,B", default spectral blue).</summary>
    internal static Color GhostTint(string? spec)
    {
        var c = spec is { Length: > 0 } && !spec.Contains('|') && !spec.Equals("rainbow", StringComparison.OrdinalIgnoreCase) && spec != "255,255,255"
            ? ColorOf(spec, Random.Shared) : Color.FromArgb(255, 170, 210, 255);
        return Color.FromArgb(255, c.R, c.G, c.B);
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

    // ================================================================= hats, face and back equipment (workshop models)

    private void UpdateGear(CCSPlayerController c, bool allowed, StoreItemType kind)
    {
        var item = allowed ? Equipped(c.Slot, kind) : null;
        if (item == null || item.Data.Length == 0 || !Available(item))
        {
            RemoveGear(c.Slot, kind);
            return;
        }

        if (_gear[kind][c.Slot] is { IsValid: true } && _gearId[kind][c.Slot] == item.Id) return;
        RemoveGear(c.Slot, kind);
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        try
        {
            var gear = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (gear == null) return;
            gear.Spawnflags = 256; // no collision
            if (!item.Color.Equals("255,255,255", StringComparison.Ordinal)) gear.Render = ColorOf(item.Color, Random.Shared);
            Fx.SpawnWithModel(gear, item.Data);
            // Upright where it belongs first, then parented to the attachment keeping that offset: the attachment's own
            // axes point along the bone, so snapping onto it would lay the model on its side.
            var (at, attachment) = GearPlacement(c.EyePosition(), pawn.EyeAngles.Y, kind);
            gear.Teleport(at, new QAngle(0, pawn.EyeAngles.Y, 0), new Vector(0, 0, 0));
            gear.AcceptInput("SetParent", pawn, gear, "!activator");
            if (attachment.Length > 0) gear.AcceptInput("SetParentAttachmentMaintainOffset", null, null, attachment);
            // Not drawn for the wearer (it would sit in front of their eyes) and honouring everyone's settings.
            Visibility.OnlyFor(gear, ViewFilter(c, true)!);
            _gear[kind][c.Slot] = gear;
            _gearId[kind][c.Slot] = item.Id;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Store] {Kind} {Model} could not be attached", kind, item.Data);
        }
    }

    /// <summary>Where a piece of equipment goes for a player with these eyes, and the attachment it then follows.</summary>
    private (Vector At, string Attachment) GearPlacement(Vector eye, float yaw, StoreItemType kind)
    {
        var (fx, fy) = (MathF.Cos(yaw * MathF.PI / 180f), MathF.Sin(yaw * MathF.PI / 180f));
        return kind switch
        {
            StoreItemType.Face => (new Vector(eye.X + fx * Cfg.FaceForward, eye.Y + fy * Cfg.FaceForward, eye.Z + Cfg.FaceHeight), Cfg.FaceAttachment),
            StoreItemType.Back => (new Vector(eye.X - fx * Cfg.BackDepth, eye.Y - fy * Cfg.BackDepth, eye.Z - Cfg.BackBelowEye), Cfg.BackAttachment),
            _ => (new Vector(eye.X, eye.Y, eye.Z + Cfg.HatHeight), Cfg.HatAttachment)
        };
    }

    private void RemoveGear(int slot)
    {
        foreach (var kind in GearTypes) RemoveGear(slot, kind);
    }

    private void RemoveGear(int slot, StoreItemType kind)
    {
        Fx.SafeRemove(_gear[kind][slot]);
        _gear[kind][slot] = null;
        _gearId[kind][slot] = null;
    }
}
