using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Days;
using SwbsJailbreak.Modules.LastRequest;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Crews;

/// <summary>
/// Crew War (a warday added for Crews): prisoners fight crew against crew (players without a crew fight alone), crewmates
/// cannot hurt each other, guards are untouchable referees with knives. Each crew starts together at a rebel point. The last
/// crew standing wins respect (and a rival bonus), the round ends as a prisoner win. Needs 2 crews with 2+ living members.
/// </summary>
public sealed class CrewWarDay : JbModule
{
    internal const string DayName = "Crew War";

    private CrewsConfig Cfg => Section<CrewsConfig>("Crews");

    public override string Name => "Crews.War";
    public override int Order => 14;
    public override bool Enabled => Cfg.Enabled && Cfg.CrewWarEnabled;

    private DayDefinition? _day;
    private CrewsModule? _crews;
    private bool _fighting;
    private bool _decided;
    private readonly HashSet<string> _startedCrews = new();
    private readonly Dictionary<int, string> _faction = new(); // slot -> faction, fixed when the day starts

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);

    public override void OnLoad(bool hotReload)
    {
        _crews = Jb.Crews as CrewsModule;
        if (Jb.Days is not DaysModule || _crews == null) return;
        _day = new DayDefinition
        {
            Name = DayName, Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons |
                    DayFlags.GivePrisonersInfiniteAmmo | DayFlags.DisableWeaponBuying,
            Description = "Crews fight each other; guards are referees.",
            CanStart = () => ReadyCrews().Count >= Math.Max(2, Cfg.CrewWarMinCrews),
            OnStart = OnStart,
            OnFreezeEnd = OnFreezeEnd,
            OnEnd = _ => OnEnd()
        };
        _day.FreezeTime = 10;
        _day.FreezeTeams = FreezeTeams.Prisoners | FreezeTeams.Guards;
        Jb.Days.Register(_day);
        DamageService.Register(Name, 96, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        OnEnd();
    }

    // ---------------------------------------------------------------- factions

    /// <summary>
    /// Crew id, or "solo:slot" for a prisoner without a crew. Once the day runs, factions are the ones fixed at its start,
    /// so joining, leaving or kicking mid-war cannot change who can hurt whom or who shares the win.
    /// </summary>
    private string Faction(CCSPlayerController c) =>
        Active && _faction.TryGetValue(c.Slot, out var f) ? f : _crews?.CrewOf(c)?.Id ?? "solo:" + c.Slot;

    private Dictionary<string, List<CCSPlayerController>> AliveFactions() =>
        Players.AlivePrisoners().GroupBy(Faction).ToDictionary(g => g.Key, g => g.ToList());

    private List<string> ReadyCrews() =>
        AliveFactions().Where(kv => !kv.Key.StartsWith("solo:", StringComparison.Ordinal) && kv.Value.Count >= Math.Max(1, Cfg.CrewWarMinMembers))
            .Select(kv => kv.Key).ToList();

    // ---------------------------------------------------------------- day callbacks

    private void OnStart(JbPlayer? starter)
    {
        _fighting = false;
        _decided = false;
        _startedCrews.Clear();
        _faction.Clear();
        foreach (var id in ReadyCrews()) _startedCrews.Add(id);
        foreach (var c in Players.AlivePrisoners()) _faction[c.Slot] = _crews?.CrewOf(c)?.Id ?? "solo:" + c.Slot;
        if (_crews != null) _crews.WarThisRound = true;
        Chat.TellAll("crews.war.start", _startedCrews.Count);

        // Each faction starts together at its own rebel point (when the map has them).
        var points = Plugin.GetModule<LastRequestModule>()?.RebelPointList ?? Array.Empty<float[]>();
        if (points.Count == 0) return;
        var order = Enumerable.Range(0, points.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        var i = 0;
        foreach (var (_, members) in AliveFactions())
        {
            var pt = points[order[i++ % order.Count]];
            if (pt is not { Length: >= 3 }) continue;
            var k = 0;
            foreach (var c in members)
            {
                var dx = (k % 3 - 1) * 40f;
                var dy = (k / 3 % 3 - 1) * 40f;
                c.TeleportTo(new CssVector(pt[0] + dx, pt[1] + dy, pt[2] + 4f), null, new CssVector(0, 0, 0));
                k++;
            }
        }
    }

    private void OnFreezeEnd()
    {
        _fighting = true;
        Cvars.SetTemp("mp_teammates_are_enemies", "1");
        foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist))
        {
            DayUtil.Give(p, "weapon_knife_t");
            DayUtil.Give(p, "weapon_deagle");
            DayUtil.Give(p, "weapon_ak47", true);
            p.Controller?.SetArmor(100, true);
        }

        foreach (var p in DayUtil.AliveOn(CsTeam.CounterTerrorist)) DayUtil.Give(p, "weapon_knife", true);
        Chat.TellAll("crews.war.fight");
        CheckWinner();
    }

    private void OnEnd()
    {
        if (!_fighting) return;
        _fighting = false;
        Cvars.Restore("mp_teammates_are_enemies");
    }

    /// <summary>Guards are referees (no damage to or from them); crewmates cannot hurt each other.</summary>
    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active) return HookResult.Continue;
        var attacker = ctx.Attacker;
        if (attacker == null || ctx.IsSelf) return HookResult.Continue; // world / fall / self
        if (!_fighting) return HookResult.Handled;
        if (attacker.Team == CsTeam.CounterTerrorist || ctx.Victim.Team == CsTeam.CounterTerrorist) return HookResult.Handled;
        return Faction(attacker) == Faction(ctx.Victim) ? HookResult.Handled : HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (Active && _fighting && !_decided) Server.NextFrame(CheckWinner);
        return HookResult.Continue;
    }

    // A disconnect or team switch raises no player_death, but can still leave one faction standing.
    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _faction.Remove(player.Slot);
        if (Active && _fighting && !_decided) Server.NextFrame(CheckWinner);
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (Active && _fighting && !_decided) Server.NextFrame(CheckWinner);
    }

    private void CheckWinner()
    {
        if (!Active || !_fighting || _decided || _crews == null) return;
        var factions = AliveFactions();
        if (factions.Count > 1) return;
        _decided = true;
        if (factions.Count == 0) return; // nobody left: the round ends on its own

        var (id, survivors) = factions.First();
        var crew = id.StartsWith("solo:", StringComparison.Ordinal) ? null : _crews.CrewById(id);
        if (crew == null)
        {
            Chat.TellAll("crews.war.lone_wolf", Chat.Name(survivors[0]));
        }
        else
        {
            crew.WarsWon++;
            _crews.Save(crew);
            _crews.GrantCrew(crew, Cfg.RespectWarWin, "crews.reason.war_win");
            if (crew.RivalId.Length > 0 && _startedCrews.Contains(crew.RivalId)) _crews.GrantCrew(crew, Cfg.RespectRivalBonus, "crews.reason.rival_beaten");
            foreach (var c in survivors.Where(x => _crews.CrewOf(x) == crew)) // still in the crew
                _crews.Grant(c, Cfg.RespectWarSurvivor, "crews.reason.war_survivor", ignoreCap: true);
            Chat.TellAll("crews.war.won", _crews.Label(crew), survivors.Count);
        }

        Round.End(CsTeam.Terrorist);
    }
}
