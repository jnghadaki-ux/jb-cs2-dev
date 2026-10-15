using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.PlayerExtras;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>
/// Section "WorkshopPack": the SWBS workshop addon (sounds, props, prisoner player models) and the separate store pack
/// (character player models), both built by scripts/workshop/build_pack.py.
/// </summary>
public sealed class WorkshopPackConfig
{
    /// <summary>
    /// Use the SWBS workshop addon (needs MultiAddonManager mounting it: mm_extra_addons &lt;WorkshopId&gt;). Off, store
    /// trails and paintballs fall back to plain beam lines and the pack's sounds and models are not used.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>For your reference: the Steam Workshop id of the uploaded pack (MultiAddonManager needs it, not the plugin).</summary>
    public string WorkshopId { get; set; } = "";

    /// <summary>The pack's sound event file, precached at map start so its sounds can play.</summary>
    public string SoundEventsFile { get; set; } = "soundevents/swbs_jailbreak.vsndevts";

    /// <summary>More pack files to precache (models, particles) once you add them to the addon.</summary>
    public List<string> ExtraResources { get; set; } = new();

    /// <summary>Set by the plugin after it pointed the sound settings at the pack once; set false to do it again.</summary>
    public bool SoundsApplied { get; set; } = false;

    /// <summary>
    /// Use the pack's models (converted from the original SWBS props) instead of beams: Capture the Flag flag and
    /// capture point, Race LR and Speedrun finish line, RPG / Rocketeer rockets. Turn off if compile_pack.bat reported
    /// fewer than 5 models (missing models would show as the engine's error model).
    /// </summary>
    public bool UseModels { get; set; } = true;

    public string FlagModel { get; set; } = "models/swbs/flag.vmdl";
    public string CapturePointModel { get; set; } = "models/swbs/capture_point.vmdl";
    public string FinishLineModel { get; set; } = "models/swbs/finish_line.vmdl";
    public string MissileModel { get; set; } = "models/swbs/missile.vmdl";

    /// <summary>Converted too, not used yet (CS2 cannot replace the knife's first-person model).</summary>
    public string RocketLauncherModel { get; set; } = "models/swbs/rocket_launcher.vmdl";

    /// <summary>
    /// Paintball splat models (the 3 original SWBS splats + 5 new shapes, scripts/workshop/paintballs.py). Store and
    /// warden paintballs pick one at random per shot; with the pack or UseModels off they draw beam splats.
    /// </summary>
    public List<string> PaintballModels { get; set; } = DefaultPaintballModels();

    internal static List<string> DefaultPaintballModels() =>
        new[] { "splat_blue", "splat_green", "splat_pink", "splat_1", "splat_2", "splat_3", "splat_4", "splat_5" }
            .Select(n => $"models/swbs/paintballs/{n}.vmdl").ToList();

    /// <summary>
    /// Turn on once the SWBS store pack (addon swbs_store, its own Workshop item, also in mm_extra_addons) is uploaded:
    /// adds its character player models to the store once (category "Player models"). Independent of Enabled.
    /// </summary>
    public bool StoreModels { get; set; } = false;

    /// <summary>For your reference: the Steam Workshop id of the store pack.</summary>
    public string StoreWorkshopId { get; set; } = "";

    /// <summary>Set by the plugin after it added the store pack's models to the store; set false to add them again.</summary>
    public bool StoreModelsAdded { get; set; } = false;

    /// <summary>Which set of store pack models was added; newer pack models are added once when this is behind.</summary>
    public int StoreModelsVersion { get; set; }

    internal const int CurrentStoreModelsVersion = 2; // 1: Deadpool X-Force, the Flash; 2: KOTOR characters
}

/// <summary>
/// Loads the SWBS workshop pack: precaches its sound event file (and any extra resources) and, the first time it is
/// enabled, points every sound setting the pack covers at the pack's sound events (round wins, LR activated, freekill,
/// guard message, Horror Movie cowbell, RPG / Rocketeer shoot, explode, pain and death, slaps, jetpack, teleports),
/// and adds the "Cowbell" kill sound to the store. The pack's SWBS prisoner models are not switched on: they were converted
/// from CS:GO without CS2's player skeleton bones (see PlayerModelsConfig.AllowPackPrisonerModels).
/// Admins can change any of them afterwards; !workshoppack re-applies. With StoreModels on, the store pack's character
/// player models are added to the store once. Players only get the files when the server makes them download the
/// addons (MultiAddonManager).
/// </summary>
public sealed class WorkshopPackModule : JbModule
{
    private WorkshopPackConfig Cfg => Section<WorkshopPackConfig>("WorkshopPack");

