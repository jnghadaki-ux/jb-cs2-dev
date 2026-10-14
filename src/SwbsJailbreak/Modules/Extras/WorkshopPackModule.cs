using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.PlayerExtras;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "WorkshopPack": the SWBS workshop addon (sounds) built by scripts/workshop/build_pack.py.</summary>
public sealed class WorkshopPackConfig
{
    /// <summary>Turn on once the addon is uploaded and MultiAddonManager mounts it (mm_extra_addons &lt;id&gt;).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>For your reference: the Steam Workshop id of the uploaded pack (MultiAddonManager needs it, not the plugin).</summary>
    public string WorkshopId { get; set; } = "";

    /// <summary>The pack's sound event file, precached at map start so its sounds can play.</summary>
    public string SoundEventsFile { get; set; } = "soundevents/swbs_jailbreak.vsndevts";

    /// <summary>More pack files to precache (models, particles) once you add them to the addon.</summary>
    public List<string> ExtraResources { get; set; } = new();

    /// <summary>Set by the plugin after it pointed the sound settings at the pack once; set false to do it again.</summary>
    public bool SoundsApplied { get; set; } = false;
}

/// <summary>
/// Loads the SWBS workshop pack: precaches its sound event file (and any extra resources) and, the first time it is
/// enabled, points every sound setting the pack covers at the pack's sound events (round wins, LR activated, freekill,
/// guard message, Horror Movie cowbell, RPG / Rocketeer shoot, explode, pain and death, slaps, jetpack, teleports) and
/// adds the "Cowbell" kill sound to the store. Admins can change any of them afterwards; !workshoppack re-applies.
/// Players only hear the sounds when the server makes them download the addon (MultiAddonManager).
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

    public override void OnLoad(bool hotReload)
    {
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            if (!Cfg.Enabled) return;
            if (!string.IsNullOrWhiteSpace(Cfg.SoundEventsFile)) manifest.AddResource(Cfg.SoundEventsFile.Trim());
            foreach (var r in Cfg.ExtraResources.Where(r => !string.IsNullOrWhiteSpace(r))) manifest.AddResource(r.Trim());
        });
        if (Cfg.Enabled && !Cfg.SoundsApplied) Apply();
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
