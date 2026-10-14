using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>
/// Tournament warday: the prisoners play a one-on-one knife bracket in an arena while everyone watches. The arena is a
/// Teleport Destination zone named DaysNew.TournamentArenaZone ("arena"), or where the warden stands when the day starts.
/// Only the two duelists can hurt each other; a duel that runs out of time goes to the healthier player. The last
/// prisoner standing wins TournamentWinPoints and the round.
/// </summary>
public sealed class TournamentDay : JbModule
{
    private NewDaysConfig Cfg => Section<NewDaysConfig>("DaysNew");

    public override string Name => "Days.Tournament";
    public override int Order => 12;

    private DayDefinition? _day;
    private bool _running;
    private Vector? _arena;
    private float _arenaYaw;
    private int _roundNo;
    private readonly List<int> _winners = new();
    private readonly Queue<(int A, int B)> _pairs = new();
    private int _duelA = -1, _duelB = -1;
    private bool _duelLive;
    private readonly Dictionary<int, (Vector Pos, QAngle Ang)> _returnTo = new();
    private Timer? _duelTimer, _countdown;
    private int _countdownLeft;

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);
    private static bool HasFreeday(JbPlayer? p) => p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule days || !Cfg.TournamentEnabled) return;
        _day = new DayDefinition
        {
            Name = "Tournament", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying,
            Description = "One-on-one knife duels in a bracket; the last prisoner standing wins.",
            FreezeTime = 5,
            CanStart = () => Players.CountAlive(CsTeam.Terrorist) >= 2,
            OnStart = Begin,
            OnFreezeEnd = Release,
            OnEnd = _ => Stop()
        };
        days.Register(_day);
        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Stop();
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (!Active || !_running) return;
        _winners.Remove(player.Slot);
        if (player.Slot == _duelA || player.Slot == _duelB)
        {
            var other = player.Slot == _duelA ? _duelB : _duelA;
            Server.NextFrame(() => EndDuel(other));
        }
    }

    private void Begin(JbPlayer? starter)
    {
        _arena = null;
        _arenaYaw = 0f;
        var zone = Cfg.TournamentArenaZone.Length > 0 ? Jb.Zones.FindByName(ZoneTypeKeys.TeleportDestination, Cfg.TournamentArenaZone) : null;
        if (zone != null)
        {
            var b = zone.Box.BottomCenter;
            _arena = new Vector(b.X, b.Y, b.Z + 2f);
            _arenaYaw = zone.Angles.Y;
            return;
        }

        var host = starter?.Controller is { } sc && sc.IsAliveP() ? sc : Players.AliveGuards().FirstOrDefault(g => Jb.Warden.IsWarden(g)) ?? Players.AliveGuards().FirstOrDefault();
        var pawn = host?.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        _arena = VectorUtil.Copy(pawn.AbsOrigin);
        _arenaYaw = pawn.EyeAngles.Y;
        Chat.TellAll("days.tournament.arena_here", host!.PlayerName);
    }

    private void Release()
    {
        if (_arena == null)
        {
            Chat.TellAll("days.tournament.no_arena");
            return;
        }

        _running = true;
        _roundNo = 0;
        _winners.Clear();
        _winners.AddRange(Players.AlivePrisoners().Where(c => !HasFreeday(c.Jb())).Select(c => c.Slot).OrderBy(_ => Random.Shared.Next()));
        Chat.TellAll("days.tournament.start", _winners.Count);
        NextRound();
    }

    private void Stop()
    {
        _running = false;
        _duelLive = false;
        _duelA = _duelB = -1;
        _pairs.Clear();
        _winners.Clear();
        _returnTo.Clear();
        KillTimers();
        foreach (var g in _duelGlows) Glow.Remove(g);
        _duelGlows.Clear();
        Hud.ClearGlobal("days.tournament");
    }

    private void KillTimers()
    {
        foreach (var t in new[] { _duelTimer, _countdown })
        {
            try { t?.Kill(); }
            catch { /* already stopped */ }
        }

        _duelTimer = _countdown = null;
    }

    private static bool Alive(int slot) => slot >= 0 && Players.Get(slot).Controller is { IsValid: true } c && c.IsAliveP() && c.Team == CsTeam.Terrorist;

    private void NextRound()
    {
        if (!Active || !_running) return;
        var entrants = _winners.Where(Alive).ToList();
        _winners.Clear();
        if (entrants.Count <= 1)
        {
            Champion(entrants.Count == 1 ? entrants[0] : -1);
            return;
        }

        _roundNo++;
        foreach (var pair in DayLogic.BracketPairs(entrants)) _pairs.Enqueue(pair);
        Chat.TellAll("days.tournament.round", _roundNo, entrants.Count);
        NextDuel();
    }

    private void NextDuel()
    {
        if (!Active || !_running) return;
        while (_pairs.Count > 0)
        {
            var (a, b) = _pairs.Dequeue();
            var aOk = Alive(a);
            var bOk = Alive(b);
            if (aOk && bOk)
            {
                StartDuel(a, b);
                return;
            }

            // A bye, or the opponent died / left before their turn: the other one goes through.
            var through = aOk ? a : bOk ? b : -1;
            if (through < 0) continue;
            _winners.Add(through);
            Chat.TellAll("days.tournament.bye", Players.Get(through).Controller!.PlayerName);
        }

        NextRound();
    }

    private void StartDuel(int a, int b)
    {
        var ca = Players.Get(a).Controller!;
        var cb = Players.Get(b).Controller!;
        _duelA = a;
        _duelB = b;
        _duelLive = false;
        Place(ca, cb);
        foreach (var c in new[] { ca, cb })
        {
            var p = c.Jb()!;
            Jb.LastRequests.StripWeapons(p);
            Jb.LastRequests.SetHealth(p, 100);
            c.SetArmor(0, false);
            c.SetMoveType(MoveType_t.MOVETYPE_NONE);
            if (Glow.Attach(c, Color.FromArgb(255, 255, 200, 40)) is { } g) _duelGlows.Add(g);
        }

        Chat.TellAll("days.tournament.duel", ca.PlayerName, cb.PlayerName);
        _countdownLeft = 3;
        Hud.SetGlobal("days.tournament", Lang.T("days.tournament.hud_vs", Hud.Escape(ca.PlayerName), Hud.Escape(cb.PlayerName), _countdownLeft), 40, 1.2f);
        _countdown = Round.Timer(1f, () =>
        {
            if (!Active || !_running) return;
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                Hud.SetGlobal("days.tournament", Lang.T("days.tournament.hud_vs", Hud.Escape(ca.PlayerName), Hud.Escape(cb.PlayerName), _countdownLeft), 40, 1.2f);
                return;
            }

            try { _countdown?.Kill(); }
            catch { /* already stopped */ }
            _countdown = null;
            Fight(a, b);
        }, true);
    }

    private readonly List<GlowHandle> _duelGlows = new();

    private void Fight(int a, int b)
    {
        if (!Alive(a) || !Alive(b))
        {
            EndDuel(Alive(a) ? a : Alive(b) ? b : -1);
            return;
        }

        _duelLive = true;
        foreach (var slot in new[] { a, b })
        {
            var p = Players.Get(slot);
            p.Controller!.SetMoveType(MoveType_t.MOVETYPE_WALK);
            DayUtil.Give(p, "weapon_knife_t", true);
        }

        Hud.SetGlobal("days.tournament", Lang.T("days.tournament.hud_fight"), 40, 1.5f);
        _duelTimer = Round.Timer(Math.Max(10, Cfg.TournamentDuelSeconds), () =>
        {
            if (!Active || !_running || !_duelLive) return;
            var ha = Players.Get(a).Controller?.GetHp() ?? 0;
            var hb = Players.Get(b).Controller?.GetHp() ?? 0;
            var winner = DayLogic.DuelTimeoutWinner(a, ha, b, hb, Random.Shared.Next(2));
            var loser = winner == a ? b : a;
            Chat.TellAll("days.tournament.timeout");
            var lc = Players.Get(loser).Controller;
            if (lc != null && lc.IsAliveP()) DamageService.KillWithCredit(lc, Players.Get(winner).Controller, "knife");
            else EndDuel(winner);
        });
    }

    /// <summary>Duelists stand <see cref="NewDaysConfig.TournamentSpacing"/> apart across the arena, facing each other, never inside a wall.</summary>
    private void Place(CCSPlayerController a, CCSPlayerController b)
    {
        var centre = _arena!;
        foreach (var c in new[] { a, b })
        {
            var pawn = c.PawnOrNull();
            if (pawn?.AbsOrigin != null && !_returnTo.ContainsKey(c.Slot))
                _returnTo[c.Slot] = (VectorUtil.Copy(pawn.AbsOrigin), new QAngle(0, pawn.EyeAngles.Y, 0));
        }

        var yaw = _arenaYaw * MathF.PI / 180f;
        var right = new Vector(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        var half = Math.Max(64f, Cfg.TournamentSpacing / 2f);
        var spotA = Spot(centre, right, half, a);
        var spotB = Spot(centre, right, -half, a);
        var yawAtoB = MathF.Atan2(spotB.Y - spotA.Y, spotB.X - spotA.X) * 180f / MathF.PI;
        var still = new Vector(0, 0, 0);
        a.PawnOrNull()?.Teleport(spotA, new QAngle(0, yawAtoB, 0), still);
        b.PawnOrNull()?.Teleport(spotB, new QAngle(0, yawAtoB + 180f, 0), still);
    }

    private static Vector Spot(Vector centre, Vector right, float distance, CCSPlayerController ignore)
    {
        var target = new Vector(centre.X + right.X * distance, centre.Y + right.Y * distance, centre.Z);
        var pawn = ignore.PawnOrNull();
        if (pawn == null) return target;
        var from = new Vector(centre.X, centre.Y, centre.Z + 40f);
        var to = new Vector(target.X, target.Y, target.Z + 40f);
        var hit = Aim.WorldHit(from, to, pawn);
        if (hit == null) return target;
        // Stop 24 units short of the wall (half a player hull plus margin).
        var dx = hit.X - centre.X;
        var dy = hit.Y - centre.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 32f) return new Vector(centre.X, centre.Y, centre.Z);
        var k = (len - 24f) / len;
        return new Vector(centre.X + dx * k, centre.Y + dy * k, centre.Z);
    }

    private void EndDuel(int winner)
    {
        if (!_running || _duelA < 0 && _duelB < 0) return; // already ended (death and disconnect can both report it)
        KillTimers();
        _duelLive = false;
        foreach (var g in _duelGlows) Glow.Remove(g);
        _duelGlows.Clear();
        _duelA = _duelB = -1;
        if (winner >= 0 && Alive(winner))
        {
            var c = Players.Get(winner).Controller!;
            var p = c.Jb()!;
            Jb.LastRequests.StripWeapons(p);
            Jb.LastRequests.SetHealth(p, 100);
            c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            if (_returnTo.Remove(winner, out var back)) c.PawnOrNull()?.Teleport(back.Pos, back.Ang, new Vector(0, 0, 0));
            _winners.Add(winner);
            Chat.TellAll("days.tournament.duel_won", c.PlayerName);
        }

        Round.Timer(3f, NextDuel);
    }

    private void Champion(int slot)
    {
        _running = false;
        Hud.ClearGlobal("days.tournament");
        var c = slot >= 0 ? Players.Get(slot).Controller : null;
        if (c == null)
        {
            Chat.TellAll("days.tournament.no_winner");
            return;
        }

        Chat.TellAll("days.tournament.champion", c.PlayerName, Cfg.TournamentWinPoints);
        Hud.SetGlobal("days.tournament", Lang.T("days.tournament.hud_champion", Hud.Escape(c.PlayerName)), 40, 5f);
        if (c.Jb() is { } p && Cfg.TournamentWinPoints > 0) Jb.Points.AddPoints(p, Cfg.TournamentWinPoints, "Tournament win");
        Fx.BeaconPulse(c, Color.FromArgb(255, 255, 200, 40));
        if (Round.IsActive) Round.End(CsTeam.Terrorist);
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active || ctx.Attacker == null || ctx.IsSelf) return HookResult.Continue;
        var a = ctx.Attacker.Slot;
        var v = ctx.Victim.Slot;
        var inDuel = _duelLive && (a == _duelA && v == _duelB || a == _duelB && v == _duelA);
        return inDuel ? HookResult.Continue : HookResult.Handled;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Active || !_running) return HookResult.Continue;
        var victim = ev.Userid;
        if (victim == null || !victim.IsValid) return HookResult.Continue;
        _winners.Remove(victim.Slot);
        if (victim.Slot != _duelA && victim.Slot != _duelB) return HookResult.Continue;
        var winner = victim.Slot == _duelA ? _duelB : _duelA;
        Server.NextFrame(() => EndDuel(winner));
        return HookResult.Continue;
    }
}
