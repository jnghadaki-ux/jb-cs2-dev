using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Days;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "KnifeTournament": the group last request.</summary>
public sealed class TournamentLrConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds the other prisoners have to join after the first one picks it.</summary>
    public int JoinSeconds { get; set; } = 10;

    /// <summary>A Teleport Destination zone with this name is the arena; without one, where the first prisoner stood.</summary>
    public string ArenaZone { get; set; } = "arena";

    /// <summary>Distance between the two duelists.</summary>
    public float Spacing { get; set; } = 220f;

    public int DuelSeconds { get; set; } = 45;

    /// <summary>Points for the champion, who then picks a normal last request.</summary>
    public int WinPoints { get; set; } = 50;
}

/// <summary>
/// Knife Tournament last request (category "Tournament"), for when two or more prisoners are choosing their LR at once.
/// The first prisoner to pick it opens a lobby; the others join by picking it too within JoinSeconds (nobody is pulled
/// in). The entrants then play a one-on-one knife bracket in the arena while everyone watches. Only the two duelists
/// can be hurt, guards included; a duel that runs out of time goes to the healthier player. Losers die; the champion
/// gets WinPoints and their LR menu back to pick a normal last request.
/// Prisoners cannot hurt each other with friendly fire off, so mp_teammates_are_enemies is on while the bracket runs
/// and every other same-team hit is blocked.
/// </summary>
public sealed class TournamentLastRequest : JbModule
{
    private TournamentLrConfig Cfg => Section<TournamentLrConfig>("KnifeTournament");

    public override string Name => "LastRequests.Tournament";
    public override int Order => 10;

