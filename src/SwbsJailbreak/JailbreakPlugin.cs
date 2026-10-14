using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak;

/// <summary>
/// SWBS Ultimate Jailbreak for Counter-Strike 2 (CounterStrikeSharp + Metamod:Source 2).
/// A port of hlstriker's SMPlugins "Ultimate Jailbreak" (Swoobles) and its companion plugins.
/// Created by Gdaki (original work). Enhanced with Claude Code (CS2 port and enhancements).
/// Inspired by: designed after HlStriker (SMPlugins Ultimate Jailbreak), SWBS development/lead admins.
/// </summary>
[MinimumApiVersion(300)]
public sealed class JailbreakPlugin : BasePlugin
{
    public const string PluginVersion = "0.14.1"; // .NET 10 / CSS 1.0.376 build

    public override string ModuleName => "SWBS Ultimate Jailbreak";
    public override string ModuleVersion => PluginVersion;
    public override string ModuleAuthor => "Gdaki (enhanced with Claude Code) - inspired by HlStriker, SWBS development/lead admins";
    public override string ModuleDescription => "Ultimate Jailbreak for CS2 - warden, cells, last requests, days, effects, admin & fun commands";

    public static JailbreakPlugin? Instance { get; private set; }

    public JailbreakConfig Config { get; } = new();

    private readonly List<JbModule> _modules = new();
    public IReadOnlyList<JbModule> Modules => _modules;

