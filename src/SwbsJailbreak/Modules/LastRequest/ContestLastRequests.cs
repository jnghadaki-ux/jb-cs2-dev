using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "Contests" (lr_jump_contest_most_jumps_* / lr_race_contest_countdown).</summary>
public sealed class ContestConfig
{
    public int MostJumpsCountdown { get; set; } = 5;
    public int MostJumpsTimeLimit { get; set; } = 20;
    public int RaceCountdown { get; set; } = 5;
}

internal enum ContestKind
{
    None,
    RussianRoulette,
    MostJumps,
    FallDamage,
    FallDamageOldSchool,
    GunToss,
    GunTossOldSchool,
    Race
}

/// <summary>State of one contest, kept on the prisoner.</summary>
internal sealed class ContestState
{
    public ContestKind Kind;
    public bool HasOpponent;
    public int OpponentSlot = -1;
    public int EffectId;
    public Timer? Timer;
    public int Tick;

    // turn based (roulette, fall damage old school)
    public int TurnSlot = -1;
    public int NextSlaySeconds;

    // race
    public Vector? RaceStart;
    public QAngle? RaceStartAngles;
    public Vector? RaceEnd;
    public bool RaceStarted;
    public readonly List<CBeam> FinishLine = new();
    public JbMenu? PlacementMenu;

    // gun toss
    public float SlayStart;
}

/// <summary>Per participant data (both prisoner and guard).</summary>
internal sealed class ContestPlayer
{
    public int KillShot, ShotsFired, OldClip;
    public int Jumps;
    public float FallDamage = -1f;
    public uint TossWeapon;
    public Vector? TossStart, TossEnd, TossLastPos, LastGround;
    public float TossForceEnd, NextMove, NextBeam;
    public bool TossDropped;
}

/// <summary>
/// Ports of LastRequests/UltJB_LR_Contest_*.sp: Russian Roulette, Most Jumps, Fall Damage, Fall Damage: Old School,
/// Gun Toss, Gun Toss: Old School and Race. Losers are slain with the kill credited to the winner. The Race finish
/// line is drawn with real beam entities (the Swoobles finish-line model cannot load in CS2).
/// </summary>
public sealed class ContestLastRequests : JbModule
{
    private ContestConfig Cfg => Section<ContestConfig>("Contests");

    public override string Name => "LastRequests.Contest";
    public override int Order => 10;

    private const LrFlags NoHurtPickups = LrFlags.DontAllowDamagingOpponent | LrFlags.AllowWeaponPickups;
    private const LrFlags NoHurtDrops = LrFlags.DontAllowDamagingOpponent | LrFlags.AllowWeaponDrops;

