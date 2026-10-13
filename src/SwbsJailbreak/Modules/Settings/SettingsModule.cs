using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Settings;

/// <summary>Section "Settings" (UltJB_Settings.sp cvars + constants).</summary>
public sealed class SettingsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>mp_teamname_1 / mp_teamname_2 (forced by the original).</summary>
    public string TeamNameGuards { get; set; } = "Guards";
    public string TeamNamePrisoners { get; set; } = "Prisoners";

    /// <summary>healthshot_health (HEALTHSHOT_AMOUNT).</summary>
    public int HealthshotHealth { get; set; } = 25;

    /// <summary>Console commands that do nothing (original: kill, killvector, explode).</summary>
    public List<string> BlockedCommands { get; set; } = new() { "kill", "killvector", "explode", "explodevector" };

    /// <summary>ultjb_cells_closed_auto_respawn_seconds: late-joining prisoners respawn once while the cells are closed.</summary>
    public int CellsClosedAutoRespawnSeconds { get; set; } = 60;

    /// <summary>Spawn preparation: strip, stable spawn spot, loadout, armor, health.</summary>
    public bool PrepareSpawns { get; set; } = true;

    /// <summary>Respawning in the same round puts the player back on their first spawn spot of that round (their cell).</summary>
    public bool StableSpawnSpots { get; set; } = true;

    /// <summary>Guard loadout; the last entry is the weapon the guard holds.</summary>
    public List<string> GuardLoadout { get; set; } = new() { "weapon_knife", "weapon_healthshot", "weapon_fiveseven" };
    public List<string> PrisonerLoadout { get; set; } = new() { "weapon_knife_t" };
    public int GuardArmor { get; set; } = 100;
    public bool GuardHelmet { get; set; } = false;
    public int PrisonerArmor { get; set; } = 0;
    public int SpawnHealth { get; set; } = 100;

    /// <summary>In-cell weapon nerf: map weapons near prisoner spawns lose reserve ammo and are capped to 15 bullets.</summary>
    public bool NerfCellWeapons { get; set; } = true;
    public float CellWeaponRadius { get; set; } = 300f;   // PRISONER_SPAWN_DIST_FROM_WEAPON
    public int CellWeaponMaxClip { get; set; } = 15;      // MAX_IN_CELL_CLIP_SIZE
    public int CellWeaponNerfSeconds { get; set; } = 90;  // MODIFYING_WEAPON_EQUIP_TIME

    /// <summary>Weapons a guard drops by killing themself are deleted so prisoners cannot be armed that way.</summary>
    public bool WipeGuardSuicideWeapons { get; set; } = true;

    /// <summary>Guards hear no func_breakable / prop_breakable sounds (prisoners breaking vents).</summary>
    public bool BreakableSoundsPrisonersOnly { get; set; } = true;

    /// <summary>
    /// The original showed a full-screen overlay at round end. CS2 servers cannot set r_screenoverlay, so a centre
    /// banner and a short screen tint are used instead.
    /// </summary>
    public bool RoundEndBanner { get; set; } = true;
    public bool RoundEndTint { get; set; } = true;
    public string GuardsWinText { get; set; } = "<font class='fontSize-xl' color='#3FA9F5'>GUARDS WIN!</font>";
    public string PrisonersWinText { get; set; } = "<font class='fontSize-xl' color='#FF4D00'>PRISONERS WIN!</font>";

    /// <summary>Optional sound event names played to everyone (original: custom mp3s which CS2 cannot play without a workshop addon).</summary>
    public string GuardsWinSound { get; set; } = "";
    public string PrisonersWinSound { get; set; } = "";
    public string DrawSound { get; set; } = "";

    /// <summary>Hide the stock round_end event from clients (only together with custom win sounds).</summary>
    public bool BlockRoundEndBroadcast { get; set; } = false;

    /// <summary>ultjb_settings_help1_seconds / help2: play time below which a player counts as new (help model + bonus HP).</summary>
    public int Help1Seconds { get; set; } = 54000;
    public int Help2Seconds { get; set; } = 108000;
}

internal sealed class SettingsPlayerState : IRoundScoped
{
    public CsTeam SpawnTeam = CsTeam.None;
    public Vector? SpawnPos;
    public QAngle? SpawnAng;
    public float TimeOfDeath;
    public bool RespawnDenied;
    public int RespawnsUsed;
    public double ConnectedAt;

