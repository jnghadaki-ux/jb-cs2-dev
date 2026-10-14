using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

internal enum DuelKind
{
    None,
    Dodgeball,
    Headshot,
    GrenadeWar,
    Taser,
    QuickDraw,
    GunRoulette
}

internal sealed class DuelState
{
    public DuelKind Kind;
    public bool Active, Started;
    public int OpponentSlot = -1;
    public Timer? Timer, TurnTimer;
    public int Tick;

    // quick draw
    public bool DrawCalled;
    public float ReadyAt;

    // gun roulette
    public List<string> Guns = new();
    public int GunIndex, ShotsThisGun, TurnSlot = -1;
}

/// <summary>
/// 0.15 weapon duels (category "Weapon Battle"): Dodgeball (decoys, one hit kills, refilled), Headshot Duel (Deagles,
/// only headshots hurt), Grenade War (HE only, refilled, low gravity), Taser Duel (1 s recharge), Quick Draw (rooted back
/// to back, shoot after "DRAW!", shooting early loses) and Gun Roulette (LastRequestsExtra.GunRouletteGuns random guns,
/// each gives both players one shot in turn; after the last gun the healthier player wins, so it never runs to round
/// end). Weapons exist only for the LR: both players' own weapons are saved and given back when it ends.
/// </summary>
public sealed class DuelLastRequests : JbModule
{
    private LrExtraConfig Cfg => Section<LrExtraConfig>("LastRequestsExtra");

    public override string Name => "LastRequests.Duels";
    public override int Order => 10;