    private static ContestState S(JbPlayer p) => p.Data<ContestState>();
    private static ContestPlayer P(JbPlayer p) => p.Data<ContestPlayer>();

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        Register("Russian Roulette", ContestKind.RussianRoulette, LrFlags.DontAllowDamagingOpponent);
        Register("Most Jumps", ContestKind.MostJumps, NoHurtPickups);
        Register("Fall Damage", ContestKind.FallDamage, NoHurtPickups);
        Register("Fall Damage: Old School", ContestKind.FallDamageOldSchool, NoHurtPickups);
        Register("Gun Toss", ContestKind.GunToss, NoHurtDrops);
        Register("Gun Toss: Old School", ContestKind.GunTossOldSchool, NoHurtDrops);
        Register("Race", ContestKind.Race, NoHurtPickups);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityDamage);
        Plugin.RegisterEventHandler<EventPlayerJump>(OnJump);
        DamageService.Register(Name, 98, OnDamage);
    }

    public override void OnUnload(bool hotReload) => DamageService.Unregister(OnDamage);

    private void Register(string name, ContestKind kind, LrFlags flags) =>
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = name, Category = "Contest", Flags = flags,
            OnStart = p =>
            {
                var s = S(p);
                Reset(s);
                s.Kind = kind;
                Jb.LastRequests.DisplayOpponentSelection(p, OnOpponentSelected);
            },
            OnEnd = OnEnd
        });

    private static void Reset(ContestState s)
    {
        KillTimer(s);
        s.Kind = ContestKind.None;
        s.HasOpponent = false;
        s.OpponentSlot = s.TurnSlot = -1;
        s.EffectId = 0;
        s.RaceStart = s.RaceEnd = null;
        s.RaceStarted = false;
        foreach (var b in s.FinishLine) Fx.SafeRemove(b);
        s.FinishLine.Clear();
        s.PlacementMenu = null;
    }

    private static void KillTimer(ContestState s)
    {
        try { s.Timer?.Kill(); }
        catch { /* already stopped */ }
        s.Timer = null;
    }

    private static void ResetPlayer(JbPlayer p)
    {
        var d = P(p);
        d.KillShot = d.ShotsFired = d.OldClip = d.Jumps = 0;
        d.FallDamage = -1f;
        d.TossWeapon = 0;
        d.TossStart = d.TossEnd = d.TossLastPos = null;
        d.TossDropped = false;
    }

    private static void Say(JbPlayer a, JbPlayer? b, string text)
    {
        a.Controller?.PrintToChat(" " + text);
        b?.Controller?.PrintToChat(" " + text);
    }

    private static void Tell(JbPlayer p, string key, params object?[] args) => p.Controller?.PrintToChat(" " + Lang.T(key, args));

    private static JbPlayer? Opp(JbPlayer prisoner) => Jb.LastRequests.GetOpponent(prisoner);

    /// <summary>The prisoner whose contest this player takes part in (works for the guard too).</summary>
    private static JbPlayer? ContestOwner(JbPlayer p)
    {
        if (p.Controller?.Team == CsTeam.Terrorist) return S(p).HasOpponent ? p : null;
        var prisoner = Jb.LastRequests.GetOpponent(p);
        return prisoner != null && S(prisoner).HasOpponent ? prisoner : null;
    }

    // ================================================================== flow

    private void OnOpponentSelected(JbPlayer p, JbPlayer opponent)
    {
        var s = S(p);
        s.HasOpponent = true;
        s.OpponentSlot = opponent.Slot;
        ResetPlayer(p);
        ResetPlayer(opponent);
        switch (s.Kind)
        {
            case ContestKind.RussianRoulette:
                foreach (var x in new[] { p, opponent })
                {
                    P(x).KillShot = Random.Shared.Next(1, 8); // CLIP_SIZE 7
                    Jb.LastRequests.StripWeapons(x, true);
                }

                RouletteTurn(p, Random.Shared.Next(2) == 0 ? p : opponent);
                return;
            case ContestKind.GunToss:
            case ContestKind.GunTossOldSchool:
                Jb.LastRequests.StripWeapons(p, true);
                Jb.LastRequests.StripWeapons(opponent, true);
                break;
        }

        Jb.Effects.DisplaySelectionMenu(p, (pl, effectId) =>
        {
            S(pl).EffectId = effectId;
            AfterEffect(pl);
        }, pl =>
        {
            if (S(pl).Kind is ContestKind.GunToss or ContestKind.GunTossOldSchool) Tell(pl, "lr.battle.no_effect");
            S(pl).EffectId = 0;
            AfterEffect(pl);
        });
    }

    private static void StartEffects(JbPlayer p, JbPlayer o, ContestState s)
    {
        if (s.EffectId <= 0) return;
        var data = Jb.Effects.GetDefaultData(s.EffectId);
        Jb.Effects.StartEffect(p, s.EffectId, data);
        Jb.Effects.StartEffect(o, s.EffectId, data);
    }

    private void AfterEffect(JbPlayer p)
    {
        var s = S(p);
        var o = Opp(p);
        if (!s.HasOpponent || o == null) return;
        switch (s.Kind)
        {
            case ContestKind.MostJumps:
                StartEffects(p, o, s);
                Say(p, o, Lang.T("lr.contest.jumps.starting", Cfg.MostJumpsCountdown));
                s.Tick = 0;
                s.Timer = Plugin.AddTimer(1f, () => JumpsCountdown(p.Slot), TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
                break;
            case ContestKind.FallDamage:
                Jb.LastRequests.StartSlayTimer(p);
                StartEffects(p, o, s);
                break;
            case ContestKind.FallDamageOldSchool:
                StartEffects(p, o, s);
                s.NextSlaySeconds = 20;
                FallTurn(p, p); // prisoner starts (they start jumping first)
                break;
            case ContestKind.GunToss:
            case ContestKind.GunTossOldSchool:
                Jb.LastRequests.StartSlayTimer(p, 45);
                s.SlayStart = Server.CurrentTime;
                StartEffects(p, o, s);
                GiveDeagle(p);
                GiveDeagle(o);
                break;
            case ContestKind.Race:
                Jb.LastRequests.StartSlayTimer(p, 15, SlayTimerFlags.Prisoner);
                ShowPlacementMenu(p);
                break;
        }
    }

    private static void OnEnd(JbPlayer p, JbPlayer? opponent)
    {
        var s = S(p);
        if (!s.HasOpponent)
        {
            Reset(s);
            return;
        }

        var kind = s.Kind;
        var effect = s.EffectId;
        if (s.PlacementMenu != null && p.Controller is { } c && ReferenceEquals(MenuService.Current(c), s.PlacementMenu)) MenuService.Close(c, false);
        Reset(s);
        foreach (var x in new[] { p, opponent })
        {
            if (x?.Controller is not { IsValid: true } xc) continue;
            if (xc.IsAliveP())
            {
                // Only the contests that stripped weapons restore them (Most Jumps, Fall Damage and Race never strip).
                if (kind is ContestKind.RussianRoulette or ContestKind.GunToss or ContestKind.GunTossOldSchool) Jb.LastRequests.RestoreWeapons(x);
                if (kind == ContestKind.Race) xc.SetMoveType(MoveType_t.MOVETYPE_WALK);
            }

            if (effect > 0) Jb.Effects.StopEffect(x, effect);
            ResetPlayer(x);
        }
    }

    private static void Win(JbPlayer winner, JbPlayer loser) =>
        DamageService.KillWithCredit(loser.Controller!, winner.Controller);

    // ================================================================== Russian Roulette

    private static void RouletteTurn(JbPlayer prisoner, JbPlayer shooter)
    {
        var s = S(prisoner);
        var other = shooter.Slot == prisoner.Slot ? Opp(prisoner) : prisoner;
        if (other == null) return;
        s.TurnSlot = shooter.Slot;
        Jb.LastRequests.StripWeapons(other, false);
        var w = Jb.WeaponSelection.GiveWeapon(shooter, "weapon_deagle");
        var d = P(shooter);
        var clip = Math.Max(1, 7 - d.ShotsFired);
        if (w != null)
        {
            w.Clip1 = clip;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        }

        d.OldClip = clip;
        SelectSlot(shooter, "slot2");
        Jb.LastRequests.StartSlayTimer(prisoner, 10, shooter.Controller?.Team == CsTeam.CounterTerrorist ? SlayTimerFlags.Guard : SlayTimerFlags.Prisoner);
    }

    private static void RouletteTick(JbPlayer prisoner, ContestState s)
    {
        var o = Opp(prisoner);
        if (o == null) return;
        var shooter = s.TurnSlot == prisoner.Slot ? prisoner : s.TurnSlot == o.Slot ? o : null;
        if (shooter == null) return;
        var other = shooter.Slot == prisoner.Slot ? o : prisoner;
        var d = P(shooter);
        var w = shooter.Controller?.ActiveWeapon();
        if (w == null)
        {
            RouletteTurn(prisoner, other);
            return;
        }

        var clip = w.Clip1;
        if (clip > d.OldClip)
        {
            d.OldClip = clip;
            return;
        }

        if (clip == d.OldClip) return;
        d.ShotsFired++;
        d.OldClip = clip;
        RouletteTurn(prisoner, other);
        if (d.ShotsFired == d.KillShot) DamageService.KillWithCredit(shooter.Controller!, other.Controller);
    }

    private static void SelectSlot(JbPlayer p, string slot)
    {
        var c = p.Controller;
        Server.NextFrame(() =>
        {
            if (c is { IsValid: true } && c.IsAliveP()) c.ExecuteClientCommand(slot);
        });
    }

    // ================================================================== Most Jumps

    private void JumpsCountdown(int slot)
    {
        var p = Players.Get(slot);
        var s = S(p);
        var o = Opp(p);
        if (o == null || s.Kind != ContestKind.MostJumps)
        {
            KillTimer(s);
            return;
        }

        s.Tick++;
        if (s.Tick >= Math.Max(1, Cfg.MostJumpsCountdown))
        {
            KillTimer(s);
            P(p).Jumps = 0;
            P(o).Jumps = 0;
            Say(p, o, Lang.T("lr.contest.jumps.ending", Cfg.MostJumpsTimeLimit));
            s.Timer = Plugin.AddTimer(Math.Max(1, Cfg.MostJumpsTimeLimit), () => JumpsEnd(slot), TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        Say(p, o, Lang.T("lr.battle.tick", Cfg.MostJumpsCountdown - s.Tick));
    }

    private static void JumpsEnd(int slot)
    {
        var p = Players.Get(slot);
        var s = S(p);
        s.Timer = null;
        var o = Opp(p);
        if (o == null)
        {
            Jb.LastRequests.EndLastRequest(p);
            return;
        }

        var a = P(p).Jumps;
        var b = P(o).Jumps;
        if (a == b)
        {
            Tell(p, "lr.contest.jumps.tie", a);
            Tell(o, "lr.contest.jumps.tie", b);
            Jb.LastRequests.EndLastRequest(p);
            return;
        }

        var (winner, loser) = a > b ? (p, o) : (o, p);
        Tell(winner, "lr.contest.jumps.won", P(winner).Jumps, P(loser).Jumps);
        Tell(loser, "lr.contest.jumps.lost", winner.Name, P(winner).Jumps, P(loser).Jumps);
        Win(winner, loser);
    }

    private HookResult OnJump(EventPlayerJump ev, GameEventInfo info)
    {
        var p = Players.Get(ev.Userid);
        if (p == null) return HookResult.Continue;
        var owner = ContestOwner(p);
        if (owner != null && S(owner).Kind == ContestKind.MostJumps) P(p).Jumps++;
        return HookResult.Continue;
    }

    // ================================================================== Fall Damage (+ Old School)

    private static void FallTurn(JbPlayer prisoner, JbPlayer jumper)
    {
        var s = S(prisoner);
        var o = Opp(prisoner);
        if (o == null) return;
        // Slay timer always counts for the prisoner's turn; each guard turn shortens the next prisoner turn by 5 s.
        if (jumper.Controller?.Team == CsTeam.Terrorist)
        {
            Jb.LastRequests.StartSlayTimer(prisoner, Math.Max(1, s.NextSlaySeconds), SlayTimerFlags.Prisoner, OnFallSlay);
        }
        else
        {
            Jb.LastRequests.StartSlayTimer(prisoner, Math.Max(1, s.NextSlaySeconds), SlayTimerFlags.Guard, OnFallSlay);
            s.NextSlaySeconds -= 5;
        }

        s.TurnSlot = jumper.Slot;
    }

    private static void OnFallSlay(JbPlayer prisoner, JbPlayer? opponent, SlayTimerResult result)
    {
        if (opponent == null || result is SlayTimerResult.None or SlayTimerResult.Both) return;
        var (winner, loser) = result == SlayTimerResult.Prisoner ? (opponent, prisoner) : (prisoner, opponent);
        FallEndMessage(winner, loser);
    }

    private static void FallEndMessage(JbPlayer winner, JbPlayer loser)
    {
        var hp = winner.Controller?.GetHp() ?? 0;
        Tell(loser, "lr.contest.fall.os_lost", hp);
        Tell(winner, "lr.contest.fall.os_won", hp);
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        var victim = ctx.Victim.Jb();
        if (victim == null) return HookResult.Continue;
        var owner = ContestOwner(victim);
        if (owner == null) return HookResult.Continue;
        var s = S(owner);
        if (s.Kind is not (ContestKind.FallDamage or ContestKind.FallDamageOldSchool)) return HookResult.Continue;
        var o = victim.Slot == owner.Slot ? Opp(owner) : owner;
        if (o == null)
        {
            Jb.LastRequests.EndLastRequest(owner);
            return HookResult.Continue;
        }

        if (!ctx.IsFall) return HookResult.Handled;

        if (s.Kind == ContestKind.FallDamageOldSchool)
        {
            if (s.TurnSlot != victim.Slot) return HookResult.Handled;
            if (ctx.VictimPawn.Health - ctx.Damage < 1f)
            {
                FallEndMessage(o, victim);
                var vc = ctx.Victim;
                var oc = o.Controller;
                Server.NextFrame(() =>
                {
                    if (vc.IsValid) DamageService.KillWithCredit(vc, oc);
                });
                return HookResult.Handled;
            }

            FallTurn(owner, o);
            return HookResult.Continue;
        }

        var vd = P(victim);
        var od = P(o);
        if (vd.FallDamage >= 0f) return HookResult.Handled; // only the first fall counts
        vd.FallDamage = ctx.Damage;
        if (od.FallDamage < 0f)
        {
            var hp = ctx.VictimPawn.Health;
            if (hp - ctx.Damage < 1f) ctx.Damage = hp - 1;
            return HookResult.Continue;
        }

        var victimCtl = ctx.Victim;
        if (vd.FallDamage < od.FallDamage)
        {
            Tell(victim, "lr.contest.fall.lost", vd.FallDamage, od.FallDamage);
            Tell(o, "lr.contest.fall.won", od.FallDamage, vd.FallDamage);
            var oc = o.Controller;
            Server.NextFrame(() =>
            {
                if (victimCtl.IsValid) DamageService.KillWithCredit(victimCtl, oc);
            });
        }
        else if (vd.FallDamage > od.FallDamage)
        {
            Tell(victim, "lr.contest.fall.won", vd.FallDamage, od.FallDamage);
            Tell(o, "lr.contest.fall.lost", od.FallDamage, vd.FallDamage);
            var oc = o.Controller;
            Server.NextFrame(() =>
            {
                if (oc is { IsValid: true }) DamageService.KillWithCredit(oc, victimCtl);
            });
        }
        else
        {
            Tell(victim, "lr.contest.fall.tie", vd.FallDamage);
            Tell(o, "lr.contest.fall.tie", od.FallDamage);
            Server.NextFrame(() => Jb.LastRequests.EndLastRequest(owner));
        }

        return HookResult.Handled;
    }

    // ================================================================== Gun Toss (+ Old School)

    private static void GiveDeagle(JbPlayer p)
    {
        var w = Jb.WeaponSelection.GiveWeapon(p, "weapon_deagle");
        var d = P(p);
        d.TossWeapon = w?.EntityHandle.Raw ?? 0;
        d.TossDropped = false;
        d.TossStart = d.TossEnd = null;
        SelectSlot(p, "slot2");
    }

    private void TossTick(JbPlayer prisoner, ContestState s, JbPlayer player, float now)
    {
        var d = P(player);
        var c = player.Controller;
        var pawn = c?.PawnOrNull();
        if (c == null || pawn == null || d.TossWeapon == 0) return;
        var onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
        if (onGround && pawn.AbsOrigin is { } g) d.LastGround = new Vector(g.X, g.Y, g.Z);

        var w = new CHandle<CCSWeaponBase>(d.TossWeapon).Value;
        if (!d.TossDropped)
        {
            if (w == null || !w.IsValid) return;
            if (w.OwnerEntity.IsValid) return; // still held
            // Dropped: the toss starts where the player stood (Old School: last ground position when in the air).
            d.TossDropped = true;
            d.TossForceEnd = now + 10f;
            var start = s.Kind == ContestKind.GunTossOldSchool && !onGround && d.LastGround != null
                ? d.LastGround
                : pawn.AbsOrigin is { } o ? new Vector(o.X, o.Y, o.Z) : null;
            d.TossStart = start;
            DrawTossBeam(player, start);
            return;
        }

        if (d.TossEnd == null)
        {
            if (w == null || !w.IsValid)
            {
                Jb.LastRequests.EndLastRequest(prisoner);
                return;
            }

            if (now >= d.NextMove)
            {
                d.NextMove = now + 0.2f;
                var pos = w.AbsOrigin is { } wo ? new Vector(wo.X, wo.Y, wo.Z) : null;
                if (pos != null && (now >= d.TossForceEnd || d.TossLastPos != null && VectorUtil.Distance(pos, d.TossLastPos) < 0.01f))
                {
                    d.TossEnd = pos;
                    DrawTossBeam(player, pos);
                    CheckTossWinner(prisoner, s, player);
                    return;
                }

                d.TossLastPos = pos;
            }
        }

        if (now >= d.NextBeam)
        {
            d.NextBeam = now + 0.5f;
            DrawTossBeam(player, d.TossStart);
            DrawTossBeam(player, d.TossEnd);
        }
    }

    private static void DrawTossBeam(JbPlayer player, Vector? at)
    {
        if (at == null) return;
        var color = player.Controller?.Team == CsTeam.CounterTerrorist ? Color.FromArgb(255, 0, 0, 255) : Color.FromArgb(255, 255, 85, 0);
        Fx.Beam(at, new Vector(at.X, at.Y, at.Z + 50f), color, 3f, 0.6f);
    }

    private static void CheckTossWinner(JbPlayer prisoner, ContestState s, JbPlayer player)
    {
        var other = player.Slot == prisoner.Slot ? Opp(prisoner) : prisoner;
        if (other == null) return;
        var od = P(other);
        if (od.TossEnd == null)
        {
            // The other player has as long as the first toss took.
            var seconds = (int)Math.Ceiling(Server.CurrentTime - s.SlayStart);
            Jb.LastRequests.StartSlayTimer(prisoner, Math.Max(1, seconds),
                player.Controller?.Team == CsTeam.CounterTerrorist ? SlayTimerFlags.Prisoner : SlayTimerFlags.Guard);
            return;
        }

        var pd = P(player);
        var a = pd.TossStart != null && pd.TossEnd != null ? VectorUtil.Distance(pd.TossStart, pd.TossEnd) : 0f;
        var b = od.TossStart != null && od.TossEnd != null ? VectorUtil.Distance(od.TossStart, od.TossEnd) : 0f;
        var (winner, loser, dw, dl) = a > b ? (player, other, a, b) : (other, player, b, a);
        Jb.WeaponSelection.GiveWeapon(winner, "weapon_deagle");
        SelectSlot(winner, "slot2");
        Win(winner, loser);
        Tell(winner, "lr.contest.toss.won", dw, dl);
        Tell(loser, "lr.contest.toss.lost", dl, dw);
    }

    /// <summary>Tossed guns cannot be shot around (original zeroed the damage force on the weapon).</summary>
    private static HookResult OnEntityDamage(CBaseEntity entity, CTakeDamageInfo info)
    {
        try
        {
            if (entity == null || !entity.IsValid || !entity.DesignerName.StartsWith("weapon_")) return HookResult.Continue;
            var raw = entity.EntityHandle.Raw;
            foreach (var p in Players.Connected())
                if (P(p).TossWeapon == raw && P(p).TossDropped) return HookResult.Handled;
        }
        catch
        {
            // ignore
        }

        return HookResult.Continue;
    }

    // ================================================================== Race

    private void ShowPlacementMenu(JbPlayer p)
    {
        var s = S(p);
        var c = p.Controller;
        if (c == null) return;
        var start = s.RaceStart == null;
        var m = new JbMenu(Lang.T(start ? "lr.contest.race.title_start" : "lr.contest.race.title_end")) { ExitButton = false, Timeout = -1 };
        m.Add(Lang.T(start ? "lr.contest.race.create_start" : "lr.contest.race.create_end"), x =>
        {
            var pl = x.Jb();
            if (pl == null || !S(pl).HasOpponent) return;
            var st = S(pl);
            st.PlacementMenu = null;
            if (st.RaceStart == null)
            {
                if (CreateRaceStart(pl)) Jb.LastRequests.StartSlayTimer(pl, 25, SlayTimerFlags.Prisoner);
            }
            else
            {
                CreateRaceEnd(pl);
            }

            if (st.RaceEnd == null) ShowPlacementMenu(pl);
        });
        s.PlacementMenu = m;
        m.Open(c);
    }

    private static bool PlacementOk(JbPlayer p)
    {
        var c = p.Controller!;
        var pawn = c.PawnOrNull();
        if (pawn == null) return false;
        if ((pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0 || (c.Buttons & PlayerButtons.Duck) != 0)
        {
            Tell(p, "lr.contest.race.no_duck");
            return false;
        }

        if ((pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0)
        {
            Tell(p, "lr.contest.race.on_ground");
            return false;
        }

        return true;
    }

    private static bool CreateRaceStart(JbPlayer p)
    {
        if (!PlacementOk(p)) return false;
        var pawn = p.Controller!.PawnOrNull()!;
        var s = S(p);
        s.RaceStart = new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
        s.RaceStartAngles = new QAngle(0, pawn.EyeAngles.Y, 0);
        var o = Opp(p);
        if (o?.Controller != null)
        {
            o.Controller.TeleportTo(s.RaceStart, s.RaceStartAngles, null);
            Tell(o, "lr.contest.race.watch");
        }

        return true;
    }

    private void CreateRaceEnd(JbPlayer p)
    {
        if (!PlacementOk(p)) return;
        var s = S(p);
        var pawn = p.Controller!.PawnOrNull()!;
        var here = new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
        var dist = VectorUtil.Distance(here, s.RaceStart!);
        if (dist < 200f)
        {
            Tell(p, "lr.contest.race.too_close", (int)MathF.Round(200f - dist));
            return;
        }

        s.RaceEnd = here;
        DrawFinishLine(s, here, pawn.EyeAngles.Y);
        Jb.LastRequests.StopSlayTimer(p);
        StartRaceCountdown(p);
    }

    /// <summary>Finish line: two poles and a checkered top bar made of beam entities, 40 units wide.</summary>
    private static void DrawFinishLine(ContestState s, Vector at, float yaw)
    {
        var rad = (yaw + 90f) * MathF.PI / 180f;
        var dx = MathF.Cos(rad) * 20f;
        var dy = MathF.Sin(rad) * 20f;
        var left = new Vector(at.X + dx, at.Y + dy, at.Z);
        var right = new Vector(at.X - dx, at.Y - dy, at.Z);
        var lTop = new Vector(left.X, left.Y, left.Z + 80f);
        var rTop = new Vector(right.X, right.Y, right.Z + 80f);
        foreach (var b in new[]
                 {
                     Fx.Beam(left, lTop, Color.White, 3f), Fx.Beam(right, rTop, Color.White, 3f),
                     Fx.Beam(lTop, rTop, Color.Black, 6f), Fx.Beam(new Vector(lTop.X, lTop.Y, lTop.Z - 6f), new Vector(rTop.X, rTop.Y, rTop.Z - 6f), Color.White, 6f),
                     Fx.Beam(left, right, Color.Lime, 2f)
                 })
            if (b != null) s.FinishLine.Add(b);
    }

    private void StartRaceCountdown(JbPlayer p)
    {
        var s = S(p);
        var o = Opp(p);
        if (o == null || s.RaceStart == null) return;
        foreach (var x in new[] { p, o })
        {
            x.Controller?.SetMoveType(MoveType_t.MOVETYPE_NONE);
            x.Controller?.TeleportTo(s.RaceStart, null, new Vector(0, 0, 0));
        }

        Say(p, o, Lang.T("lr.contest.race.starting", Cfg.RaceCountdown));
        StartEffects(p, o, s);
        s.Tick = 0;
        var slot = p.Slot;
        s.Timer = Plugin.AddTimer(1f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            var op = Opp(pl);
            if (op == null || st.Kind != ContestKind.Race)
            {
                KillTimer(st);
                return;
            }

            st.Tick++;
            if (st.Tick >= Math.Max(1, Cfg.RaceCountdown))
            {
                KillTimer(st);
                Say(pl, op, Lang.T("lr.contest.race.started"));
                foreach (var x in new[] { pl, op })
                {
                    x.Controller?.SetMoveType(MoveType_t.MOVETYPE_WALK);
                    x.Controller?.PawnOrNull()?.Teleport(null, null, new Vector(0, 0, 0));
                }

                st.RaceStarted = true;
                Jb.LastRequests.StartSlayTimer(pl, 45);
                return;
            }

            Say(pl, op, Lang.T("lr.battle.tick", Cfg.RaceCountdown - st.Tick));
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void RaceTick(JbPlayer prisoner, ContestState s)
    {
        if (!s.RaceStarted || s.RaceEnd == null) return;
        var o = Opp(prisoner);
        if (o == null) return;
        foreach (var x in new[] { prisoner, o })
        {
            var pawn = x.Controller?.PawnOrNull();
            if (pawn?.AbsOrigin == null || pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP) continue;
            var a = pawn.AbsOrigin;
            // Trigger box of the original finish line: 40 x 40 x 80 units.
            if (Math.Abs(a.X - s.RaceEnd.X) > 20f || Math.Abs(a.Y - s.RaceEnd.Y) > 20f || a.Z < s.RaceEnd.Z - 1f || a.Z > s.RaceEnd.Z + 80f) continue;
            s.RaceStarted = false;
            var loser = x.Slot == prisoner.Slot ? o : prisoner;
            Win(x, loser);
            return;
        }
    }

    // ================================================================== tick

    private void OnTick()
    {
        var now = (float)Server.EngineTime;
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.HasOpponent || s.Kind == ContestKind.None) continue;
            switch (s.Kind)
            {
                case ContestKind.RussianRoulette:
                    RouletteTick(p, s);
                    break;
                case ContestKind.GunToss:
                case ContestKind.GunTossOldSchool:
                    var o = Opp(p);
                    if (o == null) break;
                    TossTick(p, s, p, now);
                    if (S(p).HasOpponent) TossTick(p, s, o, now);
                    break;
                case ContestKind.Race:
                    RaceTick(p, s);
                    break;
            }
        }
    }
}