    public void ResetForRound()
    {
        SpawnTeam = CsTeam.None;
        SpawnPos = null;
        SpawnAng = null;
    }
}

/// <summary>
/// Port of UltJB_Settings.sp (v1.36): server rules, spawn preparation and loadout, late-join auto-respawn while the
/// cells are closed, in-cell weapon nerf, guard suicide weapon wipe, round-end feedback and the settings natives
/// (auto-respawn engine, BlockTerminateRound, next round-end reason, help number).
/// Noblock is applied by ServerTweaks (hlstriker_noblock) so it exists in one place only.
/// </summary>
public sealed class SettingsModule : JbModule, ISettingsService
{
    private SettingsConfig Cfg => Section<SettingsConfig>("Settings");

    public override string Name => "Settings";
    public override int Order => 6; // after Cells (5): round start reads the freshly resolved doors
    public override bool Enabled => Cfg.Enabled;

    private static readonly HashSet<string> GrenadeNames = new(StringComparer.Ordinal)
    {
        "weapon_hegrenade", "weapon_smokegrenade", "weapon_incgrenade", "weapon_decoy", "weapon_molotov", "weapon_tagrenade",
        "weapon_flashbang"
    };

    private readonly HashSet<uint> _cellWeapons = new();
    private float _roundStartTime;

    // auto-respawn engine
    private Timer? _respawnTimer;
    private float _respawnStarted;
    private int _respawnPerClient;
    private int _respawnSeconds;
    private RespawnTeam _respawnTeam = RespawnTeam.None;
    private bool _denyOnDeath;
    private bool _denyOnTeamChange;
    private float _delayPrisoners = 0.3f;
    private float _delayGuards = 0.3f;

    // round end reason override
    private bool _useNextReason;
    private RoundEndReason _nextReason = RoundEndReason.RoundDraw;
    private bool _terminateHooked;
    private bool _soundHooked;

    private static SettingsPlayerState S(JbPlayer p) => p.Data<SettingsPlayerState>();

    private static IEnumerable<JbPlayer> AllSlots() => Enumerable.Range(0, Players.MaxSlots).Select(Players.Get);

    // ================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.Settings = this;
        foreach (var cmd in Cfg.BlockedCommands.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            Plugin.AddCommandListener(cmd.Trim(), BlockCommand, HookMode.Pre);

        Plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEndPre, HookMode.Pre);
        Plugin.RegisterEventHandler<EventRoundStart>(OnRoundStartPre, HookMode.Pre);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
        Plugin.RegisterEventHandler<EventItemPickup>(OnItemPickup);
        JbEvents.CellsOpened += OnCellsOpened;
        // TerminateRound is hooked only while a day sets the next round-end reason (SetNextRoundEndReason), like the
        // CanAcquire hook in Weapons: no native detour stays on the hot path, and none is installed while the server boots.

