using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "CaptureTheFlag" (ultjb_ctf_* cvars).</summary>
public sealed class CtfConfig
{
    public bool CanDefendersReturnFlag { get; set; } = false; // ultjb_ctf_can_defenders_return_flag
    public int HpPerClientTeamDiff { get; set; } = 25;        // ultjb_ctf_hp_per_client_team_diff
    public float RespawnDelayAttackers { get; set; } = 2.5f;  // ultjb_ctf_respawn_delay_attackers
    public float RespawnDelayDefenders { get; set; } = 10f;   // ultjb_ctf_respawn_delay_defenders
    public float SpeedForAttackers { get; set; } = 1.2f;      // ultjb_ctf_speed_for_attackers
    public string PickupSound { get; set; } = "UIPanorama.popup_accept_match_beep";
    public string DropSound { get; set; } = "UIPanorama.round_report_round_lost";
    public string ReturnSound { get; set; } = "UIPanorama.generic_button_press";
    public string CaptureSound { get; set; } = "UIPanorama.round_report_round_won";
}

/// <summary>Per-map CTF points (original ZoneManager teleport zones "ctf_flag" and "ctf_capture").</summary>
public sealed class CtfMapPoints
{
    public float[]? Flag { get; set; }    // flag spawn + defenders' spawn
    public float[]? Capture { get; set; } // capture point + attackers' spawn
}

/// <summary>
/// Port of Days/UltJB_Warday_CaptureTheFlag.sp: the warden picks attack/defend for their team (10 s), defenders guard the
/// flag at "ctf_flag", attackers carry it to "ctf_capture". Unlimited respawns (defenders 10 s, attackers 2.5 s), team
/// HP balance, flag drop (G / death), auto return after 15 s, defenders win when the round time runs out.
/// CS2: the Swoobles flag / capture-point models cannot load, so the flag is a team-coloured beam flag (pole + banner + ring)
/// and the capture point a green beam ring; both are real entities. The two map points are set with !ctf_setflag and
/// !ctf_setcapture (the day is disabled on maps without them, like the original without zones).
/// </summary>
public sealed class CaptureTheFlagDay : JbModule
{
    private CtfConfig Cfg => Section<CtfConfig>("CaptureTheFlag");

    public override string Name => "Days.CaptureTheFlag";
    public override int Order => 12;

    private const float PickupDelay = 0.4f, PickupDelayPerClient = 1f, ReturnDelay = 0.75f, AutoReturn = 15f;

    private DayDefinition? _day;
    private CtfMapPoints _points = new();
    private string _map = "";

    private bool _started;
    private CsTeam _defending = CsTeam.None;
    private JbMenu? _sideMenu;
    private Timer? _selectionTimer, _logicTimer;
    private float _roundStart, _roundTime;