    public string ConfigDirectory => Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "SwbsJailbreak"));

    private int _tick;

    public T? GetModule<T>() where T : JbModule => _modules.OfType<T>().FirstOrDefault();

    public override void Load(bool hotReload)
    {
        Instance = this;
        Config.Load(Path.Combine(ConfigDirectory, "SwbsJailbreak.json"), Logger);
        _ = Config.General;
        MigrateConfig();
        Lang.Load(ModuleDirectory, Config.General.Language);
        Storage.Init(Path.Combine(ConfigDirectory, "data"));
        Hud.ResetAll();

        // ---- core listeners / events ----
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(DamageService.OnPlayerTakeDamagePre);
        RegisterListener<Listeners.CheckTransmit>(Glow.OnCheckTransmit);
        RegisterListener<Listeners.CheckTransmit>(Visibility.OnCheckTransmit);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(DamageService.OnPlayerDeathPre, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventPlayerPing>(Aim.OnPlayerPing);
        AddCommandListener("drop", WeaponService.OnDropCommand);
        // Before any module's say listener, so a pending chat prompt captures the line first.
        AddCommandListener("say", ChatInput.OnSay, HookMode.Pre);
        AddCommandListener("say_team", ChatInput.OnSay, HookMode.Pre);
        AddCommand("css_jb_reload", "Reload SWBS Jailbreak config and phrases", OnReloadCommand);
        AddCommand("css_jb_version", "Show SWBS Jailbreak version", OnVersionCommand);

        AddTimer(Math.Max(5, Config.General.DataFlushSeconds), () => Storage.Flush(), TimerFlags.REPEAT);
        JbEvents.CellsOpened += () =>
        {
            if (!Round.Machine.MarkCellsOpened()) DebugLog("Round: " + Round.Machine.LastRejection);
        };

        // ---- modules (discovered by reflection, sorted by Order) ----
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(JbModule).IsAssignableFrom(t));
        foreach (var type in types)
        {
            try
            {
                if (Activator.CreateInstance(type) is JbModule m)
                {
                    m.Plugin = this;
                    _modules.Add(m);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create module {Module}", type.Name);
            }
        }

        _modules.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));
        _modules.RemoveAll(m =>
        {
            try
            {
                if (m.Enabled) return false;
                Logger.LogInformation("Module {Module} disabled by config", m.Name);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Module {Module} Enabled check failed", m.Name);
                return true;
            }
        });

        foreach (var m in _modules.ToList())
        {
            try
            {
                RegisterAllAttributes(m);
                m.OnLoad(hotReload);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Module {Module} failed to load - disabled", m.Name);
                _modules.Remove(m);
            }
        }

        foreach (var m in _modules) Safe(m, x => x.OnAllModulesLoaded(hotReload), "OnAllModulesLoaded");
        Config.SaveIfDirty(Logger);

        if (hotReload)
        {
            foreach (var c in Players.All())
            {
                Players.OnConnect(c);
                var p = Players.Get(c)!;
                foreach (var m in _modules) Safe(m, x => x.OnPlayerConnected(p), "OnPlayerConnected");
            }

            // Late load: synchronise with the real game state instead of assuming a fresh round.
            var gr = Round.GameRules;
            Round.Machine.Synchronise(gr == null || gr.WarmupPeriod ? RoundPhase.Inactive : gr.FreezePeriod ? RoundPhase.FreezeTime : RoundPhase.Active);
            Round.StartTime = Server.CurrentTime;
            Round.FreezeEndTime = Server.CurrentTime;
        }

        Logger.LogInformation("{Name} v{Version} loaded: {Count} modules, {Phrases} phrases. Created by Gdaki, enhanced with Claude Code - inspired by HlStriker, SWBS development/lead admins.",
            ModuleName, ModuleVersion, _modules.Count, Lang.Count);
    }

    /// <summary>One-time changes to existing config files (General.ConfigRevision). New installs start at the latest.</summary>
    private void MigrateConfig()
    {
        const string oldModel = "characters/models/ctm_heavy/ctm_heavy.vmdl";
        if (Config.General.ConfigRevision < 1)
        {
            // 0.10.5: warden / last guard model swaps broke player models on current CS2 builds; turned off unless the
            // owner picked a different model.
            Config.Update<Modules.Warden.WardenConfig>("Warden", w => { if (w.WardenModel == oldModel) w.WardenModel = ""; });
            Config.Update<Modules.LastGuard.LastGuardConfig>("LastGuard", l => { if (l.LastGuardModel == oldModel) l.LastGuardModel = ""; });
            Config.Update<GeneralConfig>("General", g => g.ConfigRevision = 1);
            Logger.LogInformation("Config migrated to revision 1: warden / last guard model swaps turned off (PlayerModels.ApplyModels is off by default)");
        }
    }

    public override void Unload(bool hotReload)
    {
        for (var i = _modules.Count - 1; i >= 0; i--)
            Safe(_modules[i], m => m.OnUnload(hotReload), "OnUnload");
        _modules.Clear();
        Round.KillRoundTimers();
        Glow.RemoveAll();
        Visibility.Reset();
        ChatInput.Reset();
        WeaponService.Clear();
        DamageService.Clear();
        Cvars.RestoreAll();
        Storage.Flush(false);
        JbEvents.ClearAll();
        Aim.Unload();
        Jb.Reset();
        foreach (var c in Players.Humans())
        {
            try { MenuService.Close(c, false); }
            catch { /* ignore */ }
        }

        Instance = null;
    }

    // ------------------------------------------------------------------ dispatch helpers

    private void Safe(JbModule m, Action<JbModule> action, string what)
    {
        try
        {
            action(m);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Module {Module} threw in {What}", m.Name, what);
        }
    }

    private void Dispatch(Action<JbModule> action, string what)
    {
        foreach (var m in _modules.ToArray()) Safe(m, action, what);
    }

    // ------------------------------------------------------------------ listeners

    private void OnMapStart(string map)
    {
        Round.Machine.ResetForMap();
        Round.KillRoundTimers();
        Hud.ForgetGameRules();
        Hud.ResetAll();
        Aim.Reset();
        ChatInput.Reset();
        Dispatch(m => m.OnMapStart(map), "OnMapStart");
    }

    private void OnMapEnd()
    {
        Dispatch(m => m.OnMapEnd(), "OnMapEnd");
        Round.KillRoundTimers();
        Round.Machine.ResetForMap();
        Cvars.RestoreAll();
        Storage.Flush();
    }

    private void OnClientPutInServer(int slot)
    {
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid || c.IsHLTV) return;
        Players.OnConnect(c);
        Hud.ResetSlot(slot);
        MenuService.ResetSlot(slot);
        var p = Players.Get(slot);
        Dispatch(m => m.OnPlayerConnected(p), "OnPlayerConnected");
    }

    private void OnClientDisconnect(int slot)
    {
        var p = Players.Get(slot);
        if (p.Connected) Dispatch(m => m.OnPlayerDisconnect(p), "OnPlayerDisconnect");
        Players.OnDisconnect(slot);
        Movement.Reset(slot);
        Aim.Forget(slot);
        ChatInput.Forget(slot);
        Hud.ResetSlot(slot);
        MenuService.ResetSlot(slot);
    }

    private void OnTick()
    {
        _tick++;
        Movement.Tick();
        Hud.KeepPanelSteady();
        if (_tick % 4 != 0) return; // 16 Hz HUD upkeep
        MenuService.Tick();
        foreach (var c in Players.Humans())
        {
            if (MenuService.HasMenu(c)) continue;
            Hud.Send(c, Hud.Compose(c));
        }
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        if (pressed != 0) MenuService.OnButtons(player, pressed);
    }

    // ------------------------------------------------------------------ events

    private HookResult OnRoundPrestart(EventRoundPrestart ev, GameEventInfo info)
    {
        Round.KillRoundTimers();
        Players.ResetRound();
        Movement.ResetAll();
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        var warmup = Round.IsWarmup;
        var target = warmup ? RoundPhase.Inactive
            : Round.CvarInt("mp_freezetime") > 0 ? RoundPhase.FreezeTime : RoundPhase.Active;
        if (Round.Phase == RoundPhase.Inactive && target == RoundPhase.Inactive)
        {
            DebugLog("Round: warmup round_start ignored");
            return HookResult.Continue;
        }

        if (!Round.Machine.TryTransition(target))
        {
            // A missed round_end (e.g. mp_restartgame) leaves us mid-round: close the old round first.
            if (target != RoundPhase.Inactive && Round.Machine.TryTransition(RoundPhase.Ended) && Round.Machine.TryTransition(target))
                DebugLog("Round: recovered from missing round_end");
            else
            {
                Logger.LogWarning("Round state: {Reason} - round_start ignored", Round.Machine.LastRejection);
                return HookResult.Continue;
            }
        }

        if (Round.Phase == RoundPhase.Inactive)
        {
            DebugLog("Round: warmup started - jailbreak round logic paused");
            return HookResult.Continue;
        }

        Round.StartTime = Server.CurrentTime;
        if (Round.Phase == RoundPhase.Active) Round.FreezeEndTime = Server.CurrentTime;
        Dispatch(m => m.OnRoundStart(), "OnRoundStart");
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd ev, GameEventInfo info)
    {
        if (!Round.Machine.TryTransition(RoundPhase.Active))
        {
            DebugLog("Round: " + Round.Machine.LastRejection);
            return HookResult.Continue;
        }

        Round.FreezeEndTime = Server.CurrentTime;
        Dispatch(m => m.OnRoundFreezeEnd(), "OnRoundFreezeEnd");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        if (!Round.Machine.TryTransition(RoundPhase.Ended))
        {
            DebugLog("Round: " + Round.Machine.LastRejection + " - duplicate round_end ignored");
            return HookResult.Continue;
        }

        var winner = (CsTeam)ev.Winner;
        var reason = ev.Reason;
        Dispatch(m => m.OnRoundEnd(winner, reason), "OnRoundEnd");
        Round.KillRoundTimers();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.IsHLTV) return HookResult.Continue;
        var slot = c.Slot;
        Server.NextFrame(() =>
        {
            var p = Players.Get(slot);
            var ctrl = p.Controller;
            if (ctrl == null || !p.Connected) return;
            if (ctrl.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) || !ctrl.IsAliveP()) return;
            Dispatch(m => m.OnPlayerSpawn(p), "OnPlayerSpawn");
        });
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        var victim = Players.Get(ev.Userid);
        if (victim == null) return HookResult.Continue;
        var attacker = Players.Get(ev.Attacker);
        Movement.Reset(victim.Slot);
        Dispatch(m => m.OnPlayerDeath(victim, attacker, ev), "OnPlayerDeath");
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo info)
    {
        var p = Players.Get(ev.Userid);
        if (p == null) return HookResult.Continue;
        var oldTeam = (CsTeam)ev.Oldteam;
        var newTeam = (CsTeam)ev.Team;
        var disconnect = ev.Disconnect;
        Dispatch(m => m.OnPlayerTeam(p, oldTeam, newTeam, disconnect), "OnPlayerTeam");
        return HookResult.Continue;
    }

    public void DebugLog(string message)
    {
        if (Config.General.Debug) Logger.LogInformation("[debug] {Message}", message);
    }

    // ------------------------------------------------------------------ commands

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Perms.HasFlag(player, "@css/config"))
        {
            Chat.Reply(player, info, Lang.T("core.no_access"));
            return;
        }

        // Config sections are re-read on next plugin load; phrases reload immediately.
        Lang.Load(ModuleDirectory, Config.General.Language);
        Chat.Reply(player, info, Lang.T("core.reloaded", Lang.Count));
        Chat.Reply(player, info, Lang.T("core.reload_hint"));
    }

    private void OnVersionCommand(CCSPlayerController? player, CommandInfo info)
    {
        Chat.Reply(player, info, $"{{green}}{ModuleName}{{default}} v{ModuleVersion} - created by {{gold}}Gdaki{{default}}, enhanced with Claude Code. Inspired by {{lightblue}}HlStriker{{default}}, SWBS development/lead admins.");
    }
}
