using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "PlayerModels" (UltJB_PlayerModels.sp).</summary>
public sealed class PlayerModelsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Give every guard GuardModel and every prisoner one of PrisonerModels on spawn (and to everyone already in game
    /// when the plugin loads). Off by default: a model that is missing on the current CS2 build (Valve moved its agents
    /// to agents/models/...) or not a full player agent leaves players as small boxes with a huge first-person gun.
    /// Turn on only with models you have checked in game.
    /// </summary>
    public bool ApplyModels { get; set; } = false;

    /// <summary>All guards look the same (original: stock ctm_swat). Empty = keep the player's own agent.</summary>
    public string GuardModel { get; set; } = "characters/models/ctm_sas/ctm_sas.vmdl";

    /// <summary>
    /// Prisoner models. [0] = help 1 model (new players), [1] = help 2 model, [2..] = random pool for everyone else.
    /// Default: the stock CS2 jumpsuit agents. Turning WorkshopPack.Enabled on switches this to the SWBS prisoners,
    /// rebuilt for CS2 in the workshop pack (characters/models/swbs/prisoners/...).
    /// </summary>
    public List<string> PrisonerModels { get; set; } = new()
    {
        "characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl",
        "characters/models/tm_jumpsuit/tm_jumpsuit_variantb.vmdl",
        "characters/models/tm_jumpsuit/tm_jumpsuit_variantc.vmdl"
    };

    /// <summary>
    /// The workshop pack's SWBS prisoners (characters/models/swbs/prisoners/...) were converted from CS:GO and miss CS2's
    /// player skeleton bones (hands, IK targets, aim axis): every spawn floods the console with "Attempted to attach
    /// attachment ... to an unknown bone" and the prisoner cannot hold weapons properly. They are skipped unless this is
    /// turned on (only after the pack's prisoners are rebuilt on a CS2 agent skeleton).
    /// </summary>
    public bool AllowPackPrisonerModels { get; set; } = false;

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

    /// <summary>Plugin (re)load: players already alive keep the engine's agent until they respawn unless we set it now.</summary>
    public override void OnAllModulesLoaded(bool hotReload)
    {
        if (!hotReload || !Cfg.ApplyModels) return;
        Server.NextFrame(ApplyAll);
    }

    public override void OnRoundFreezeEnd()
    {
        // Safety net: a model the game or a late module reset during the spawn frames is put back once players can move.
        if (Cfg.ApplyModels) ApplyAll();
    }

    /// <summary>The team model for every alive player (then the store model on top through PlayerModelsApplied).</summary>
    private void ApplyAll()
    {
        foreach (var c in Players.Alive().ToList())
            if (c.Jb() is { } p && ApplyTeamModel(p))
                JbEvents.RaisePlayerModelsApplied(p);
    }

    private bool ApplyTeamModel(JbPlayer player)
    {
        var ctrl = player.Controller;
        if (ctrl == null || !ctrl.IsAliveP()) return false;
        if (ctrl.Team == CsTeam.Terrorist) return ApplyPrisonerModel(player);
        if (ctrl.Team == CsTeam.CounterTerrorist) return ApplyGuardModel(player);
        return false;
    }

    private void OnPrecache(ResourceManifest manifest)
    {
        if (!Cfg.ApplyModels) return;
        foreach (var path in UsablePrisonerModels().Append(Cfg.GuardModel))
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
        }

        if (!Cfg.ApplyModels)
        {
            JbEvents.RaisePlayerModelsApplied(player);
            return;
        }

        // Next frame: a model set during the spawn itself can be overwritten by the agent the game assigns. Once more a
        // moment later for the same reason (the agent loadout can land after the first frame); the store model is
        // re-applied on top each time through PlayerModelsApplied.
        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var p = Players.Get(slot);
            if (p.Controller is not { } ctrl || !ctrl.IsAliveP()) return;
            ApplyTeamModel(p);
            // Deviation from the original: always raised (it skipped donator models, leaving Xray glows stale).
            JbEvents.RaisePlayerModelsApplied(p);
        });
        Plugin.AddTimer(0.2f, () =>
        {
            var p = Players.Get(slot);
            if (ApplyTeamModel(p)) JbEvents.RaisePlayerModelsApplied(p);
        });
    }

    public bool ApplyGuardModel(JbPlayer player) => Cfg.ApplyModels && SetModel(player, Cfg.GuardModel);

    public bool ApplyPrisonerModel(JbPlayer player)
    {
        if (!Cfg.ApplyModels) return false;
        var list = UsablePrisonerModels();
        if (list.Count == 0) return false;
        var help = Jb.Settings.GetHelpNumber(player) - 1;
        string model;
        if (help >= 0 && help < list.Count && list.Count > 2) model = list[help];
        else if (_lastPrisonerModel[player.Slot] is { } kept && list.Contains(kept) && CurrentModel(player) == kept) model = kept;
        else model = list.Count > 2 ? list[Random.Shared.Next(2, list.Count)] : list[Random.Shared.Next(list.Count)];
        _lastPrisonerModel[player.Slot] = model;
        return SetModel(player, model);
    }

    /// <summary>Engine-free: is this one of the pack's CS:GO-converted prisoners (no CS2 skeleton)?</summary>
    internal static bool IsPackPrisoner(string path) => path.Contains("/swbs/prisoners/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The configured prisoner models minus the unusable ones; the stock jumpsuits when nothing is left.</summary>
    internal List<string> UsablePrisonerModels() => Usable(Cfg.PrisonerModels, Cfg.AllowPackPrisonerModels);

    internal static List<string> Usable(IEnumerable<string> configured, bool allowPack)
    {
        var list = configured.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim())
            .Where(m => allowPack || !IsPackPrisoner(m)).ToList();
        return list.Count > 0 ? list : new PlayerModelsConfig().PrisonerModels;
    }

    private readonly string?[] _lastPrisonerModel = new string?[Players.MaxSlots];

    private static string? CurrentModel(JbPlayer player)
    {
        try { return player.Controller?.PawnOrNull()?.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName; }
        catch { return null; }
    }

    private bool SetModel(JbPlayer player, string model)
    {
        var pawn = player.Controller?.PawnOrNull();
        if (pawn == null || string.IsNullOrWhiteSpace(model)) return false;
        model = model.Trim();
        try
        {
            if (!ModelPaths.UsableOnPlayer(model, "PlayerModels")) return false;
            if (CurrentModel(player) == model) return true;
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
