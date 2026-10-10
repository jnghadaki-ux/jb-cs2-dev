using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Effects;

/// <summary>Section "Effects" (UltJB_API_Effects.sp + Effects/*.sp).</summary>
public sealed class EffectsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_select_effect_time: seconds to pick an effect before a random one is chosen.</summary>
    public int SelectEffectTime { get; set; } = 15;

    /// <summary>UltJB_Effect_LowGravity LOW_GRAVITY_VALUE.</summary>
    public float LowGravityValue { get; set; } = 0.4f;

    /// <summary>UltJB_Effect_FarOut FOV.</summary>
    public int FarOutFov { get; set; } = 160;

    /// <summary>Drugs colour-cycle updates per second (original: every 0.01 s; CS2 limited to reduce network load).</summary>
    public float DrugsUpdatesPerSecond { get; set; } = 16f;
}

internal sealed class EffectState
{
    public readonly HashSet<int> Active = new();
    public readonly Dictionary<int, Timer> Timers = new();
    public Timer? SelectionTimer;
    public Action<JbPlayer, int>? OnSelected;
    public Action<JbPlayer>? OnFailed;
    public bool Selecting;

    // per-effect scratch
    public int[] DrugColor = { 255, 0, 0, 140 };
    public int DrugStep;
    public float NextSlap;
    public float SlapDelay;
    public int DrunkFov = 90;
    public bool DrunkIncreaseFov = true;
    public float DrunkAngle;
    public bool DrunkIncreaseAngle;
    public float DrunkNextView;
}

/// <summary>
/// Effects API: a registry of per-player effects (drugs, drunk, low gravity, speed...) that LRs and days apply,
/// plus the "Select an effect" menu with a timeout that picks a random effect.
/// </summary>
public sealed class EffectsModule : JbModule, IEffectService
{
    private EffectsConfig Cfg => Section<EffectsConfig>("Effects");

    public override string Name => "Effects";
    public override int Order => 7;
    public override bool Enabled => Cfg.Enabled;

    private readonly List<EffectDefinition> _effects = new();
    public IReadOnlyList<EffectDefinition> All => _effects;

    private static EffectState S(JbPlayer p) => p.Data<EffectState>();

    public override void OnLoad(bool hotReload)
    {
        Jb.Effects = this;
        DamageService.Register(Name, 95, OnDamage);
        BuiltInEffects.RegisterAll(this, Cfg);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        foreach (var p in Players.Connected()) StopAllEffects(p);
    }

    // ------------------------------------------------------------------ registry