        if (hotReload)
        {
            ApplyCvars();
            HookSounds();
            foreach (var p in Players.Connected()) S(p).ConnectedAt = Server.EngineTime;
        }
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.CellsOpened -= OnCellsOpened;
        StopAutoRespawning();
        UnhookTerminateRound();
        foreach (var p in Players.Connected()) SavePlayTime(p);
    }

    public override void OnMapStart(string mapName)
    {
        _cellWeapons.Clear();
        ReleaseNextRoundEndReason();
        foreach (var p in AllSlots()) S(p).ResetForRound();
        Server.NextFrame(() =>
        {
            ApplyCvars();
            BlockTerminateRound(false);
            HookSounds();
        });
    }

    public override void OnMapEnd()
    {
        StopAutoRespawning();
        BlockTerminateRound(false);
        foreach (var p in Players.Connected()) SavePlayTime(p);
    }

    private void ApplyCvars()
    {
        Cvars.Set("mp_teamname_1", Cfg.TeamNameGuards);
        Cvars.Set("mp_teamname_2", Cfg.TeamNamePrisoners);
        Cvars.Set("healthshot_health", Cfg.HealthshotHealth.ToString());
        if (ConVar.Find("sv_disable_immunity_alpha") != null) Cvars.Set("sv_disable_immunity_alpha", "1");
    }

    private static HookResult BlockCommand(CCSPlayerController? player, CommandInfo info) =>
        player == null ? HookResult.Continue : HookResult.Handled;

    // ================================================================== round flow

    private HookResult OnRoundStartPre(EventRoundStart ev, GameEventInfo info)
    {
        ReleaseNextRoundEndReason();
        return HookResult.Continue;
    }

    public override void OnRoundStart()
    {
        _roundStartTime = (float)Server.EngineTime;
        foreach (var p in AllSlots()) S(p).ResetForRound(); // cs_pre_restart reset of the stable spawn spots
        ApplyCvars();

        // Late prisoners (joined / left spectator) respawn once while the cells are closed. Uses the default 0.3 s delay
        // for both teams (the original could inherit a warday's delays - documented fix).
        if (Jb.Cells.DoExist && Cfg.CellsClosedAutoRespawnSeconds > 0)
        {
            StartAutoRespawning(true, 1, Cfg.CellsClosedAutoRespawnSeconds, RespawnTeam.Prisoners, true, true);
        }

        ScanCellWeapons();
    }

    private void OnCellsOpened()
    {
        // The cells-closed respawn window ends with the cells (custom day/LR respawns use their own Start call).
        if (_respawnTeam == RespawnTeam.Prisoners && _respawnPerClient == 1) StopAutoRespawning();
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        if (!Cfg.RoundEndBanner && !Cfg.RoundEndTint && Cfg.GuardsWinSound.Length == 0 && Cfg.PrisonersWinSound.Length == 0 &&
            Cfg.DrawSound.Length == 0) return;

        string text, sound;
        Color tint;
        switch (winner)
        {
            case CsTeam.CounterTerrorist:
                text = Cfg.GuardsWinText;
                sound = Cfg.GuardsWinSound;
                tint = Color.FromArgb(60, 0, 120, 255);
                break;
            case CsTeam.Terrorist:
                text = Cfg.PrisonersWinText;
                sound = Cfg.PrisonersWinSound;
                tint = Color.FromArgb(60, 255, 77, 0);
                break;
            default:
                text = "";
                sound = Cfg.DrawSound;
                tint = Color.Empty;
                break;
        }

        if (sound.Length > 0) Sound.PlayAll(sound);
        foreach (var c in Players.Humans())
        {
            if (Cfg.RoundEndBanner && text.Length > 0) Hud.Set(c, "settings.roundend", text, 90, 5);
            if (Cfg.RoundEndTint && tint != Color.Empty) Fx.Fade(c, tint, 0.5f, 1f, Fx.FadeIn | Fx.FadePurge);
        }
    }

    private HookResult OnRoundEndPre(EventRoundEnd ev, GameEventInfo info)
    {
        if (Cfg.BlockRoundEndBroadcast) info.DontBroadcast = true;
        return HookResult.Continue;
    }

    // ================================================================== spawn preparation

    public override void OnPlayerSpawn(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null || !c.IsAliveP() || c.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) return;
        if (!Cfg.PrepareSpawns)
        {
            JbEvents.RaiseSettingsSpawnPost(player);
            return;
        }

        var pawn = c.PawnOrNull();
        if (pawn == null) return;

        c.Strip(false);

        var s = S(player);
        if (Cfg.StableSpawnSpots)
        {
            if (s.SpawnTeam == CsTeam.None || s.SpawnPos == null)
            {
                if (pawn.AbsOrigin is { } o && pawn.AbsRotation is { } a)
                {
                    s.SpawnTeam = c.Team;
                    s.SpawnPos = new Vector(o.X, o.Y, o.Z);
                    s.SpawnAng = new QAngle(a.X, a.Y, a.Z);
                }
            }
            else if (s.SpawnTeam == c.Team)
            {
                c.TeleportTo(s.SpawnPos, s.SpawnAng, new Vector(0, 0, 0));
            }
        }

        var guard = c.Team == CsTeam.CounterTerrorist;
        foreach (var weapon in guard ? Cfg.GuardLoadout : Cfg.PrisonerLoadout)
        {
            if (string.IsNullOrWhiteSpace(weapon)) continue;
            if (Jb.WeaponSelection.GiveWeapon(player, weapon) == null) c.Give(weapon);
        }

        var active = (guard ? Cfg.GuardLoadout : Cfg.PrisonerLoadout).LastOrDefault();
        if (active != null) SelectWeapon(c, active);

        c.SetArmor(guard ? Cfg.GuardArmor : Cfg.PrisonerArmor, guard && Cfg.GuardHelmet);
        c.SetHp(Cfg.SpawnHealth);
        if (pawn.ItemServices != null)
        {
            var items = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            if (items.HasDefuser)
            {
                items.HasDefuser = false;
                Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pItemServices");
            }
        }

        Movement.Reset(c.Slot);
        JbEvents.RaiseSettingsSpawnPost(player);
    }

    /// <summary>Best effort: CS2 has no reliable server-side "make active", the client switches on the slot command.</summary>
    private static void SelectWeapon(CCSPlayerController c, string weapon)
    {
        var slot = Weapons.IsKnife(weapon) ? "slot3" : Weapons.IsPistol(weapon) ? "slot2" : Weapons.IsGrenade(weapon) ? "slot4" : "slot1";
        Server.NextFrame(() =>
        {
            if (c.IsValid && c.IsAliveP()) c.ExecuteClientCommand(slot);
        });
    }

    // ================================================================== in-cell weapon nerf

    private void ScanCellWeapons()
    {
        _cellWeapons.Clear();
        if (!Cfg.NerfCellWeapons) return;
        var spawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Select(s => s.AbsOrigin).Where(o => o != null).Select(o => new Vector(o!.X, o.Y, o.Z)).ToList();
        if (spawns.Count == 0) return;
        foreach (var w in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>("weapon_"))
        {
            if (!w.IsValid || GrenadeNames.Contains(w.DesignerName) || w.OwnerEntity.IsValid) continue;
            var o = w.AbsOrigin;
            if (o == null) continue;
            if (spawns.Any(sp => VectorUtil.Distance(sp, o) <= Cfg.CellWeaponRadius)) _cellWeapons.Add(w.EntityHandle.Raw);
        }

        if (_cellWeapons.Count > 0) Logger.LogDebug("[Settings] {Count} map weapons near prisoner spawns will be nerfed", _cellWeapons.Count);
    }

    private HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info)
    {
        if (_cellWeapons.Count == 0 || Jb.Cells.HaveOpened) return HookResult.Continue;
        if ((float)Server.EngineTime - _roundStartTime > Cfg.CellWeaponNerfSeconds) return HookResult.Continue;
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.Team != CsTeam.Terrorist) return HookResult.Continue;
        Server.NextFrame(() =>
        {
            if (!c.IsValid) return;
            foreach (var w in c.Weapons())
            {
                var raw = w.EntityHandle.Raw;
                if (!_cellWeapons.Remove(raw)) continue;
                w.Clip1 = Math.Min(w.Clip1, Math.Max(1, Cfg.CellWeaponMaxClip));
                w.ReserveAmmo[0] = 0;
                w.ReserveAmmo[1] = 0;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
            }
        });
        return HookResult.Continue;
    }

    // ================================================================== deaths: suicide wipe + respawn denial

    private HookResult OnDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        var victim = Players.Get(ev.Userid);
        if (victim != null) S(victim).TimeOfDeath = (float)Server.EngineTime;
        return HookResult.Continue;
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (_respawnTimer != null && _denyOnDeath) S(victim).RespawnDenied = true;

        var c = victim.Controller;
        if (!Cfg.WipeGuardSuicideWeapons || c == null || c.Team != CsTeam.CounterTerrorist) return;
        if (attacker != null && attacker.Slot != victim.Slot) return; // world/fall damage counts as suicide too (attacker 0)
        var pawnRaw = c.PlayerPawn.Raw;
        var died = Server.CurrentTime;
        Server.NextFrame(() =>
        {
            foreach (var w in Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>("weapon_"))
            {
                try
                {
                    if (!w.IsValid || w.OwnerEntity.IsValid) continue;
                    if (w.PrevOwner.Raw != pawnRaw || w.DroppedAtTime < died - 0.5f) continue;
                    w.AcceptInput("Kill");
                }
                catch
                {
                    // entity going away
                }
            }
        });
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (_respawnTimer != null && _denyOnTeamChange && oldTeam >= CsTeam.Terrorist) S(player).RespawnDenied = true;
    }

    // ================================================================== auto-respawn engine

    public bool IsAutoRespawning => _respawnTimer != null;

    public void StartAutoRespawning(bool resetRespawnLimits = false, int allowedRespawnsPerPlayer = 0, int secondsToRespawnFor = 0,
        RespawnTeam team = RespawnTeam.Both, bool disableRespawnOnDeath = false, bool disableRespawnOnTeamChange = true)
    {
        StopAutoRespawning();
        if (resetRespawnLimits)
            foreach (var p in AllSlots())
            {
                S(p).RespawnDenied = false;
                S(p).RespawnsUsed = 0;
            }

        _respawnPerClient = allowedRespawnsPerPlayer;
        _respawnSeconds = secondsToRespawnFor;
        _respawnTeam = team;
        _denyOnDeath = disableRespawnOnDeath;
        _denyOnTeamChange = disableRespawnOnTeamChange;
        _delayPrisoners = 0.3f;
        _delayGuards = 0.3f;
        _respawnStarted = (float)Server.EngineTime;
        _respawnTimer = Round.Timer(0.1f, RespawnTick, repeat: true);
    }

    public void StopAutoRespawning()
    {
        try { _respawnTimer?.Kill(); }
        catch { /* already stopped */ }
        _respawnTimer = null;
        _respawnTeam = RespawnTeam.None;
    }

    public void SetAutoRespawnDelay(float delay, RespawnTeam team = RespawnTeam.Both)
    {
        delay = Math.Max(0.1f, delay);
        if (team is RespawnTeam.Both or RespawnTeam.Prisoners) _delayPrisoners = delay;
        if (team is RespawnTeam.Both or RespawnTeam.Guards) _delayGuards = delay;
    }

    private void RespawnTick()
    {
        var now = (float)Server.EngineTime;
        if (_respawnSeconds > 0 && now - _respawnStarted >= _respawnSeconds)
        {
            StopAutoRespawning();
            return;
        }

        if (_respawnTeam == RespawnTeam.None || Round.Phase is not (RoundPhase.Active or RoundPhase.FreezeTime)) return;
        foreach (var p in Players.Connected())
        {
            var c = p.Controller;
            if (c == null || c.IsAliveP() || c.Team < CsTeam.Terrorist) continue;
            var s = S(p);
            if (s.RespawnDenied) continue;
            var guard = c.Team == CsTeam.CounterTerrorist;
            if (_respawnTeam == RespawnTeam.Prisoners && guard || _respawnTeam == RespawnTeam.Guards && !guard) continue;
            if (s.TimeOfDeath > 0 && now - s.TimeOfDeath < (guard ? _delayGuards : _delayPrisoners)) continue;
            if (!c.PlayerPawn.IsValid) continue; // never picked a team model yet
            c.Respawn();
            s.RespawnsUsed++;
            if (_respawnPerClient > 0 && s.RespawnsUsed >= _respawnPerClient) s.RespawnDenied = true;
        }
    }

    // ================================================================== round control natives

    public void BlockTerminateRound(bool block) => Cvars.Set("mp_ignore_round_win_conditions", block ? "1" : "0");

    public bool IsBlockingTerminateRound
    {
        get
        {
            try { return ConVar.Find("mp_ignore_round_win_conditions")?.GetPrimitiveValue<bool>() ?? false; }
            catch { return false; }
        }
    }

    public void SetNextRoundEndReason(bool useReason, RoundEndReason reason = RoundEndReason.RoundDraw)
    {
        _nextReason = reason;
        if (!useReason)
        {
            ReleaseNextRoundEndReason();
            return;
        }

        _useNextReason = true;
        if (!_terminateHooked) HookTerminateRound();
    }

    /// <summary>Stops overriding the round-end reason; the detour is removed next frame, never from inside the hooked call
    /// (round_end handlers run within TerminateRound).</summary>
    private void ReleaseNextRoundEndReason()
    {
        _useNextReason = false;
        if (!_terminateHooked) return;
        Server.NextFrame(() =>
        {
            if (!_useNextReason) UnhookTerminateRound();
        });
    }

    private void HookTerminateRound()
    {
        try
        {
            if (OperatingSystem.IsWindows()) VirtualFunctions.TerminateRoundFuncWindows.Hook(OnTerminateRound, HookMode.Pre);
            else VirtualFunctions.TerminateRoundFuncLinux.Hook(OnTerminateRound, HookMode.Pre);
            _terminateHooked = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Settings] TerminateRound hook unavailable - next round-end reason override disabled");
        }
    }

    private void UnhookTerminateRound()
    {
        if (!_terminateHooked) return;
        try
        {
            if (OperatingSystem.IsWindows()) VirtualFunctions.TerminateRoundFuncWindows.Unhook(OnTerminateRound, HookMode.Pre);
            else VirtualFunctions.TerminateRoundFuncLinux.Unhook(OnTerminateRound, HookMode.Pre);
        }
        catch
        {
            // shutting down
        }

        _terminateHooked = false;
    }

    private HookResult OnTerminateRound(DynamicHook hook)
    {
        if (!_useNextReason) return HookResult.Continue;
        // Linux: (this, reason, delay, ...)  Windows: (this, delay, reason, ...)
        hook.SetParam(OperatingSystem.IsWindows() ? 2 : 1, _nextReason);
        return HookResult.Changed;
    }

    public void StripWeaponFromOwner(CBasePlayerWeapon weapon)
    {
        if (weapon == null || !weapon.IsValid) return;
        try
        {
            var owner = weapon.OwnerEntity.Value?.As<CCSPlayerPawn>();
            var ws = owner?.WeaponServices;
            if (ws != null && owner!.IsValid)
            {
                if (ws.ActiveWeapon.Raw == weapon.EntityHandle.Raw) ws.ActiveWeapon.Raw = uint.MaxValue;
                weapon.OwnerEntity.Raw = uint.MaxValue;
            }

            weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Settings] StripWeaponFromOwner failed");
        }
    }

    // ================================================================== help number (play time)

    /// <summary>
    /// 1 below Help1Seconds of play time, 2 below Help2Seconds, else 0. The original read DatabaseUserStats; the port
    /// tracks play time on this server in the plugin's JSON storage (counted from when this plugin was installed).
    /// </summary>
    public int GetHelpNumber(JbPlayer player)
    {
        if (player.IsBot || player.SteamId64 == 0) return 0;
        var played = Storage.Get<long>("playtime", player.SteamId64.ToString()) + (long)(Server.EngineTime - S(player).ConnectedAt);
        if (Cfg.Help1Seconds > 0 && played < Cfg.Help1Seconds) return 1;
        if (Cfg.Help2Seconds > 0 && played < Cfg.Help2Seconds) return 2;
        return 0;
    }

    public override void OnPlayerConnected(JbPlayer player) => S(player).ConnectedAt = Server.EngineTime;

    public override void OnPlayerDisconnect(JbPlayer player) => SavePlayTime(player);

    private static void SavePlayTime(JbPlayer p)
    {
        if (p.IsBot || p.SteamId64 == 0) return;
        var s = S(p);
        var now = Server.EngineTime;
        var add = (long)Math.Max(0, now - s.ConnectedAt);
        s.ConnectedAt = now;
        if (add <= 0) return;
        var key = p.SteamId64.ToString();
        Storage.Set("playtime", key, Storage.Get<long>("playtime", key) + add);
    }

    // ================================================================== breakable sound filter

    private void HookSounds()
    {
        if (_soundHooked || !Cfg.BreakableSoundsPrisonersOnly) return;
        try
        {
            var id = UserMessage.FindIdByName("CMsgSosStartSoundEvent");
            if (id <= 0) return;
            Plugin.HookUserMessage(id, OnSoundEvent, HookMode.Pre);
            _soundHooked = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Settings] could not hook sound events - breakable sounds are heard by everyone");
        }
    }

    private HookResult OnSoundEvent(UserMessage msg)
    {
        try
        {
            var idx = msg.ReadInt("source_entity_index");
            if (idx <= 0) return HookResult.Continue;
            var ent = Utilities.GetEntityFromIndex<CBaseEntity>(idx);
            if (ent == null || !ent.IsValid || ent.DesignerName is not ("func_breakable" or "prop_breakable")) return HookResult.Continue;
            foreach (var c in msg.Recipients.ToList())
                if (c.IsValid && c.Team == CsTeam.CounterTerrorist)
                    msg.Recipients.Remove(c);
        }
        catch
        {
            // field layout changed - never break sounds
        }

        return HookResult.Continue;
    }
}
