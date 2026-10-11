using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Games;

/// <summary>
/// Small helpers from hlstriker's SMPlugins and its contributors, limited so they never help in normal jailbreak play:
/// <c>!hide</c> (HidePlayers), the Auto Bhop zone type (MinigamesAutoBhop) and <c>!measure</c> / <c>!gap</c> (MeasureGap by
/// Hymns For Disco). Design: docs/design/MINIGAMES_SUITE_DESIGN.md §6.
/// </summary>
public sealed class GameHelpersModule : JbModule
{
    public override string Name => "Games.Helpers";
    public override int Order => 17;

    private readonly bool[] _hide = new bool[Players.MaxSlots];
    private readonly Dictionary<int, (Vector3? A, CBeam? Beam)> _measure = new();

    public override void OnLoad(bool hotReload)
    {
        Jb.Zones.RegisterType(new AutoBhopZone());
        Plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _hide[player.Slot] = false;
        ClearMeasure(player.Slot);
    }

    public override void OnMapStart(string mapName)
    {
        Array.Clear(_hide);
        _measure.Clear();
    }

    // ====================================================================== !hide (HidePlayers)

    /// <summary>Hiding others is only allowed for players who are not alive, or who play a warden game.</summary>
    private bool HideAllowed(CCSPlayerController c) => !c.IsAliveP() || (_games?.IsPlaying(c.Slot) ?? false);

    private WardenGamesModule? _games;

    public override void OnAllModulesLoaded(bool hotReload) => _games = Plugin.GetModule<WardenGamesModule>();

    [ConsoleCommand("css_hide", "sm_hide - hide the other players (dead players and warden game players)")]
    public void CmdHide(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        if (!_hide[caller.Slot] && !HideAllowed(caller)) { Chat.Tell(caller, "games.hide.not_now"); return; }
        _hide[caller.Slot] = !_hide[caller.Slot];
        Chat.Tell(caller, _hide[caller.Slot] ? "games.hide.on" : "games.hide.off");
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        List<(int Slot, CCSPlayerPawn Pawn, List<CBasePlayerWeapon> Weapons)>? alive = null;
        foreach (var (info, viewer) in infoList)
        {
            if (viewer == null || !viewer.IsValid || !_hide[viewer.Slot] || !HideAllowed(viewer)) continue;
            alive ??= Players.Alive().Select(c => (c.Slot, Pawn: c.PlayerPawn.Value!, Weapons: c.Weapons().ToList()))
                .Where(t => t.Pawn != null && t.Pawn.IsValid).ToList();
            // Never hide the player being spectated (the client needs that pawn).
            var target = viewer.ObserverPawn.Value?.ObserverServices?.ObserverTarget.Value?.Index ?? 0;
            foreach (var (slot, pawn, weapons) in alive)
            {
                if (slot == viewer.Slot || pawn.Index == target) continue;
                info.TransmitEntities.Remove(pawn);
                foreach (var w in weapons) info.TransmitEntities.Remove(w); // no floating guns
            }
        }
    }

    // ====================================================================== !measure / !gap (MeasureGap)

    [ConsoleCommand("css_measure", "sm_measure - (admin) measure the distance between two pinged points")]
    [ConsoleCommand("css_gap", "sm_gap - (admin) measure the distance between two pinged points")]
    public void CmdMeasure(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        if (!Perms.Has(caller, "zones")) { Chat.Tell(caller, "core.no_access"); return; }
        ShowMeasure(caller);
    }

    private void ShowMeasure(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("games.measure.title"));
        m.Subtitle = Hud.Escape(Lang.T("games.measure.hint"));
        m.Add(Lang.T("games.measure.set_a"), p => SetPoint(p, true));
        m.Add(Lang.T("games.measure.set_b"), p => SetPoint(p, false), _measure.TryGetValue(c.Slot, out var s) && s.A != null);
        m.Add(Lang.T("games.measure.clear"), p => { ClearMeasure(p.Slot); ShowMeasure(p); });
        m.Open(c);
    }

    private void SetPoint(CCSPlayerController c, bool first)
    {
        if (!Aim.TryGetPing(c, 15, out var ping, out _)) { Chat.Tell(c, "games.measure.ping_first"); ShowMeasure(c); return; }
        var point = new Vector3(ping.X, ping.Y, ping.Z);
        if (first)
        {
            ClearMeasure(c.Slot);
            _measure[c.Slot] = (point, null);
            Chat.Tell(c, "games.measure.a_set");
        }
        else if (_measure.TryGetValue(c.Slot, out var s) && s.A is { } a)
        {
            var (total, horizontal, vertical) = GapMath.Measure(a, point);
            Chat.Tell(c, "games.measure.result", total.ToString("0.0"), horizontal.ToString("0.0"), vertical.ToString("0.0"));
            Fx.SafeRemove(s.Beam);
            var beam = Fx.Beam(new CssVector(a.X, a.Y, a.Z), new CssVector(point.X, point.Y, point.Z), Color.FromArgb(255, 255, 220, 60), 1.5f, 20f);
            Visibility.OnlyFor(beam, c.Slot); // only the measuring admin sees it
            _measure[c.Slot] = (a, beam);
        }

        ShowMeasure(c);
    }

    private void ClearMeasure(int slot)
    {
        if (_measure.Remove(slot, out var s)) Fx.SafeRemove(s.Beam);
    }
}

/// <summary>MinigamesAutoBhop (hlstriker): auto bunny-hop only inside this zone, for everyone or one team (sm_setbhopteam).</summary>
internal sealed class AutoBhopZone : ZoneType
{
    private const string MoveKey = "zone.autobhop";

    public override string Key => "auto_bhop";
    public override string DisplayName => "Auto Bhop";
    public override string Description => "Holding jump bunny-hops automatically while inside (bhop levels of minigame maps).";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Team", ZoneFieldKind.Choice, 1) { Options = new[] { ("Everyone", 0), ("Prisoners", 1), ("Guards", 2) } }
    };

    private static bool Applies(Zone zone, CCSPlayerController c) => zone.GetInt(1) switch
    {
        1 => c.Team == CsTeam.Terrorist,
        2 => c.Team == CsTeam.CounterTerrorist,
        _ => true
    };

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        if (Applies(zone, player)) Movement.SetAutoBhop(player, MoveKey + zone.Id, true);
    }

    public override void OnExit(Zone zone, CCSPlayerController player) => Movement.SetAutoBhop(player, MoveKey + zone.Id, false);

    public override void OnRoundStart()
    {
        // Zones forget who is inside at round start (everyone respawns): drop every zone's key.
        foreach (var c in Players.All())
            foreach (var z in Jb.Zones.OfType(Key)) Movement.SetAutoBhop(c, MoveKey + z.Id, false);
    }

    public override string Summary(Zone zone) => zone.GetInt(1) switch { 1 => "prisoners", 2 => "guards", _ => "everyone" };
}
