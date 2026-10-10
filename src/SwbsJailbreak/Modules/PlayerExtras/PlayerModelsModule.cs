using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "PlayerModels" (UltJB_PlayerModels.sp).</summary>
public sealed class PlayerModelsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>All guards look the same (original: stock ctm_swat). Empty = keep the player's own agent.</summary>
    public string GuardModel { get; set; } = "characters/models/ctm_sas/ctm_sas.vmdl";

    /// <summary>
    /// Prisoner models. [0] = help 1 model (new players), [1] = help 2 model, [2..] = random pool for everyone else.
    /// The Swoobles custom prisoner models cannot load in CS2; the stock CS2 jumpsuit agents keep the 3-slot design.
    /// Workshop addon .vmdl paths can be used instead when the server mounts the addon.
    /// </summary>
    public List<string> PrisonerModels { get; set; } = new()
    {
        "characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl",
        "characters/models/tm_jumpsuit/tm_jumpsuit_variantb.vmdl",
        "characters/models/tm_jumpsuit/tm_jumpsuit_variantc.vmdl"
    };

    /// <summary>ultjb_playermodels_help1_bonushealth / help2.</summary>
    public int Help1BonusHealth { get; set; } = 30;
    public int Help2BonusHealth { get; set; } = 15;
}

/// <summary>
/// Port of UltJB_PlayerModels.sp (v1.20): uniform guard model, prisoner model by help level (new players get a
/// recognisable model and bonus HP), natives ApplyGuardModel / ApplyPrisonerModel and the OnApplied forward.
/// </summary>
public sealed class PlayerModelsModule : JbModule, IPlayerModelService
{
    private PlayerModelsConfig Cfg => Section<PlayerModelsConfig>("PlayerModels");

    public override string Name => "PlayerModels";
    public override int Order => 12;
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        Jb.Models = this;
        JbEvents.SettingsSpawnPost += OnSpawnPost;
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecache);
    }

    public override void OnUnload(bool hotReload) => JbEvents.SettingsSpawnPost -= OnSpawnPost;

    private void OnPrecache(ResourceManifest manifest)
    {
        foreach (var path in Cfg.PrisonerModels.Append(Cfg.GuardModel))
            if (!string.IsNullOrWhiteSpace(path)) manifest.AddResource(path);
    }

    private void OnSpawnPost(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null || !c.IsAliveP()) return;
        if (c.Team == CsTeam.Terrorist)
        {
            var help = Jb.Settings.GetHelpNumber(player) - 1;
            if (help == 0 && Cfg.Help1BonusHealth > 0) c.SetHp(c.GetHp() + Cfg.Help1BonusHealth);
            else if (help == 1 && Cfg.Help2BonusHealth > 0) c.SetHp(c.GetHp() + Cfg.Help2BonusHealth);
            ApplyPrisonerModel(player);
        }
        else if (c.Team == CsTeam.CounterTerrorist)
        {
            ApplyGuardModel(player);
        }

        // Deviation from the original: always raised (it skipped donator models, leaving Xray glows stale).
        JbEvents.RaisePlayerModelsApplied(player);
    }

    public bool ApplyGuardModel(JbPlayer player) => SetModel(player, Cfg.GuardModel);

    public bool ApplyPrisonerModel(JbPlayer player)
    {
        var list = Cfg.PrisonerModels.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (list.Count == 0) return false;
        var help = Jb.Settings.GetHelpNumber(player) - 1;
        string model;
        if (help >= 0 && help < list.Count && list.Count > 2) model = list[help];
        else model = list.Count > 2 ? list[Random.Shared.Next(2, list.Count)] : list[Random.Shared.Next(list.Count)];
        return SetModel(player, model);
    }

    private bool SetModel(JbPlayer player, string model)
    {
        var pawn = player.Controller?.PawnOrNull();
        if (pawn == null || string.IsNullOrWhiteSpace(model)) return false;
        try
        {
            pawn.SetModel(model);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[PlayerModels] model {Model} could not be applied - fix the PlayerModels config", model);
            return false;
        }
    }
}