    public int Register(EffectDefinition definition)
    {
        var existing = _effects.FindIndex(e => string.Equals(e.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _effects.RemoveAt(existing);
        definition.AssignId(_effects.Count == 0 ? 1 : _effects.Max(e => e.Id) + 1); // 0 = "No effect"
        _effects.Add(definition);
        _effects.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return definition.Id;
    }

    private EffectDefinition? Find(int id) => _effects.FirstOrDefault(e => e.Id == id);

    public int GetEffectId(string name) =>
        _effects.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? -1;

    public float GetDefaultData(int effectId) => Find(effectId)?.DefaultData ?? 0f;

    public bool HasEffect(JbPlayer player, int effectId) => S(player).Active.Contains(effectId);

    public bool StartEffect(JbPlayer player, int effectId, float data = 0f)
    {
        var def = Find(effectId);
        if (def == null) return false;
        var s = S(player);
        if (!s.Active.Add(effectId))
        {
            Chat.Tell(player, "effects.already_in");
            return false;
        }

        try
        {
            def.OnStart(player, data == 0f ? def.DefaultData : data);
            return true;
        }
        catch (Exception ex)
        {
            s.Active.Remove(effectId);
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(Logger, ex, "Effect {Effect} failed to start", def.Name);
            Chat.Tell(player, "effects.start_error", def.Name);
            return false;
        }
    }

    public bool StopEffect(JbPlayer player, int effectId)
    {
        var def = Find(effectId);
        var s = S(player);
        if (def == null || !s.Active.Remove(effectId)) return false;
        KillEffectTimer(s, effectId);
        try
        {
            def.OnStop(player);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(Logger, ex, "Effect {Effect} failed to stop", def.Name);
        }

        return true;
    }

    public void StopAllEffects(JbPlayer player)
    {
        foreach (var id in S(player).Active.ToList()) StopEffect(player, id);
    }

    internal static void SetEffectTimer(JbPlayer p, int id, Timer? t)
    {
        var s = S(p);
        KillEffectTimer(s, id);
        if (t != null) s.Timers[id] = t;
    }

    private static void KillEffectTimer(EffectState s, int id)
    {
        if (!s.Timers.Remove(id, out var t)) return;
        try { t.Kill(); }
        catch { /* already stopped */ }
    }

    internal EffectState StateOf(JbPlayer p) => S(p);

    // ------------------------------------------------------------------ selection menu

    public bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, int> onSelected, Action<JbPlayer>? onFailed = null, int selectTimeOverride = 0)
    {
        var c = player.Controller;
        if (c == null) return false;
        if (_effects.Count == 0)
        {
            Chat.Tell(c, "effects.none_available");
            onFailed?.Invoke(player);
            return false;
        }

        var s = S(player);
        CancelSelection(player);
        s.OnSelected = onSelected;
        s.OnFailed = onFailed;
        s.Selecting = true;

        var m = new JbMenu(Lang.T("effects.menu.title")) { ExitButton = false, Timeout = -1 };
        m.Add(Lang.T("effects.menu.none"), x => Select(player, 0));
        foreach (var e in _effects)
        {
            var id = e.Id;
            m.Add(Hud.Escape(e.Name), x => Select(player, id));
        }

        m.Open(c);
        var seconds = selectTimeOverride >= 1 ? selectTimeOverride : Math.Max(1, Cfg.SelectEffectTime);
        var slot = player.Slot;
        s.SelectionTimer = Round.Timer(seconds, () =>
        {
            var p = Players.Get(slot);
            var st = S(p);
            st.SelectionTimer = null;
            if (!st.Selecting) return;
            var ctrl = p.Controller;
            if (ctrl != null)
            {
                MenuService.Close(ctrl, false);
                Chat.Tell(ctrl, "effects.random");
            }

            var pick = _effects.Count == 0 ? 0 : _effects[Random.Shared.Next(_effects.Count)].Id;
            Select(p, pick);
        });
        return true;
    }

    private void Select(JbPlayer player, int effectId)
    {
        var s = S(player);
        if (!s.Selecting) return;
        s.Selecting = false;
        try { s.SelectionTimer?.Kill(); }
        catch { /* ignore */ }
        s.SelectionTimer = null;
        var def = Find(effectId);
        if (def != null) Chat.TellAll("effects.selected", player.Name, def.Name);
        var cb = s.OnSelected;
        s.OnSelected = null;
        s.OnFailed = null;
        cb?.Invoke(player, def == null ? 0 : effectId);
    }

    public bool CancelSelection(JbPlayer player)
    {
        var s = S(player);
        var was = s.Selecting;
        s.Selecting = false;
        try { s.SelectionTimer?.Kill(); }
        catch { /* ignore */ }
        s.SelectionTimer = null;
        s.OnSelected = null;
        s.OnFailed = null;
        var c = player.Controller;
        if (was && c != null && MenuService.HasMenu(c)) MenuService.Close(c, false);
        return was;
    }

    // ------------------------------------------------------------------ lifecycle

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        // Selection is abandoned on death: report failure like a closed menu.
        var s = S(victim);
        if (s.Selecting)
        {
            var failed = s.OnFailed;
            CancelSelection(victim);
            failed?.Invoke(victim);
        }
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        foreach (var p in Players.Connected())
        {
            CancelSelection(p);
            StopAllEffects(p);
        }
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        CancelSelection(player);
        StopAllEffects(player);
    }

    // ------------------------------------------------------------------ HS only (damage pipeline)

    internal int HeadshotsOnlyId = -1;

    private HookResult OnDamage(DamageContext ctx)
    {
        if (HeadshotsOnlyId < 0 || ctx.Attacker == null) return HookResult.Continue;
        var victim = Players.Get(ctx.Victim);
        if (victim == null || !S(victim).Active.Contains(HeadshotsOnlyId)) return HookResult.Continue;
        var weapon = ctx.Attacker.ActiveWeapon()?.DesignerName ?? ctx.Weapon;
        if (ctx.HitGroup == HitGroup_t.HITGROUP_HEAD || Weapons.IsKnife(weapon)) return HookResult.Continue;
        return HookResult.Handled;
    }
}
