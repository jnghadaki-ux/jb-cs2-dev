using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Days;

/// <summary>
/// Gun Game warday (team play or free-for-all): every kill moves the killer to the next weapon of DaysNew.GunGameWeapons,
/// the knife is the last level and a kill on it wins. Knifed players lose a level. Players respawn while the day runs
/// (round win conditions are paused); after GunGameMinutes the highest level wins.
/// </summary>
public sealed class GunGameDay : JbModule
{
    private NewDaysConfig Cfg => Section<NewDaysConfig>("DaysNew");

    public override string Name => "Days.GunGame";
    public override int Order => 12;

    private DayDefinition? _day;
    private bool _running;
    private List<string> _ladder = new();
    private readonly Dictionary<int, (int Level, long Order)> _levels = new();
    private long _order;
    private Timer? _hud, _limit;

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);
    private static bool HasFreeday(JbPlayer? p) => p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule days || !Cfg.GunGameEnabled) return;
        _day = new DayDefinition
        {
            Name = "Gun Game", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying |
                    DayFlags.GiveGuardsInfiniteAmmo | DayFlags.GivePrisonersInfiniteAmmo,
            Description = "Every kill gives you the next gun; a kill with the knife on the last level wins.",
            OnStart = _ =>
            {
                _ladder = DayLogic.GunGameLadder(Cfg.GunGameWeapons);
                _levels.Clear();
                _order = 0;
            },
            OnFreezeEnd = Release,
            OnEnd = _ => Stop()
        };
        days.Register(_day);
        JbEvents.DaySpawnPost += OnDaySpawn;
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.DaySpawnPost -= OnDaySpawn;
        Stop();
    }

    public override void OnPlayerDisconnect(JbPlayer player) => _levels.Remove(player.Slot);

    private void Release()
    {
        _running = true;
        Cvars.SetTemp("mp_ignore_round_win_conditions", "1");
        foreach (var p in DayUtil.AliveAll()) Equip(p);

        // Round win conditions are paused, so the day must finish before the round clock runs out.
        var seconds = Math.Max(30f, Cfg.GunGameMinutes * 60f);
        var left = Round.TimeLeft;
        if (left > 10f) seconds = Math.Min(seconds, left - 3f);
        _limit = Round.Timer(seconds, TimeUp);
        _hud = Round.Timer(1f, ShowHud, true);
        Chat.TellAll("days.gungame.go", _ladder.Count, (int)(seconds / 60), (int)(seconds % 60));
    }

    private void Stop()
    {
        if (_running) Cvars.Restore("mp_ignore_round_win_conditions");
        _running = false;
        foreach (var t in new[] { _hud, _limit })
        {
            try { t?.Kill(); }
            catch { /* already stopped */ }
        }

        _hud = _limit = null;
        Hud.ClearPrefix("days.gungame");
    }

    private int LevelOf(int slot) => _levels.TryGetValue(slot, out var l) ? l.Level : 0;

    private void SetLevel(int slot, int level) => _levels[slot] = (level, ++_order);

    private void Equip(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || !c.IsAliveP() || HasFreeday(p) || _ladder.Count == 0) return;
        if (!_levels.ContainsKey(p.Slot)) SetLevel(p.Slot, 0);
        Jb.LastRequests.StripWeapons(p);
        var weapon = _ladder[Math.Clamp(LevelOf(p.Slot), 0, _ladder.Count - 1)];
        var knife = c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t";
        DayUtil.Give(p, knife, weapon == "weapon_knife");
        if (weapon != "weapon_knife") DayUtil.Give(p, weapon, true);
    }

    private void OnDaySpawn(JbPlayer p)
    {
        if (!Active || !_running) return;
        var slot = p.Slot;
        Server.NextFrame(() =>
        {
            if (Active && _running) Equip(Players.Get(slot));
        });
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Active || !_running) return HookResult.Continue;
        var victim = ev.Userid;
        if (victim == null || !victim.IsValid) return HookResult.Continue;
        var attacker = ev.Attacker;
        var weapon = ev.Weapon ?? "";
        var knife = weapon.StartsWith("knife") || weapon.StartsWith("bayonet");

        if (attacker is { IsValid: true } && attacker.Slot != victim.Slot && attacker.Jb() is { } ap && !HasFreeday(ap))
        {
            var next = DayLogic.GunGameAdvance(LevelOf(attacker.Slot), _ladder.Count);
            if (next < 0)
            {
                Win(attacker);
                return HookResult.Continue;
            }

            SetLevel(attacker.Slot, next);
            var slot = attacker.Slot;
            Server.NextFrame(() =>
            {
                if (Active && _running) Equip(Players.Get(slot));
            });
            if (next == _ladder.Count - 1) Chat.TellAll("days.gungame.knife_level", attacker.PlayerName);

            if (knife && Cfg.GunGameKnifeDemote > 0)
            {
                SetLevel(victim.Slot, DayLogic.GunGameDemote(LevelOf(victim.Slot), _ladder.Count, Cfg.GunGameKnifeDemote));
                Chat.Tell(victim, "days.gungame.demoted", attacker.PlayerName);
            }
        }

        var victimSlot = victim.Slot;
        Round.Timer(Math.Max(0.5f, Cfg.GunGameRespawnSeconds), () =>
        {
            if (!Active || !_running) return;
            var c = Players.Get(victimSlot).Controller;
            if (c is { IsValid: true } && !c.IsAliveP() && c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) c.RespawnSolid();
        });
        return HookResult.Continue;
    }

    private void TimeUp()
    {
        if (!Active || !_running) return;
        var leader = DayLogic.GunGameLeader(_levels.Where(kv => Players.Get(kv.Key).Controller is { IsValid: true })
            .ToDictionary(kv => kv.Key, kv => kv.Value));
        var c = leader >= 0 ? Players.Get(leader).Controller : null;
        if (c == null)
        {
            Stop();
            Round.End(RoundEndReason.RoundDraw);
            return;
        }

        Chat.TellAll("days.gungame.time_up");
        Win(c);
    }

    private void Win(CCSPlayerController winner)
    {
        if (!_running) return;
        Chat.TellAll("days.gungame.winner", winner.PlayerName);
        if (winner.Jb() is { } wp && Cfg.GunGameWinPoints > 0) Jb.Points.AddPoints(wp, Cfg.GunGameWinPoints, "Gun Game win");
        var team = winner.Team;
        Stop();
        Round.End(team == CsTeam.CounterTerrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
    }

    private void ShowHud()
    {
        if (!Active || !_running) return;
        var leader = DayLogic.GunGameLeader(_levels);
        var leaderName = leader >= 0 ? Players.Get(leader).Controller?.PlayerName ?? "?" : "-";
        var leaderLevel = leader >= 0 ? LevelOf(leader) + 1 : 0;
        foreach (var c in Players.Humans())
        {
            if (!c.IsAliveP()) continue;
            var level = LevelOf(c.Slot);
            var weapon = _ladder.Count == 0 ? "" : Weapons.Display(_ladder[Math.Clamp(level, 0, _ladder.Count - 1)]);
            Hud.Set(c, "days.gungame", Lang.T("days.gungame.hud", level + 1, _ladder.Count, Hud.Escape(weapon), Hud.Escape(leaderName), leaderLevel), 20, 1.5f);
        }
    }
}