    public override string Name => "WorkshopPack";
    public override int Order => 1;

    /// <summary>Sound event names defined in the pack (scripts/workshop/build_pack.py writes the same names).</summary>
    internal static class Events
    {
        public const string GuardsWin = "swbs.guards_win";
        public const string PrisonersWin = "swbs.prisoners_win";
        public const string LrActivated = "swbs.lr_activated";
        public const string Freekill = "swbs.freekill";
        public const string ChatAlert = "swbs.chat_alert";
        public const string Cowbell = "swbs.cowbell";
        public const string RpgShoot = "swbs.rpg_shoot";
        public const string RpgExplode = "swbs.rpg_explode";
        public const string RpgPain = "swbs.rpg_pain";
        public const string RpgDeath = "swbs.rpg_death";
        public const string Jetpack = "swbs.jetpack";
        public const string Slap = "swbs.slap";
        public const string Teleport = "swbs.teleport";

        public static readonly string[] All =
        {
            GuardsWin, PrisonersWin, LrActivated, Freekill, ChatAlert, Cowbell, RpgShoot, RpgExplode, RpgPain, RpgDeath, Jetpack, Slap, Teleport
        };
    }

    /// <summary>The SWBS prisoner player models in the pack (scripts/workshop/prisoners.py writes the same paths).</summary>
    internal static readonly string[] PrisonerModels =
    {
        "characters/models/swbs/prisoners/male_black_big/male_black_big.vmdl",
        "characters/models/swbs/prisoners/female_asian/female_asian.vmdl",
        "characters/models/swbs/prisoners/male_white_hair/male_white_hair.vmdl"
    };

    /// <summary>The store pack's player models (scripts/workshop/store_models.py writes the same paths).</summary>
    internal static List<StoreItem> StoreModelItems() => new()
    {
        new() { Id = "model_deadpool_xforce", Name = "Deadpool (X-Force)", Type = StoreItemType.PlayerModel,
            Data = "characters/models/swbs/store/deadpool_xforce/deadpool_xforce.vmdl", Price = 6000 },
        new() { Id = "model_flash", Name = "The Flash", Type = StoreItemType.PlayerModel,
            Data = "characters/models/swbs/store/flash/flash.vmdl", Price = 6000 },
        // KOTOR characters (scripts/workshop/kotor_models.py, from the server owner's own game files)
        KotorItem("kotor_malak", "Darth Malak", 7000),
        KotorItem("kotor_revan", "Darth Revan", 7000),
        KotorItem("kotor_sion", "Darth Sion", 7000),
        KotorItem("kotor_traya", "Darth Traya", 7000),
        KotorItem("kotor_nihilus", "Darth Nihilus", 7000),
        KotorItem("kotor_sith_trooper", "Sith Trooper (silver)", 5000),
        KotorItem("kotor_sith_trooper_red", "Sith Trooper (red)", 5000)
    };

    private static StoreItem KotorItem(string name, string title, int price) => new()
    {
        Id = "model_" + name, Name = title, Type = StoreItemType.PlayerModel,
        Data = $"characters/models/swbs/store/{name}/{name}.vmdl", Price = price
    };

    private static WorkshopPackModule? _instance;

    /// <summary>The pack model to use for a prop, or null (pack off, models off or no path): callers draw beams instead.</summary>
    internal static string? Model(Func<WorkshopPackConfig, string> pick)
    {
        var cfg = _instance?.Cfg;
        if (cfg is not { Enabled: true, UseModels: true }) return null;
        var path = pick(cfg).Trim();
        return path.Length == 0 ? null : path;
    }

    private static readonly IReadOnlyList<string> NoModels = Array.Empty<string>();

    /// <summary>True when the pack and its models are on (trail pieces, splats, props can be used).</summary>
    internal static bool PackModelsOn => _instance?.Cfg is { Enabled: true, UseModels: true };

    /// <summary>The paintball splat models to use, or none (pack off, models off): callers draw beam splats instead.</summary>
    internal static IReadOnlyList<string> PaintballModels()
    {
        var cfg = _instance?.Cfg;
        if (cfg is not { Enabled: true, UseModels: true }) return NoModels;
        return cfg.PaintballModels.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
    }