    private LastRequestDefinition? _def;
    private DayLogic.TournamentPhase _phase;
    private readonly List<int> _entrants = new();
    private readonly List<int> _winners = new();
    private readonly Queue<(int A, int B)> _pairs = new();
    private readonly Dictionary<int, (Vector Pos, QAngle Ang)> _returnTo = new();
    private readonly List<GlowHandle> _duelGlows = new();
    private Vector? _arena;
    private float _arenaYaw;
    private int _roundNo, _duelA = -1, _duelB = -1, _countdownLeft, _lobbyLeft;
    private bool _duelLive, _teammatesEnemies, _ending;
    private Timer? _lobbyTimer, _countdown, _duelTimer;

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule || !Cfg.Enabled) return;
        _def = new LastRequestDefinition
        {
            Name = "Knife Tournament", Category = "Tournament",
            Description = "1v1 knife bracket with the other prisoners choosing a last request; the champion then picks a normal LR.",
            Flags = LrFlags.SeeTeammates | LrFlags.NoBeacon,
            Unavailable = _ => DayLogic.TournamentUnavailable(_phase, Choosing()),
            OnStart = Join,
            OnEnd = (p, _) => Left(p)
        };
        Jb.LastRequests.Register(_def);
        DamageService.Register(Name, 96, OnDamage);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Finish();
    }

    public override void OnRoundStart() => Finish();

    public override void OnRoundEnd(CsTeam winner, int reason) => Finish();

    /// <summary>Alive prisoners who are still choosing their last request.</summary>
    private static int Choosing() => Players.AlivePrisoners().Count(c =>
        c.Jb() is { } p && Jb.LastRequests.IsInLastRequest(p) && !Jb.LastRequests.HasStartedLastRequest(p));

    private bool InTournament(int slot)
    {
        if (slot < 0 || !_entrants.Contains(slot)) return false;
        var p = Players.Get(slot);
        return p.Controller is { IsValid: true } c && c.IsAliveP() && c.Team == CsTeam.Terrorist && ReferenceEquals(Jb.LastRequests.GetActive(p), _def);
    }

    // ====================================================================== lobby

    private void Join(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (_phase == DayLogic.TournamentPhase.Running)
        {
            // Cannot be picked while running (the menu greys it out); a late pick goes back to the menu.
            var slot = p.Slot;
            Server.NextFrame(() => Jb.LastRequests.EndLastRequest(Players.Get(slot)));
            return;
        }

        if (!_entrants.Contains(p.Slot)) _entrants.Add(p.Slot);
        if (_phase == DayLogic.TournamentPhase.Lobby)
        {
            Chat.TellAll("lr.tournament.joined", p.Name, _entrants.Count);
            return;
        }

        _phase = DayLogic.TournamentPhase.Lobby;
        SetArena(c);
        _lobbyLeft = Math.Clamp(Cfg.JoinSeconds, 3, 60);
        Chat.TellAll("lr.tournament.opened", p.Name, _lobbyLeft);
        foreach (var other in Players.AlivePrisoners())
        {
            if (other.Slot != c.Slot && other.Jb() is { } op && Jb.LastRequests.IsInLastRequest(op) && !Jb.LastRequests.HasStartedLastRequest(op))
                Chat.Tell(other, "lr.tournament.invite", p.Name);
        }

        ShowLobby();
        _lobbyTimer = Round.Timer(1f, () =>
        {
            _lobbyLeft--;
            if (_lobbyLeft > 0)
            {
                ShowLobby();
                return;
            }

            Kill(ref _lobbyTimer);
            CloseLobby();
        }, true);
    }

    private void ShowLobby() =>
        Hud.SetGlobal("lr.tournament", Lang.T("lr.tournament.hud_lobby", _entrants.Count, _lobbyLeft), 35, 1.2f,
            c => c.Team == CsTeam.Terrorist);

    private void SetArena(CCSPlayerController host)
    {
        _arena = null;
        var zone = Cfg.ArenaZone.Length > 0 ? Jb.Zones.FindByName(ZoneTypeKeys.TeleportDestination, Cfg.ArenaZone) : null;
        if (zone != null)
        {
            var b = zone.Box.BottomCenter;
            _arena = new Vector(b.X, b.Y, b.Z + 2f);
            _arenaYaw = zone.Angles.Y;
            return;
        }

        var pawn = host.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        _arena = VectorUtil.Copy(pawn.AbsOrigin);
        _arenaYaw = pawn.EyeAngles.Y;
    }

    private void CloseLobby()
    {
        if (_phase != DayLogic.TournamentPhase.Lobby) return;
        var entrants = _entrants.Where(InTournament).ToList();
        if (entrants.Count < 2 || _arena == null)
        {
            Chat.TellAll(_arena == null ? "lr.tournament.no_arena" : "lr.tournament.not_enough");
            var back = entrants.ToList();
            Finish();
            // Back to the LR menu: ending the LR re-opens it for a living prisoner.
            foreach (var slot in back) EndLrOf(slot);
            return;
        }

        _phase = DayLogic.TournamentPhase.Running;
        _entrants.Clear();
        _entrants.AddRange(entrants.OrderBy(_ => Random.Shared.Next()));
        _winners.Clear();
        _winners.AddRange(_entrants);
        Cvars.SetTemp("mp_teammates_are_enemies", "1");
        _teammatesEnemies = true;
        Hud.ClearGlobal("lr.tournament");
        Chat.TellAll("lr.tournament.start", _entrants.Count);
        _roundNo = 0;
        NextRound();
    }

    // ====================================================================== bracket

    private void NextRound()
    {
        if (_phase != DayLogic.TournamentPhase.Running) return;
        var entrants = _winners.Where(InTournament).ToList();
        _winners.Clear();
        if (entrants.Count <= 1)
        {
            Champion(entrants.Count == 1 ? entrants[0] : -1);
            return;
        }

        _roundNo++;
        foreach (var pair in DayLogic.BracketPairs(entrants)) _pairs.Enqueue(pair);
        Chat.TellAll("lr.tournament.round", _roundNo, entrants.Count);
        NextDuel();
    }

    private void NextDuel()
    {
        if (_phase != DayLogic.TournamentPhase.Running) return;
        while (_pairs.Count > 0)
        {
            var (a, b) = _pairs.Dequeue();
            var aOk = InTournament(a);
            var bOk = InTournament(b);
            if (aOk && bOk)
            {
                StartDuel(a, b);
                return;
            }

            // A bye, or the opponent died / left before their turn: the other one goes through.
            var through = aOk ? a : bOk ? b : -1;
            if (through < 0) continue;
            _winners.Add(through);
            Chat.TellAll("lr.tournament.bye", Players.Get(through).Name);
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

        Chat.TellAll("lr.tournament.duel", ca.PlayerName, cb.PlayerName);
        _countdownLeft = 3;
        ShowVs(ca, cb);
        _countdown = Round.Timer(1f, () =>
        {
            if (_phase != DayLogic.TournamentPhase.Running) return;
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                ShowVs(ca, cb);
                return;
            }

            Kill(ref _countdown);
            Fight(a, b);
        }, true);
    }

    private void ShowVs(CCSPlayerController a, CCSPlayerController b) =>
        Hud.SetGlobal("lr.tournament", Lang.T("lr.tournament.hud_vs", Hud.Escape(a.PlayerName), Hud.Escape(b.PlayerName), _countdownLeft), 40, 1.2f);

    private void Fight(int a, int b)
    {
        if (!InTournament(a) || !InTournament(b))
        {
            EndDuel(InTournament(a) ? a : InTournament(b) ? b : -1);
            return;
        }

        _duelLive = true;
        foreach (var slot in new[] { a, b })
        {
            var p = Players.Get(slot);
            p.Controller!.SetMoveType(MoveType_t.MOVETYPE_WALK);
            Jb.WeaponSelection.GiveWeapon(p, "weapon_knife_t");
            DayUtil.Select(p, "weapon_knife_t");
        }

        Hud.SetGlobal("lr.tournament", Lang.T("lr.tournament.hud_fight"), 40, 1.5f);
        _duelTimer = Round.Timer(Math.Max(10, Cfg.DuelSeconds), () =>
        {
            if (_phase != DayLogic.TournamentPhase.Running || !_duelLive) return;
            var ha = Players.Get(a).Controller?.GetHp() ?? 0;
            var hb = Players.Get(b).Controller?.GetHp() ?? 0;
            var winner = DayLogic.DuelTimeoutWinner(a, ha, b, hb, Random.Shared.Next(2));
            var loser = winner == a ? b : a;
            Chat.TellAll("lr.tournament.timeout");
            var lc = Players.Get(loser).Controller;
            if (lc != null && lc.IsAliveP()) DamageService.KillWithCredit(lc, Players.Get(winner).Controller, "knife");
            else EndDuel(winner);
        });
    }

    /// <summary>Duelists stand <see cref="TournamentLrConfig.Spacing"/> apart across the arena, facing each other, never inside a wall.</summary>
    private void Place(CCSPlayerController a, CCSPlayerController b)
    {
        var centre = _arena!;
        foreach (var c in new[] { a, b })
        {
            var pawn = c.PawnOrNull();
            if (pawn?.AbsOrigin != null) _returnTo[c.Slot] = (VectorUtil.Copy(pawn.AbsOrigin), new QAngle(0, pawn.EyeAngles.Y, 0));
        }

        var yaw = _arenaYaw * MathF.PI / 180f;
        var right = new Vector(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        var half = Math.Max(64f, Cfg.Spacing / 2f);
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
        var hit = Aim.WorldHit(new Vector(centre.X, centre.Y, centre.Z + 40f), new Vector(target.X, target.Y, target.Z + 40f), pawn);
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
        if (_phase != DayLogic.TournamentPhase.Running || _duelA < 0 && _duelB < 0) return; // already ended
        Kill(ref _countdown);
        Kill(ref _duelTimer);
        _duelLive = false;
        foreach (var g in _duelGlows) Glow.Remove(g);
        _duelGlows.Clear();
        _duelA = _duelB = -1;
        if (InTournament(winner))
        {
            var c = Players.Get(winner).Controller!;
            var p = c.Jb()!;
            Jb.LastRequests.StripWeapons(p);
            Jb.LastRequests.SetHealth(p, 100);
            c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            if (_returnTo.Remove(winner, out var back)) c.PawnOrNull()?.Teleport(back.Pos, back.Ang, new Vector(0, 0, 0));
            _winners.Add(winner);
            Chat.TellAll("lr.tournament.duel_won", c.PlayerName);
        }

        Round.Timer(3f, NextDuel);
    }

    private void Champion(int slot)
    {
        var c = slot >= 0 ? Players.Get(slot).Controller : null;
        Finish();
        if (c == null)
        {
            Chat.TellAll("lr.tournament.no_winner");
            return;
        }

        Chat.TellAll("lr.tournament.champion", c.PlayerName, Cfg.WinPoints);
        Fx.BeaconPulse(c, Color.FromArgb(255, 255, 200, 40));
        var p = c.Jb();
        if (p == null) return;
        if (Cfg.WinPoints > 0) Jb.Points.AddPoints(p, Cfg.WinPoints, "Knife Tournament win");
        // Ending the tournament LR of a living prisoner re-opens the LR menu: the champion now picks a normal LR.
        EndLrOf(slot);
        Chat.Tell(c, "lr.tournament.pick_lr");
    }

    /// <summary>An entrant's LR ended (death, disconnect, abort or round end).</summary>
    private void Left(JbPlayer p)
    {
        if (_ending || _phase == DayLogic.TournamentPhase.None) return;
        var slot = p.Slot;
        _entrants.Remove(slot);
        _winners.Remove(slot);
        if (_phase == DayLogic.TournamentPhase.Lobby)
        {
            if (_entrants.Count == 0) Finish();
            return;
        }

        if (slot == _duelA || slot == _duelB)
        {
            var other = slot == _duelA ? _duelB : _duelA;
            Server.NextFrame(() => EndDuel(other));
        }
    }

    private void EndLrOf(int slot)
    {
        var p = Players.Get(slot);
        if (p.Controller is not { IsValid: true } || !ReferenceEquals(Jb.LastRequests.GetActive(p), _def)) return;
        _ending = true;
        try { Jb.LastRequests.EndLastRequest(p); }
        finally { _ending = false; }
    }

    private void Finish()
    {
        if (_teammatesEnemies) Cvars.Restore("mp_teammates_are_enemies");
        _teammatesEnemies = false;
        _phase = DayLogic.TournamentPhase.None;
        _duelLive = false;
        _duelA = _duelB = -1;
        _entrants.Clear();
        _winners.Clear();
        _pairs.Clear();
        _returnTo.Clear();
        Kill(ref _lobbyTimer);
        Kill(ref _countdown);
        Kill(ref _duelTimer);
        foreach (var g in _duelGlows) Glow.Remove(g);
        _duelGlows.Clear();
        Hud.ClearGlobal("lr.tournament");
    }

    private static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    // ====================================================================== damage

    private HookResult OnDamage(DamageContext ctx)
    {
        if (_phase == DayLogic.TournamentPhase.None || ctx.Attacker == null || ctx.IsSelf) return HookResult.Continue;
        var a = ctx.Attacker.Slot;
        var v = ctx.Victim.Slot;
        if (_entrants.Contains(a) || _entrants.Contains(v))
        {
            var inDuel = _duelLive && (a == _duelA && v == _duelB || a == _duelB && v == _duelA);
            return inDuel ? HookResult.Continue : HookResult.Handled;
        }

        // mp_teammates_are_enemies is only for the duelists: every other same-team hit stays blocked.
        return _teammatesEnemies && ctx.Attacker.Team == ctx.Victim.Team ? HookResult.Handled : HookResult.Continue;
    }
}