    private static DuelState S(JbPlayer p) => p.Data<DuelState>();
    private int _taserUsers;

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        if (Cfg.DodgeballEnabled) Register("Dodgeball", DuelKind.Dodgeball, "Decoys only; one hit kills. A new decoy after every throw.");
        if (Cfg.HeadshotDuelEnabled) Register("Headshot Duel", DuelKind.Headshot, "Deagles; only headshots deal damage.");
        if (Cfg.GrenadeWarEnabled) Register("Grenade War", DuelKind.GrenadeWar, "HE grenades only, refilled, low gravity.");
        if (Cfg.TaserDuelEnabled) Register("Taser Duel", DuelKind.Taser, "Tasers that recharge in 1 second; first hit wins.");
        if (Cfg.QuickDrawEnabled) Register("Quick Draw", DuelKind.QuickDraw, "Back to back; shoot after DRAW! Shooting early loses.");
        if (Cfg.GunRouletteEnabled)
            Register("Gun Roulette", DuelKind.GunRoulette, $"{Math.Max(1, Cfg.GunRouletteGuns)} random guns, one shot each per gun; healthier player wins after the last gun.");
        DamageService.Register(Name, 99, OnDamage);
        Plugin.RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        Plugin.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        Plugin.RegisterEventHandler<EventDecoyStarted>(OnDecoyStarted);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        if (_taserUsers > 0) Cvars.Restore("mp_taser_recharge_time");
        _taserUsers = 0;
    }

    private void Register(string name, DuelKind kind, string description) =>
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = name, Category = "Weapon Battle", Description = description,
            OnStart = p =>
            {
                var s = S(p);
                Reset(s);
                s.Kind = kind;
                s.Active = true;
                Jb.LastRequests.DisplayOpponentSelection(p, OnOpponent);
            },
            OnEnd = OnEnd
        });

    private static void Reset(DuelState s)
    {
        LrKit.Kill(ref s.Timer);
        LrKit.Kill(ref s.TurnTimer);
        s.Kind = DuelKind.None;
        s.Active = s.Started = s.DrawCalled = false;
        s.OpponentSlot = s.TurnSlot = -1;
        s.Tick = s.GunIndex = s.ShotsThisGun = 0;
        s.Guns = new List<string>();
    }

    private void OnOpponent(JbPlayer p, JbPlayer o)
    {
        var s = S(p);
        if (!s.Active) return;
        s.OpponentSlot = o.Slot;
        Jb.LastRequests.StripWeapons(p, true);
        Jb.LastRequests.StripWeapons(o, true);
        if (s.Kind == DuelKind.QuickDraw)
        {
            QuickDrawSetup(p, o, s);
            return;
        }

        s.Tick = 3;
        LrKit.HudBoth(p, o, "lr.duel", Lang.T("lr.duel.starting", s.Tick), 45, 1.2f);
        var slot = p.Slot;
        s.Timer = Round.Timer(1f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            var op = LrKit.Opp(pl);
            if (!st.Active || op == null) return;
            if (--st.Tick > 0)
            {
                LrKit.HudBoth(pl, op, "lr.duel", Lang.T("lr.duel.starting", st.Tick), 45, 1.2f);
                return;
            }

            LrKit.Kill(ref st.Timer);
            Begin(pl, op, st);
        }, true);
    }

    private void Begin(JbPlayer p, JbPlayer o, DuelState s)
    {
        s.Started = true;
        LrKit.HudBoth(p, o, "lr.duel", Lang.T("lr.duel.go"), 45, 1.5f);
        switch (s.Kind)
        {
            case DuelKind.Dodgeball:
                foreach (var x in new[] { p, o }) LrKit.Give(x, "weapon_decoy");
                Jb.LastRequests.StartSlayTimer(p, 60);
                break;
            case DuelKind.Headshot:
                foreach (var x in new[] { p, o }) LrKit.SetClip(LrKit.Give(x, "weapon_deagle"), 7, 70);
                Jb.LastRequests.StartSlayTimer(p, 60);
                break;
            case DuelKind.GrenadeWar:
                foreach (var x in new[] { p, o })
                {
                    Movement.SetGravity(x.Controller, "lr.grenadewar", 0.5f);
                    LrKit.Give(x, "weapon_hegrenade");
                }

                Jb.LastRequests.StartSlayTimer(p, 60);
                break;
            case DuelKind.Taser:
                if (_taserUsers++ == 0) Cvars.SetTemp("mp_taser_recharge_time", "1");
                foreach (var x in new[] { p, o }) LrKit.Give(x, "weapon_taser");
                Jb.LastRequests.StartSlayTimer(p, 45);
                break;
            case DuelKind.GunRoulette:
                s.Guns = LrLogic.PickGuns(Cfg.GunRoulettePool, Math.Clamp(Cfg.GunRouletteGuns, 1, 20), Random.Shared);
                if (s.Guns.Count == 0) s.Guns.Add("weapon_deagle");
                s.GunIndex = 0;
                s.ShotsThisGun = 0;
                LrKit.TellBoth(p, o, "lr.roulette.how", s.Guns.Count);
                RouletteTurn(p, Random.Shared.Next(2) == 0 ? p : o);
                break;
        }
    }

    private void OnEnd(JbPlayer p, JbPlayer? o)
    {
        var s = S(p);
        if (!s.Active) return;
        var kind = s.Kind;
        var hadOpponent = s.OpponentSlot >= 0;
        Reset(s);
        if (kind == DuelKind.Taser && hadOpponent && _taserUsers > 0 && --_taserUsers == 0) Cvars.Restore("mp_taser_recharge_time");
        if (!hadOpponent) return;
        foreach (var x in new[] { p, o })
        {
            if (x?.Controller is not { IsValid: true } c) continue;
            Hud.Clear(c, "lr.duel");
            Movement.ClearGravity(c, "lr.grenadewar");
            if (!c.IsAliveP()) continue;
            if (c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            // The LR guns never outlive the LR: strip them and give back what the player had.
            Jb.LastRequests.StripWeapons(x);
            Jb.LastRequests.RestoreWeapons(x);
        }
    }

    /// <summary>The prisoner of the duel this player is in, when it is of the given kind and running.</summary>
    private static JbPlayer? Duel(CCSPlayerController? c, DuelKind kind)
    {
        var p = c?.Jb();
        if (p == null) return null;
        var owner = LrKit.Owner(p);
        if (owner == null) return null;
        var s = S(owner);
        return s.Active && s.Started && s.Kind == kind && s.OpponentSlot >= 0 ? owner : null;
    }

    private static bool Pair(CCSPlayerController a, CCSPlayerController b, JbPlayer owner) =>
        a.Slot == owner.Slot && b.Slot == S(owner).OpponentSlot || b.Slot == owner.Slot && a.Slot == S(owner).OpponentSlot;

    // ====================================================================== damage

    private HookResult OnDamage(DamageContext ctx)
    {
        if (ctx.Attacker == null || ctx.IsSelf || ctx.Victim.Jb() is not { } vp) return HookResult.Continue;
        var owner = LrKit.Owner(vp);
        if (owner == null) return HookResult.Continue;
        var s = S(owner);
        if (!s.Active || s.OpponentSlot < 0 || !Pair(ctx.Attacker, ctx.Victim, owner)) return HookResult.Continue;
        if (!s.Started) return HookResult.Handled;
        switch (s.Kind)
        {
            case DuelKind.Dodgeball:
                if (ctx.Weapon.Contains("decoy")) ctx.Damage = 999f;
                break;
            case DuelKind.Headshot:
                if ((ctx.Info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0 && ctx.HitGroup != HitGroup_t.HITGROUP_HEAD) return HookResult.Handled;
                break;
            case DuelKind.QuickDraw:
                if (!s.DrawCalled) return HookResult.Handled;
                ctx.Damage = 999f;
                break;
        }

        return HookResult.Continue;
    }

    // ====================================================================== grenades

    private HookResult OnGrenadeThrown(EventGrenadeThrown ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid) return HookResult.Continue;
        var weapon = ev.Weapon ?? "";
        string? refill = null;
        if (weapon.Contains("decoy") && Duel(c, DuelKind.Dodgeball) != null) refill = "weapon_decoy";
        else if (weapon.Contains("hegrenade") && Duel(c, DuelKind.GrenadeWar) != null) refill = "weapon_hegrenade";
        if (refill == null) return HookResult.Continue;
        var slot = c.Slot;
        var kind = refill == "weapon_decoy" ? DuelKind.Dodgeball : DuelKind.GrenadeWar;
        Round.Timer(refill == "weapon_decoy" ? 1f : 1.5f, () =>
        {
            var pc = Players.Get(slot).Controller;
            if (pc == null || !pc.IsAliveP() || Duel(pc, kind) == null || pc.HasWeapon(refill)) return;
            LrKit.Give(Players.Get(slot), refill);
        });
        return HookResult.Continue;
    }

    private HookResult OnDecoyStarted(EventDecoyStarted ev, GameEventInfo info)
    {
        if (Duel(ev.Userid, DuelKind.Dodgeball) == null) return HookResult.Continue;
        var e = Utilities.GetEntityFromIndex<CBaseEntity>(ev.Entityid);
        if (e is { IsValid: true }) e.AcceptInput("Kill");
        return HookResult.Continue;
    }

    // ====================================================================== tick: dodgeball hit check, quick draw

    private void OnTick()
    {
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.Active || s.OpponentSlot < 0) continue;
            if (s.Kind == DuelKind.Dodgeball && s.Started) DodgeballTick(p, s);
            else if (s.Kind == DuelKind.QuickDraw) QuickDrawTick(p, s);
        }
    }

    /// <summary>A decoy in flight inside the opponent's hull kills them (a CS2 decoy hit does not always arrive as damage).</summary>
    private static void DodgeballTick(JbPlayer p, DuelState s)
    {
        var o = Players.Get(s.OpponentSlot);
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("decoy_projectile"))
        {
            if (!e.IsValid || e.AbsOrigin == null) continue;
            var v = e.AbsVelocity;
            if (v.X * v.X + v.Y * v.Y + v.Z * v.Z < 150f * 150f) continue;
            var ownerPawn = e.OwnerEntity.Value;
            if (ownerPawn is not { IsValid: true } || ownerPawn.DesignerName != "player") continue;
            var thrower = ownerPawn.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>();
            if (thrower == null) continue;
            var victim = thrower.Slot == p.Slot ? o : thrower.Slot == o.Slot ? p : null;
            var vo = victim?.Controller?.PawnOrNull()?.AbsOrigin;
            if (victim == null || vo == null) continue;
            var pos = e.AbsOrigin;
            if (Math.Abs(pos.X - vo.X) > 20f || Math.Abs(pos.Y - vo.Y) > 20f || pos.Z < vo.Z - 4f || pos.Z > vo.Z + 76f) continue;
            e.AcceptInput("Kill");
            LrKit.Win(Players.Get(thrower.Slot), victim);
            return;
        }
    }

    // ====================================================================== Quick Draw

    private void QuickDrawSetup(JbPlayer p, JbPlayer o, DuelState s)
    {
        var pc = p.Controller!;
        var oc = o.Controller!;
        foreach (var x in new[] { p, o }) LrKit.SetClip(LrKit.Give(x, "weapon_deagle"), 2, 0);
        // Back to back: each faces straight away from the other, rooted to the spot.
        var a = pc.PawnOrNull()?.AbsOrigin;
        var b = oc.PawnOrNull()?.AbsOrigin;
        if (a != null && b != null)
        {
            var yawAwayA = MathF.Atan2(a.Y - b.Y, a.X - b.X) * 180f / MathF.PI;
            pc.PawnOrNull()!.Teleport(null, new QAngle(0, yawAwayA, 0), new Vector(0, 0, 0));
            oc.PawnOrNull()!.Teleport(null, new QAngle(0, yawAwayA + 180f, 0), new Vector(0, 0, 0));
        }

        pc.SetMoveType(MoveType_t.MOVETYPE_NONE);
        oc.SetMoveType(MoveType_t.MOVETYPE_NONE);
        s.Started = true;
        s.DrawCalled = false;
        s.ReadyAt = Server.CurrentTime + 1f; // a held trigger from before does not count as shooting early
        LrKit.HudBoth(p, o, "lr.duel", Lang.T("lr.quickdraw.ready"), 45, 8f);
        LrKit.TellBoth(p, o, "lr.quickdraw.how");
        var slot = p.Slot;
        s.Timer = Round.Timer(2f + (float)Random.Shared.NextDouble() * 4f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            var op = LrKit.Opp(pl);
            if (!st.Active || st.Kind != DuelKind.QuickDraw || op == null) return;
            st.DrawCalled = true;
            foreach (var x in new[] { pl, op })
            {
                var w = x.Controller?.ActiveWeapon();
                if (w == null) continue;
                w.NextPrimaryAttackTick = Server.TickCount;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }

            LrKit.HudBoth(pl, op, "lr.duel", Lang.T("lr.quickdraw.draw"), 60, 2f);
            Jb.LastRequests.StartSlayTimer(pl, 10);
        });
    }

    private static void QuickDrawTick(JbPlayer p, DuelState s)
    {
        if (!s.Started || s.DrawCalled) return;
        var o = Players.Get(s.OpponentSlot);
        foreach (var x in new[] { p, o })
        {
            var c = x.Controller;
            if (c == null || !c.IsAliveP()) continue;
            var w = c.ActiveWeapon();
            if (w != null && w.NextPrimaryAttackTick != int.MaxValue)
            {
                w.NextPrimaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }

            if (Server.CurrentTime < s.ReadyAt || (c.Buttons & PlayerButtons.Attack) == 0) continue;
            // Jumped the gun.
            var other = x.Slot == p.Slot ? o : p;
            Chat.TellAll("lr.quickdraw.early", x.Name);
            s.DrawCalled = true;
            LrKit.Win(other, x);
            return;
        }
    }

    // ====================================================================== Gun Roulette

    private void RouletteTurn(JbPlayer prisoner, JbPlayer shooter)
    {
        var s = S(prisoner);
        var o = LrKit.Opp(prisoner);
        if (!s.Active || o == null) return;
        LrKit.Kill(ref s.TurnTimer);
        if (s.GunIndex >= s.Guns.Count)
        {
            RouletteFinal(prisoner, o);
            return;
        }

        var other = shooter.Slot == prisoner.Slot ? o : prisoner;
        s.TurnSlot = shooter.Slot;
        Jb.LastRequests.StripWeapons(other);
        Jb.LastRequests.StripWeapons(shooter);
        var gun = s.Guns[s.GunIndex];
        LrKit.SetClip(LrKit.Give(shooter, gun), 1, 0);
        var html = Lang.T("lr.roulette.hud", s.GunIndex + 1, s.Guns.Count, Hud.Escape(Weapons.Display(gun)), Hud.Escape(shooter.Name));
        LrKit.HudBoth(prisoner, o, "lr.duel", html, 45, Math.Max(2, Cfg.GunRouletteTurnSeconds));
        var slot = prisoner.Slot;
        var shooterSlot = shooter.Slot;
        s.TurnTimer = Round.Timer(Math.Max(2, Cfg.GunRouletteTurnSeconds), () =>
        {
            var st = S(Players.Get(slot));
            if (!st.Active || st.TurnSlot != shooterSlot) return;
            LrKit.Tell(Players.Get(shooterSlot), "lr.roulette.too_slow");
            NextShot(Players.Get(slot));
        });
    }

    /// <summary>The current shooter fired (or ran out of time): the other shoots this gun, or the next gun comes up.</summary>
    private void NextShot(JbPlayer prisoner)
    {
        var s = S(prisoner);
        var o = LrKit.Opp(prisoner);
        if (!s.Active || o == null) return;
        var other = s.TurnSlot == prisoner.Slot ? o : prisoner;
        s.ShotsThisGun++;
        if (s.ShotsThisGun >= 2)
        {
            s.ShotsThisGun = 0;
            s.GunIndex++;
        }

        RouletteTurn(prisoner, other);
    }

    private void RouletteFinal(JbPlayer prisoner, JbPlayer o)
    {
        var s = S(prisoner);
        s.TurnSlot = -1;
        var hp = prisoner.Controller?.GetHp() ?? 0;
        var ho = o.Controller?.GetHp() ?? 0;
        var winner = LrLogic.FinalWinner(hp, ho, Random.Shared.Next(2)) == 0 ? prisoner : o;
        var loser = winner == prisoner ? o : prisoner;
        Chat.TellAll("lr.roulette.final", s.Guns.Count, winner.Name, winner == prisoner ? hp : ho, loser.Name, loser == prisoner ? hp : ho);
        LrKit.Win(winner, loser);
    }

    private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var owner = Duel(c, DuelKind.GunRoulette);
        if (owner == null || c == null) return HookResult.Continue;
        var s = S(owner);
        if (s.TurnSlot != c.Slot) return HookResult.Continue;
        var slot = owner.Slot;
        var shooter = c.Slot;
        s.TurnSlot = -2; // fired: wait for the bullet to land before the next turn
        LrKit.Kill(ref s.TurnTimer);
        Round.Timer(0.6f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            if (!st.Active || st.TurnSlot != -2) return;
            st.TurnSlot = shooter;
            NextShot(pl);
        });
        return HookResult.Continue;
    }
}