    private IEnumerable<string> Models() =>
        new[] { Cfg.FlagModel, Cfg.CapturePointModel, Cfg.FinishLineModel, Cfg.MissileModel, Cfg.RocketLauncherModel }
            .Concat(Cfg.PaintballModels)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim());

    public override void OnUnload(bool hotReload)
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    public override void OnLoad(bool hotReload)
    {
        _instance = this;
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            if (!Cfg.Enabled) return;
            if (!string.IsNullOrWhiteSpace(Cfg.SoundEventsFile)) manifest.AddResource(Cfg.SoundEventsFile.Trim());
            if (Cfg.UseModels) foreach (var m in Models()) manifest.AddResource(m);
            foreach (var r in Cfg.ExtraResources.Where(r => !string.IsNullOrWhiteSpace(r))) manifest.AddResource(r.Trim());
        });
        if (Cfg.Enabled && !Cfg.SoundsApplied) Apply();
        if (Cfg.Enabled && string.IsNullOrWhiteSpace(Cfg.WorkshopId))
            Logger.LogWarning("[WorkshopPack] Enabled, but WorkshopPack.WorkshopId is empty. Set it to the pack's Workshop id and " +
                              "add the same id to MultiAddonManager (mm_extra_addons), or players see missing models, beam-only " +
                              "trails / paintballs and hear no pack sounds.");
        if (Cfg.StoreModels && (!Cfg.StoreModelsAdded || Cfg.StoreModelsVersion < WorkshopPackConfig.CurrentStoreModelsVersion))
            AddStoreModels();
    }

    private void AddStoreModels()
    {
        var items = StoreModelItems();
        Plugin.Config.Update<StoreConfig>("Store", c => c.Items = StoreModule.Merge(c.Items, items));
        Plugin.Config.Update<WorkshopPackConfig>("WorkshopPack", c =>
        {
            c.StoreModelsAdded = true;
            c.StoreModelsVersion = WorkshopPackConfig.CurrentStoreModelsVersion;
        });
        Logger.LogInformation("[WorkshopPack] added the store pack's {Count} player models to the store", items.Count);
    }

    [ConsoleCommand("css_workshoppack", "sm_workshoppack - point the sound settings at the SWBS workshop pack again")]
    public void CmdApply(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "root")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (!Cfg.Enabled) { Chat.ReplyT(caller, info, "workshop.disabled"); return; }
        Apply();
        Chat.ReplyT(caller, info, "workshop.applied", Events.All.Length);
    }

    private void Apply()
    {
        var config = Plugin.Config;
        config.Update<Settings.SettingsConfig>("Settings", c =>
        {
            c.GuardsWinSound = Events.GuardsWin;
            c.PrisonersWinSound = Events.PrisonersWin;
        });
        config.Update<LastRequest.LastRequestConfig>("LastRequest", c => c.ActivatedSound = Events.LrActivated);
        config.Update<Commands.CommandsConfig>("Commands", c =>
        {
            c.FreekillSound = Events.Freekill;
            c.GuardMessageSound = Events.ChatAlert;
        });
        config.Update<Days.DaysLargeConfig>("DaysLarge", c => c.HorrorMoveSound = Events.Cowbell);
        config.Update<Days.RpgConfig>("Rpg", c =>
        {
            c.ShootSound = Events.RpgShoot;
            c.ExplosionSound = Events.RpgExplode;
            c.PainSound = Events.RpgPain;
            c.DeathSound = Events.RpgDeath;
        });
        config.Update<Admin.AdminConfig>("Admin", c => c.SlapSound = Events.Slap);
        config.Update<LastGuard.LastGuardConfig>("LastGuard", c => c.SlapSound = Events.Slap);
        config.Update<Games.GameDaysConfig>("GameDays", c => c.JetpackSound = Events.Jetpack);
        config.Update<Days.NewDaysConfig>("DaysNew", c => c.TeleportSound = Events.Teleport);
        config.Update<LastRequest.LrExtraConfig>("LastRequestsExtra", c => c.TeleporterSound = Events.Teleport);
        config.Update<StoreConfig>("Store", c =>
        {
            if (!c.Items.Any(i => i.Id.Equals("killsound_cowbell", StringComparison.OrdinalIgnoreCase)))
                c.Items.Add(new StoreItem { Id = "killsound_cowbell", Name = "Cowbell", Type = StoreItemType.KillSound, Data = Events.Cowbell, Price = 800 });
        });
        config.Update<WorkshopPackConfig>("WorkshopPack", c => c.SoundsApplied = true);
        Logger.LogInformation("[WorkshopPack] sound settings now use the pack's {Count} sound events", Events.All.Length);
    }
}
