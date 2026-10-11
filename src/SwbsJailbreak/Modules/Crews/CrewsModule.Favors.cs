using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Crews;

public sealed partial class CrewsModule
{
    private readonly HashSet<string> _favorUsed = new();          // crew ids that used a favor this round
    private readonly Dictionary<string, float> _rollCallUntil = new();
    private readonly List<CEntityInstance> _roundEntities = new();
    private readonly List<GlowHandle> _roundGlows = new();
    private readonly Dictionary<int, double> _nextPing = new();
    private bool _shakedownUsed;

    // ====================================================================== favors

    private void ShowFavorMenu(CCSPlayerController c)
    {
        var crew = CrewOf(c);
        if (crew == null) { Chat.Tell(c, "crews.err.not_member"); return; }
        var me = crew.Member(Steam(c))!;
        var level = LevelOf(crew);
        var used = _favorUsed.Contains(crew.Id);
        var m = new JbMenu(Lang.T("crews.favor.title"));
        m.Subtitle = Hud.Escape(Lang.T("crews.favor.sub", crew.Respect, used ? Lang.T("crews.favor.used") : Lang.T("crews.favor.ready")));
        foreach (var f in CrewRules.Favors)
        {
            var favor = f;
            var locked = level < f.MinLevel;
            var label = $"{f.Name} — {f.Cost}" + (locked ? $" ({Lang.T("crews.favor.needs_level", f.MinLevel)})" : "");
            m.Add(Hud.Escape(label), p => CallFavor(p, favor),
                Cfg.FavorsEnabled && !used && !locked && crew.Respect >= f.Cost && CrewRules.CanUseFavors(me.Rank));
        }

        foreach (var f in CrewRules.Favors) m.AddInfo("<font class='fontSize-s'>" + Hud.Escape($"{f.Name}: {f.Description}") + "</font>");
        m.Add(Lang.T("crews.menu.back"), ShowCrewMenu);
        m.Open(c);
    }

    private void CallFavor(CCSPlayerController c, CrewFavor favor)
    {
        var crew = CrewOf(c);
        if (crew == null || !Cfg.FavorsEnabled) return;
        var me = crew.Member(Steam(c));
        if (me == null || !CrewRules.CanUseFavors(me.Rank)) { Chat.Tell(c, "crews.err.rank"); return; }
        if (_favorUsed.Contains(crew.Id)) { Chat.Tell(c, "crews.favor.already"); return; }
        if (LevelOf(crew) < favor.MinLevel) { Chat.Tell(c, "crews.favor.locked", favor.MinLevel); return; }
        if (crew.Respect < favor.Cost) { Chat.Tell(c, "crews.favor.too_expensive", favor.Cost); return; }
        if (!c.IsAliveP() || c.Team != CsTeam.Terrorist) { Chat.Tell(c, "crews.favor.alive_prisoner"); return; }
        if (Jb.Days.IsInProgress) { Chat.Tell(c, "crews.favor.not_in_days"); return; }

        var color = CrewColors.Of(crew.Color).Rgb;
        switch (favor.Key)
        {
            case "banner":
                var o = c.PawnOrNull()?.AbsOrigin;
                if (o == null) return;
                var b = new CssVector(o.X, o.Y, o.Z + 2f);
                _roundEntities.AddRange(Fx.Ring(b, 40f, Color.FromArgb(200, color), 3f, 0f, 20));
                var pillar = Fx.Beam(b, new CssVector(o.X, o.Y, o.Z + 220f), Color.FromArgb(220, color), 5f);
                if (pillar != null) _roundEntities.Add(pillar);
                break;
            case "colors":
                foreach (var m in OnlineMembers(crew).Where(x => x.IsAliveP() && x.Team == CsTeam.Terrorist))
                    AddTimedGlow(m, color, null, 15f);
                break;
            case "lookout":
                var lookoutCrew = crew.Id;
                foreach (var m in OnlineMembers(crew).Where(x => x.IsAliveP() && x.Team == CsTeam.Terrorist))
                {
                    var owner = m.Slot;
                    AddTimedGlow(m, color, viewer => viewer.Slot != owner && IsPrisonerOf(viewer, lookoutCrew), 8f);
                }

                break;
            case "rollcall":
                _rollCallUntil[crew.Id] = Server.CurrentTime + 30f;
                RollCallTick();
                break;
            default:
                return;
        }

        crew.Respect -= favor.Cost;
        Save(crew);
        _favorUsed.Add(crew.Id);
        TellCrew(crew, "crews.favor.called", Chat.Name(c), favor.Name, favor.Cost);
    }

    private void AddTimedGlow(CCSPlayerController target, Color color, Func<CCSPlayerController, bool>? visibleTo, float seconds)
    {
        var h = Glow.Attach(target, color, visibleTo);
        if (h == null) return;
        _roundGlows.Add(h);
        Round.Timer(seconds, () =>
        {
            Glow.Remove(h);
            _roundGlows.Remove(h);
        });
    }