    // flag state
    private bool _flagAtSpawn = true;
    private int _carrierSlot = -1;
    private Vector? _flagPos;
    private float _droppedTime;
    private readonly float[] _droppedPerClient = new float[Players.MaxSlots];
    private readonly List<CBeam> _flagBeams = new();
    private List<CBeam> _flagRing = new();
    private List<CBeam> _captureRing = new();

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule) return;
        _day = new DayDefinition
        {
            Name = "Capture the Flag", Type = DayType.Warday,
            Flags = DayFlags.StripGuardsWeapons | DayFlags.StripPrisonersWeapons,
            OnStart = OnDayStart,
            OnEnd = _ => OnDayEnd()
        };
        Jb.Days.Register(_day);
        _day.FreezeTime = 10; // SIDE_SELECTION_TIME
        _day.FreezeTeams = FreezeTeams.Guards | FreezeTeams.Prisoners;
        _map = Server.MapName;
        LoadPoints();
        JbEvents.DaySpawnPost += OnDaySpawn;
        Plugin.AddCommandListener("drop", OnDrop, HookMode.Pre);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.DaySpawnPost -= OnDaySpawn;
        OnDayEnd();
    }

    public override void OnMapStart(string mapName)
    {
        _map = mapName;
        LoadPoints();
    }

    public override void OnRoundStart()
    {
        ApplyZonePoints(); // zones edited during the map apply from the next round
        _roundStart = (float)Server.EngineTime;
        var rt = ConVar.Find("mp_roundtime")?.GetPrimitiveValue<float>() ?? 5f;
        _roundTime = rt * 60f + 3f;
        if (_day != null) _day.Enabled = _points.Flag != null && _points.Capture != null && _day.Enabled;
    }

    // ================================================================== map points

    private void LoadPoints()
    {
        try { _points = Storage.LoadMapDoc<CtfMapPoints>("ctf", (_map ?? "").ToLowerInvariant()); }
        catch { _points = new CtfMapPoints(); }
        ApplyZonePoints();
        // Disabled without both points (original: without both zones); never re-enables a day disabled with !daysedit.
        if (_day != null) _day.Enabled = _day.Enabled && _points.Flag != null && _points.Capture != null;
    }

    [ConsoleCommand("css_ctf_setflag", "Save your position as this map's CTF flag / defender spawn")]
    public void CmdSetFlag(CCSPlayerController? caller, CommandInfo info) => SetPoint(caller, true);

    [ConsoleCommand("css_ctf_setcapture", "Save your position as this map's CTF capture point / attacker spawn")]
    public void CmdSetCapture(CCSPlayerController? caller, CommandInfo info) => SetPoint(caller, false);

    private void SetPoint(CCSPlayerController? caller, bool flag)
    {
        if (caller == null || !Perms.Has(caller, "zones")) { if (caller != null) Chat.Tell(caller, "core.no_access"); return; }
        var pawn = caller.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        var o = pawn.AbsOrigin;
        var pt = new[] { MathF.Round(o.X, 1), MathF.Round(o.Y, 1), MathF.Round(o.Z, 1), MathF.Round(pawn.EyeAngles.Y, 1) };
        if (flag) _points.Flag = pt;
        else _points.Capture = pt;
        Storage.SaveMapDoc("ctf", _map.ToLowerInvariant(), _points);
        if (_day != null) _day.Enabled = _points.Flag != null && _points.Capture != null;
        Chat.Tell(caller, flag ? "days.ctf.flag_set" : "days.ctf.capture_set");
    }

    private static Vector? ToVector(float[]? p) => p is { Length: >= 3 } ? new Vector(p[0], p[1], p[2]) : null;

    // ================================================================== day flow

    private void OnDayStart(JbPlayer? starter)
    {
        Cvars.SetTemp("mp_respawn_immunitytime", "1");
        _defending = CsTeam.None;
        _started = false;
        var c = starter?.Controller;
        if (c == null)
        {
            SelectRandomSide(null);
        }
        else
        {
            var m = new JbMenu(Lang.T("days.ctf.side_title")) { ExitButton = false, Timeout = -1 };
            m.Add(Lang.T("days.ctf.attack"), x => SelectSide(x, attack: true));
            m.Add(Lang.T("days.ctf.defend"), x => SelectSide(x, attack: false));
            m.Add(Lang.T("days.ctf.random"), x => SelectRandomSide(x));
            _sideMenu = m;
            m.Open(c);
        }

        Kill(ref _selectionTimer);
        _selectionTimer = Plugin.AddTimer(10f, () =>
        {
            _selectionTimer = null;
            if (_sideMenu != null && c is { IsValid: true } && ReferenceEquals(MenuService.Current(c), _sideMenu)) MenuService.Close(c, false);
            _sideMenu = null;
            SelectRandomSide(c);
            StartGame();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SelectRandomSide(CCSPlayerController? c)
    {
        if (_defending != CsTeam.None) return;
        if (c != null) c.PrintToChat(" " + Lang.T("days.ctf.random_side"));
        SelectSide(c, Random.Shared.Next(2) == 0);
    }

    private void SelectSide(CCSPlayerController? c, bool attack)
    {
        if (_defending != CsTeam.None) return;
        var team = c?.Team ?? CsTeam.None;
        if (team < CsTeam.Terrorist)
        {
            _defending = Random.Shared.Next(2) == 0 ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            return;
        }

        _defending = attack ? (team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist) : team;
    }

    private void StartGame()
    {
        if (!Active || _points.Flag == null || _points.Capture == null) return;
        _carrierSlot = -1;
        _flagAtSpawn = true;
        _flagPos = ToVector(_points.Flag);
        DrawCaptureRing();
        foreach (var p in DayUtil.AliveAll()) Prepare(p);
        _started = true;
        var settings = Jb.Settings;
        settings.SetNextRoundEndReason(true, _defending == CsTeam.CounterTerrorist ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin);
        settings.BlockTerminateRound(true);
        settings.StartAutoRespawning(true);
        settings.SetAutoRespawnDelay(Cfg.RespawnDelayDefenders, _defending == CsTeam.CounterTerrorist ? RespawnTeam.Guards : RespawnTeam.Prisoners);
        settings.SetAutoRespawnDelay(Cfg.RespawnDelayAttackers, _defending == CsTeam.CounterTerrorist ? RespawnTeam.Prisoners : RespawnTeam.Guards);
        Kill(ref _logicTimer);
        _logicTimer = Plugin.AddTimer(0.1f, Logic, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnDayEnd()
    {
        Cvars.Restore("mp_respawn_immunitytime");
        var wasStarted = _started;
        _started = false;
        Kill(ref _selectionTimer);
        Kill(ref _logicTimer);
        _sideMenu = null;
        RemoveFlagVisuals();
        foreach (var b in _captureRing) Fx.SafeRemove(b);
        _captureRing.Clear();
        if (wasStarted)
        {
            Jb.Settings.StopAutoRespawning();
            Jb.Settings.BlockTerminateRound(false);
            Jb.Settings.SetNextRoundEndReason(false); // an admin-ended CTF must not decide the round's winner later
        }

        Movement.ClearKeyAll("day.ctf");
    }

    private static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    private void OnDaySpawn(JbPlayer p)
    {
        if (Active && _started) Prepare(p);
    }

    private static bool IsFreeday(JbPlayer p) =>
        Jb.LastRequests.HasStartedLastRequest(p) && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    /// <summary>
    /// The original used Teleport Destination zones named "ctf_flag" / "ctf_capture"; when such zones exist they win over
    /// the !ctf_setflag / !ctf_setcapture points (floor centre of the zone, facing its yaw).
    /// </summary>
    private void ApplyZonePoints()
    {
        if (Jb.Zones.FindByName(ZoneTypeKeys.TeleportDestination, "ctf_flag") is { } flag) _points.Flag = ZonePoint(flag);
        if (Jb.Zones.FindByName(ZoneTypeKeys.TeleportDestination, "ctf_capture") is { } capture) _points.Capture = ZonePoint(capture);
    }

    private static float[] ZonePoint(Zone z)
    {
        var p = z.Box.BottomCenter;
        return new[] { p.X, p.Y, p.Z, z.Angles.Y };
    }

    private void Prepare(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        if (c.Team == CsTeam.CounterTerrorist) DayUtil.Give(p, "weapon_knife");
        else
        {
            if (IsFreeday(p)) return;
            DayUtil.Give(p, "weapon_knife_t");
        }

        string main;
        if (c.Team == _defending)
        {
            DayUtil.Give(p, "weapon_deagle");
            main = Random.Shared.Next(2) == 0 ? "weapon_m4a1" : "weapon_ak47";
            TeleportTo(c, _points.Flag);
            Chat.Tell(p, "days.ctf.you_defend");
        }
        else
        {
            DayUtil.Give(p, "weapon_glock");
            main = new[] { "weapon_mp5sd", "weapon_ump45", "weapon_bizon", "weapon_p90", "weapon_mp9", "weapon_mac10" }[Random.Shared.Next(6)];
            TeleportTo(c, _points.Capture);
            Movement.SetSpeed(c, "day.ctf", Cfg.SpeedForAttackers);
            Chat.Tell(p, "days.ctf.you_attack");
        }

        DayUtil.Give(p, main, true);
        c.SetArmor(100, true);
        SetSpawnHealth(c);
    }

    private static void TeleportTo(CCSPlayerController c, float[]? pt)
    {
        var v = ToVector(pt);
        if (v == null) return;
        // Small spread so respawning players do not all stand on one spot (players do not collide anyway).
        var dx = Random.Shared.NextSingle() * 64f - 32f;
        var dy = Random.Shared.NextSingle() * 64f - 32f;
        c.TeleportTo(new Vector(v.X + dx, v.Y + dy, v.Z + 4f), new QAngle(0, pt!.Length >= 4 ? pt[3] : 0f, 0), new Vector(0, 0, 0));
    }

    private void SetSpawnHealth(CCSPlayerController c)
    {
        int guards = 0, prisoners = 0;
        foreach (var x in Players.All())
        {
            if (x.Team == CsTeam.CounterTerrorist) guards++;
            else if (x.Team == CsTeam.Terrorist && !(x.Jb() is { } jp && IsFreeday(jp))) prisoners++;
        }

        var hp = 100;
        if (prisoners > guards && c.Team == CsTeam.CounterTerrorist) hp += (prisoners - guards) * Cfg.HpPerClientTeamDiff;
        else if (guards >= prisoners && c.Team == CsTeam.Terrorist) hp += (guards - prisoners) * Cfg.HpPerClientTeamDiff;
        c.SetMaxHp(hp);
        c.SetHp(hp, false);
    }

    // ================================================================== flag logic

    private CsTeam FlagOwner => _defending;

    private void Logic()
    {
        if (!_started)
        {
            Kill(ref _logicTimer);
            return;
        }

        var now = (float)Server.EngineTime;
        if (now - _roundStart > _roundTime)
        {
            Kill(ref _logicTimer);
            EndGame(timeExpired: true);
            HintAll(Lang.T("days.ctf.defended", _defending == CsTeam.CounterTerrorist ? "257edd" : "ddaf25",
                Lang.T(_defending == CsTeam.CounterTerrorist ? "days.ctf.guards" : "days.ctf.prisoners")));
            return;
        }

        var carrier = _carrierSlot >= 0 ? Players.Get(_carrierSlot).Controller : null;
        if (carrier != null && (!carrier.IsValid || !carrier.IsAliveP()))
        {
            SetCarrier(null, fromDeath: true);
            carrier = null;
        }

        if (carrier != null)
        {
            var o = carrier.PawnOrNull()?.AbsOrigin;
            if (o != null) _flagPos = new Vector(o.X, o.Y, o.Z + 32f);
            DrawFlag(_flagPos!, carrier.Team);
            HintAll(Lang.T("days.ctf.carried", carrier.Team == CsTeam.CounterTerrorist ? "257edd" : "ddaf25", Hud.Escape(carrier.PlayerName)));
            // Capture: the carrier reached the capture point.
            var cap = ToVector(_points.Capture);
            if (cap != null && o != null && Math.Abs(o.X - cap.X) <= 40f && Math.Abs(o.Y - cap.Y) <= 40f && Math.Abs(o.Z - cap.Z) <= 64f)
            {
                EndGame(timeExpired: false);
                carrier.Score += 1000;
                Utilities.SetStateChanged(carrier, "CCSPlayerController", "m_iScore");
                if (Cfg.CaptureSound.Length > 0) Sound.PlayAll(Cfg.CaptureSound, 0.9f);
                HintAll(Lang.T("days.ctf.captured", carrier.Team == CsTeam.CounterTerrorist ? "257edd" : "ddaf25", Hud.Escape(carrier.PlayerName)));
            }

            return;
        }

        if (_flagPos != null) DrawFlag(_flagPos, FlagOwner);
        TouchFlag(now);
        if (_flagAtSpawn || !_started) return;
        var left = (int)Math.Ceiling(_droppedTime + AutoReturn - now);
        if (left < 1)
        {
            ReturnFlag();
            HintAll(Lang.T("days.ctf.returned"));
            if (Cfg.ReturnSound.Length > 0) Sound.PlayAll(Cfg.ReturnSound);
            return;
        }

        HintAll(Lang.T("days.ctf.dropped", left));
    }

    private static void HintAll(string html)
    {
        foreach (var c in Players.Humans()) Hud.Set(c, "days.ctf", html, 55, 0.5f);
    }

    private void TouchFlag(float now)
    {
        if (_flagPos == null) return;
        foreach (var c in Players.Alive())
        {
            var p = c.Jb();
            var o = c.PawnOrNull()?.AbsOrigin;
            if (p == null || o == null || IsFreeday(p)) continue;
            // original flag trigger 24 x 24 x 40 units (+ player hull)
            if (Math.Abs(o.X - _flagPos.X) > 28f || Math.Abs(o.Y - _flagPos.Y) > 28f || o.Z < _flagPos.Z - 72f || o.Z > _flagPos.Z + 40f) continue;
            if (Cfg.CanDefendersReturnFlag && c.Team == FlagOwner && !_flagAtSpawn && now >= _droppedTime + ReturnDelay)
            {
                ReturnFlag();
                HintAll(Lang.T("days.ctf.returned_by", c.Team == CsTeam.CounterTerrorist ? "257edd" : "ddaf25", Hud.Escape(c.PlayerName)));
                if (Cfg.ReturnSound.Length > 0) Sound.PlayAll(Cfg.ReturnSound);
                return;
            }

            if (c.Team < CsTeam.Terrorist || c.Team == FlagOwner) continue;
            if (now < _droppedTime + PickupDelay || now < _droppedPerClient[c.Slot] + PickupDelayPerClient) continue;
            SetCarrier(c, false);
            return;
        }
    }

    private void SetCarrier(CCSPlayerController? c, bool fromDeath)
    {
        var prev = _carrierSlot >= 0 ? Players.Get(_carrierSlot).Controller : null;
        if (c != null)
        {
            _carrierSlot = c.Slot;
            _flagAtSpawn = false;
            if (Cfg.PickupSound.Length > 0) Sound.PlayAll(Cfg.PickupSound, 0.85f);
            return;
        }

        _carrierSlot = -1;
        if (prev == null) return;
        var pawn = prev.PawnOrNull();
        var o = pawn?.AbsOrigin ?? _flagPos;
        if (o != null)
        {
            if (fromDeath || pawn == null)
            {
                _flagPos = new Vector(o.X, o.Y, o.Z);
            }
            else
            {
                // Thrown forward like the original drop (325 u/s), landing ~64 units ahead.
                var fwd = VectorUtil.Forward(new QAngle(0, pawn.EyeAngles.Y, 0));
                _flagPos = new Vector(o.X + fwd.X * 64f, o.Y + fwd.Y * 64f, o.Z);
            }
        }

        _droppedTime = (float)Server.EngineTime;
        if (prev.IsValid) _droppedPerClient[prev.Slot] = _droppedTime;
        if (Cfg.DropSound.Length > 0) Sound.PlayAll(Cfg.DropSound, 0.85f);
    }

    private void ReturnFlag()
    {
        _carrierSlot = -1;
        _flagPos = ToVector(_points.Flag);
        _flagAtSpawn = true;
    }

    private void EndGame(bool timeExpired)
    {
        _started = false;
        var winner = timeExpired ? _defending : _defending == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var reason = winner == CsTeam.CounterTerrorist ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin;
        Jb.Settings.SetNextRoundEndReason(true, reason);
        Round.End(reason, 5f);
        Jb.Settings.BlockTerminateRound(false);
        RemoveFlagVisuals();
        Kill(ref _logicTimer);
    }

    private HookResult OnDrop(CCSPlayerController? player, CommandInfo info)
    {
        if (!_started || player == null || player.Slot != _carrierSlot) return HookResult.Continue;
        SetCarrier(null, false);
        return HookResult.Handled;
    }

    private HookResult OnDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        if (_started && ev.Userid is { } v && v.Slot == _carrierSlot) SetCarrier(null, true);
        return HookResult.Continue;
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (_started && player.Slot == _carrierSlot) SetCarrier(null, true);
    }

    // ================================================================== visuals (real beams)

    private void DrawFlag(Vector at, CsTeam team)
    {
        var color = team == CsTeam.CounterTerrorist ? Color.FromArgb(255, 37, 126, 221) : Color.FromArgb(255, 221, 175, 37);
        var bottom = new Vector(at.X, at.Y, at.Z);
        var top = new Vector(at.X, at.Y, at.Z + 64f);
        var b1 = new Vector(at.X + 24f, at.Y, at.Z + 56f);
        var b2 = new Vector(at.X, at.Y, at.Z + 48f);
        if (_flagBeams.Count != 3 || _flagBeams.Any(b => !b.IsValid))
        {
            RemoveFlagVisuals();
            foreach (var b in new[] { Fx.Beam(bottom, top, Color.White, 2f), Fx.Beam(top, b1, color, 6f), Fx.Beam(b1, b2, color, 6f) })
                if (b != null) _flagBeams.Add(b);
            _flagRing = Fx.Ring(new Vector(at.X, at.Y, at.Z + 2f), 24f, color, 2f, 0f, 12);
            return;
        }

        Fx.MoveBeam(_flagBeams[0], bottom, top);
        Fx.MoveBeam(_flagBeams[1], top, b1);
        Fx.MoveBeam(_flagBeams[2], b1, b2);
        foreach (var b in _flagBeams.Skip(1)) Fx.SetBeamColor(b, color);
        Fx.MoveRing(_flagRing, new Vector(at.X, at.Y, at.Z + 2f), 24f);
    }

    private void RemoveFlagVisuals()
    {
        foreach (var b in _flagBeams) Fx.SafeRemove(b);
        _flagBeams.Clear();
        foreach (var b in _flagRing) Fx.SafeRemove(b);
        _flagRing.Clear();
    }

    private void DrawCaptureRing()
    {
        foreach (var b in _captureRing) Fx.SafeRemove(b);
        var cap = ToVector(_points.Capture);
        _captureRing = cap == null ? new List<CBeam>() : Fx.Ring(new Vector(cap.X, cap.Y, cap.Z + 4f), 40f, Color.Lime, 4f, 0f, 20);
    }
}