    /// <summary>Roll Call: crewmates on the HUD with the named zone they stand in (cell block, armory, ...), or down.</summary>
    private void RollCallTick()
    {
        if (_rollCallUntil.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var (crewId, until) in _rollCallUntil.ToList())
        {
            if (now > until || !_crews.TryGetValue(crewId, out var crew))
            {
                _rollCallUntil.Remove(crewId);
                continue;
            }

            var members = OnlineMembers(crew).Where(x => x.Team == CsTeam.Terrorist).ToList(); // prisoners only: no guard intel either way
            var lines = new List<string>();
            foreach (var m in members.OrderBy(x => x.PlayerName))
            {
                string where;
                if (!m.IsAliveP()) where = "<font color='#FF5555'>down</font>";
                else
                {
                    var zone = Jb.Zones.ZonesOf(m).Where(z => z.Name.Length > 0 && (z.TypeKey is ZoneTypeKeys.CellBlock or ZoneTypeKeys.Named or ZoneTypeKeys.Notify))
                        .OrderBy(z => z.Box.Size.LengthSquared()).FirstOrDefault();
                    where = zone != null ? Hud.Escape(zone.Name) : "<font color='#AAAAAA'>somewhere</font>";
                }

                lines.Add($"{Hud.Escape(m.PlayerName)} · {where}");
            }

            var col = CrewColors.Of(crew.Color).Rgb;
            var html = $"<font color='#{col.R:X2}{col.G:X2}{col.B:X2}'>[{Hud.Escape(crew.Tag)}] Roll Call</font><br><font class='fontSize-s'>{string.Join("<br>", lines)}</font>";
            foreach (var m in members) Hud.Set(m, "crews.rollcall", html, 45, 1.5f);
        }
    }

    // ====================================================================== crew pings (crew-only markers)

    private void OnPing(CCSPlayerController c, CssVector pos, int entity)
    {
        if (!Cfg.CrewPingMarkers || c.Team != CsTeam.Terrorist || !c.IsAliveP()) return;
        var crew = CrewOf(c);
        if (crew == null) return;
        var now = Server.EngineTime;
        if (_nextPing.TryGetValue(c.Slot, out var t) && now < t) return;
        _nextPing[c.Slot] = now + 2f;
        var color = Color.FromArgb(230, CrewColors.Of(crew.Color).Rgb);
        var ring = Fx.Ring(new CssVector(pos.X, pos.Y, pos.Z + 3f), 26f, color, 2.5f, 4f, 16);
        var crewId = crew.Id;
        foreach (var beam in ring)
            Visibility.OnlyFor(beam, viewer => IsPrisonerOf(viewer, crewId));
    }

    // ====================================================================== warden shakedown

    [ConsoleCommand("css_shakedown", "sm_shakedown - (warden) order a shakedown on a crew: its members glow for the guards")]
    public void CmdShakedown(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        OpenShakedown(caller);
    }

    /// <summary>The warden menu shows the shakedown item only when it can be used.</summary>
    internal bool ShakedownAvailable => Cfg.ShakedownEnabled && !_shakedownUsed && !Jb.Days.IsInProgress;

    internal void OpenShakedown(CCSPlayerController caller)
    {
        if (!Cfg.ShakedownEnabled) { Chat.Tell(caller, "crews.shakedown.disabled"); return; }
        var w = Jb.Warden.Warden;
        if (w == null || w.Slot != caller.Slot) { Chat.Tell(caller, "crews.shakedown.warden_only"); return; }
        if (_shakedownUsed) { Chat.Tell(caller, "crews.shakedown.used"); return; }
        if (Jb.Days.IsInProgress) { Chat.Tell(caller, "crews.favor.not_in_days"); return; }

        var m = new JbMenu(Lang.T("crews.shakedown.title"));
        foreach (var crew in _crews.Values
                     .Select(cr => (cr, alive: OnlineMembers(cr).Count(x => x.IsAliveP() && x.Team == CsTeam.Terrorist)))
                     .Where(t => t.alive > 0)
                     .OrderByDescending(t => TurfOf(t.cr).Count).ThenByDescending(t => t.cr.Legacy))
        {
            var id = crew.cr.Id;
            m.Add(Hud.Escape($"[{crew.cr.Tag}] {crew.cr.Name} — {crew.alive} alive · turf {TurfOf(crew.cr).Count}"), p => Shakedown(p, id));
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("crews.shakedown.none"));
        m.Open(caller);
    }

    private void Shakedown(CCSPlayerController warden, string crewId)
    {
        if (_shakedownUsed || !_crews.TryGetValue(crewId, out var crew)) return;
        var w = Jb.Warden.Warden;
        if (w == null || w.Slot != warden.Slot) return;
        _shakedownUsed = true;
        var seconds = Math.Clamp(Cfg.ShakedownSeconds, 1f, 30f);
        foreach (var m in OnlineMembers(crew).Where(x => x.IsAliveP() && x.Team == CsTeam.Terrorist))
            AddTimedGlow(m, Color.FromArgb(255, 255, 60, 60), viewer => viewer.Team == CsTeam.CounterTerrorist, seconds);
        Chat.TellAll("crews.shakedown.ordered", Chat.Name(warden), Label(crew), (int)seconds);
    }

    // ====================================================================== cleanup

    private void ClearRoundVisuals()
    {
        foreach (var e in _roundEntities) Fx.SafeRemove(e);
        _roundEntities.Clear();
        foreach (var g in _roundGlows) Glow.Remove(g);
        _roundGlows.Clear();
        foreach (var c in Players.Humans()) Hud.Clear(c, "crews.rollcall");
    }
}
