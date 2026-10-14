using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

internal enum ContestXKind
{
    None,
    HotPotato,
    HideAndSeek,
    LongestJump,
    ChickenHunt,
    TargetPractice
}

internal sealed class ContestXState
{
    public ContestXKind Kind;
    public bool Active, Started;
    public int OpponentSlot = -1;
    public Timer? Timer, Fuse, Clock;
    public int Tick;

    // hot potato
    public int Holder = -1;
    public float PassAllowedAt;
    public GlowHandle? HolderGlow;

    // chicken hunt / target practice
    public readonly HashSet<uint> Chickens = new();
    public readonly List<(Vector3 Centre, List<CBeam> Beams)> Targets = new();
    public float EndsAt;
}

/// <summary>Per participant numbers (prisoner and guard).</summary>
internal sealed class ContestXPlayer
{
    public int Score, Attempts;
    public float Best;
    public bool Airborne, LeftGround;
    public Vector3 Takeoff;
    public readonly List<int> Shots = new();
}

/// <summary>
/// 0.15 contests (category "Contest"): Hot Potato (knife the other to pass the glowing potato; whoever holds it when the
/// hidden fuse runs out dies), Hide and Seek (the guard is blinded while the prisoner hides, then has SeekSeconds to knife
/// them or is slain), Longest Jump (best of LongestJumpAttempts jumps on level ground), Chicken Hunt (most stock CS2
/// chickens shot in ChickenHuntSeconds) and Target Practice (TargetShots Deagle shots at beam rings in the air, scored by
/// how close each shot passes the centre).
/// </summary>
public sealed class ContestExtraLastRequests : JbModule
{
    private LrExtraConfig Cfg => Section<LrExtraConfig>("LastRequestsExtra");

    public override string Name => "LastRequests.ContestExtra";
    public override int Order => 10;

    private static ContestXState S(JbPlayer p) => p.Data<ContestXState>();
    private static ContestXPlayer P(JbPlayer p) => p.Data<ContestXPlayer>();

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        if (Cfg.HotPotatoEnabled) Register("Hot Potato", ContestXKind.HotPotato, LrFlags.None, "Knife the other to pass the potato; whoever holds it when it blows up dies.");
        if (Cfg.HideAndSeekEnabled) Register("Hide and Seek", ContestXKind.HideAndSeek, LrFlags.NoBeacon | LrFlags.NoRadar, "You hide while the guard is blind; they must knife you in time.");
        if (Cfg.LongestJumpEnabled) Register("Longest Jump", ContestXKind.LongestJump, LrFlags.DontAllowDamagingOpponent, "Best of your jumps on level ground; longest wins.");
        if (Cfg.ChickenHuntEnabled) Register("Chicken Hunt", ContestXKind.ChickenHunt, LrFlags.DontAllowDamagingOpponent, "Shoot the most chickens before time runs out.");
        if (Cfg.TargetPracticeEnabled) Register("Target Practice", ContestXKind.TargetPractice, LrFlags.DontAllowDamagingOpponent, "Shoot the rings; closest to the centre scores most.");
        DamageService.Register(Name, 99, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerJump>(OnJump);
        Plugin.RegisterEventHandler<EventOtherDeath>(OnOtherDeath);
        Plugin.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        Plugin.RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload) => DamageService.Unregister(OnDamage);

    private void Register(string name, ContestXKind kind, LrFlags flags, string description) =>
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = name, Category = "Contest", Flags = flags, Description = description,
            OnStart = p =>
            {
                var s = S(p);
                Cleanup(s);
                s.Kind = kind;
                s.Active = true;
                Jb.LastRequests.DisplayOpponentSelection(p, OnOpponent);
            },
            OnEnd = OnEnd
        });

    private static void Cleanup(ContestXState s)
    {
        LrKit.Kill(ref s.Timer);
        LrKit.Kill(ref s.Fuse);
        LrKit.Kill(ref s.Clock);
        Glow.Remove(s.HolderGlow);
        s.HolderGlow = null;
        foreach (var raw in s.Chickens)
        {
            var e = new CHandle<CBaseEntity>(raw).Value;
            if (e is { IsValid: true }) e.AcceptInput("Kill");
        }

        s.Chickens.Clear();
        foreach (var (_, beams) in s.Targets)
            foreach (var b in beams) Fx.SafeRemove(b);
        s.Targets.Clear();
        s.Kind = ContestXKind.None;
        s.Active = s.Started = false;
        s.OpponentSlot = s.Holder = -1;
    }

    private static void ResetPlayer(JbPlayer p)
    {
        var d = P(p);
        d.Score = d.Attempts = 0;
        d.Best = 0;
        d.Airborne = false;
        d.Shots.Clear();
    }

    private void OnOpponent(JbPlayer p, JbPlayer o)
    {
        var s = S(p);
        if (!s.Active) return;
        s.OpponentSlot = o.Slot;
        ResetPlayer(p);
        ResetPlayer(o);
        switch (s.Kind)
        {
            case ContestXKind.HotPotato: HotPotatoStart(p, o, s); break;
            case ContestXKind.HideAndSeek: HideStart(p, o, s); break;
            case ContestXKind.LongestJump: JumpStart(p, o, s); break;
            case ContestXKind.ChickenHunt: ChickenStart(p, o, s); break;
            case ContestXKind.TargetPractice: TargetStart(p, o, s); break;
        }
    }

    private static void OnEnd(JbPlayer p, JbPlayer? o)
    {
        var s = S(p);
        if (!s.Active) return;
        var kind = s.Kind;
        var hadOpponent = s.OpponentSlot >= 0;
        Cleanup(s);
        if (!hadOpponent) return;
        foreach (var x in new[] { p, o })
        {
            if (x?.Controller is not { IsValid: true } c) continue;
            Hud.Clear(c, "lr.contestx");
            if (kind == ContestXKind.HideAndSeek) Fx.ClearFade(c);
            if (c.IsAliveP() && c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            if (c.IsAliveP() && kind is ContestXKind.HotPotato or ContestXKind.HideAndSeek or ContestXKind.ChickenHunt or ContestXKind.TargetPractice)
            {
                Jb.LastRequests.StripWeapons(x);
                Jb.LastRequests.RestoreWeapons(x);
            }

            ResetPlayer(x);
        }
    }

    /// <summary>The prisoner of the running contest of this kind the player is in.</summary>
    private static JbPlayer? Contest(CCSPlayerController? c, ContestXKind kind)
    {
        var p = c?.Jb();
        if (p == null) return null;
        var owner = LrKit.Owner(p);
        if (owner == null) return null;
        var s = S(owner);
        return s.Active && s.Started && s.Kind == kind && s.OpponentSlot >= 0 ? owner : null;
    }

    private static JbPlayer Other(JbPlayer owner, JbPlayer x) => x.Slot == owner.Slot ? Players.Get(S(owner).OpponentSlot) : owner;

    private void ShowScores(JbPlayer owner, string key)
    {
        var o = Players.Get(S(owner).OpponentSlot);
        var html = Lang.T(key, Hud.Escape(owner.Name), P(owner).Score, Hud.Escape(o.Name), P(o).Score,
            Math.Max(0, (int)MathF.Ceiling(S(owner).EndsAt - Server.CurrentTime)));
        LrKit.HudBoth(owner, o, "lr.contestx", html, 45, 1.2f);
    }

    /// <summary>Time is up: the higher score wins (a tie is a coin flip).</summary>
    private static void DecideByScore(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active || s.OpponentSlot < 0) return;
        var o = Players.Get(s.OpponentSlot);
        var a = P(owner).Score;
        var b = P(o).Score;
        var winner = LrLogic.FinalWinner(a, b, Random.Shared.Next(2)) == 0 ? owner : o;
        var loser = winner == owner ? o : owner;
        Chat.TellAll("lr.contestx.result", winner.Name, P(winner).Score, loser.Name, P(loser).Score);
        LrKit.Win(winner, loser);
    }

    // ====================================================================== damage

    private HookResult OnDamage(DamageContext ctx)
    {
        if (ctx.Attacker == null || ctx.IsSelf || ctx.Victim.Jb() is not { } vp) return HookResult.Continue;
        var owner = LrKit.Owner(vp);
        if (owner == null) return HookResult.Continue;
        var s = S(owner);
        if (!s.Active || s.OpponentSlot < 0) return HookResult.Continue;
        var pair = ctx.Attacker.Slot == owner.Slot && ctx.Victim.Slot == s.OpponentSlot || ctx.Victim.Slot == owner.Slot && ctx.Attacker.Slot == s.OpponentSlot;
        if (!pair) return HookResult.Continue;
        switch (s.Kind)
        {
            case ContestXKind.HotPotato:
                if (s.Started && ctx.IsKnife && ctx.Attacker.Slot == s.Holder && Server.CurrentTime >= s.PassAllowedAt) PassPotato(owner, ctx.Victim.Slot);
                return HookResult.Handled;
            case ContestXKind.HideAndSeek:
                // Only the seeker's knife counts, and it kills.
                if (!s.Started || ctx.Attacker.Slot != s.OpponentSlot || !ctx.IsKnife) return HookResult.Handled;
                ctx.Damage = 999f;
                return HookResult.Continue;
            default:
                return HookResult.Continue;
        }
    }

    // ====================================================================== Hot Potato

    private void HotPotatoStart(JbPlayer p, JbPlayer o, ContestXState s)
    {
        foreach (var x in new[] { p, o })
        {
            Jb.LastRequests.StripWeapons(x, true);
            LrKit.Give(x, x.Controller!.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t");
        }

        s.Started = true;
        var lo = Math.Max(5, Math.Min(Cfg.HotPotatoMinSeconds, Cfg.HotPotatoMaxSeconds));
        var hi = Math.Max(lo, Cfg.HotPotatoMaxSeconds);
        LrKit.TellBoth(p, o, "lr.potato.how");
        PassPotato(p, Random.Shared.Next(2) == 0 ? p.Slot : o.Slot);
        var slot = p.Slot;
        s.Fuse = Round.Timer(lo + (float)Random.Shared.NextDouble() * (hi - lo), () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            if (!st.Active || st.Kind != ContestXKind.HotPotato || st.Holder < 0) return;
            var holder = Players.Get(st.Holder);
            Chat.TellAll("lr.potato.boom", holder.Name);
            if (holder.Controller?.PawnOrNull()?.AbsOrigin is { } at) Fx.PulseAt(at, Color.FromArgb(255, 255, 90, 20), 10f, 200f, 0.4f);
            LrKit.Win(Other(pl, holder), holder);
        });
    }

    private static void PassPotato(JbPlayer owner, int toSlot)
    {
        var s = S(owner);
        s.Holder = toSlot;
        s.PassAllowedAt = Server.CurrentTime + 1f; // no instant tag-back
        Glow.Remove(s.HolderGlow);
        var holder = Players.Get(toSlot);
        s.HolderGlow = holder.Controller is { } hc ? Glow.Attach(hc, Color.FromArgb(255, 255, 90, 20)) : null;
        var other = Other(owner, holder);
        Hud.Set(holder.Controller, "lr.contestx", Lang.T("lr.potato.hud_holder"), 45, 30f);
        Hud.Set(other.Controller, "lr.contestx", Lang.T("lr.potato.hud_run", Hud.Escape(holder.Name)), 45, 30f);
    }

    // ====================================================================== Hide and Seek

    private void HideStart(JbPlayer p, JbPlayer o, ContestXState s)
    {
        Jb.LastRequests.StripWeapons(p, true);
        Jb.LastRequests.StripWeapons(o, true);
        LrKit.Give(p, "weapon_knife_t");
        var gc = o.Controller!;
        Fx.Fade(gc, Color.FromArgb(255, 0, 0, 0), 0f, 0f, Fx.FadeStayOut | Fx.FadePurge);
        gc.SetMoveType(MoveType_t.MOVETYPE_NONE);
        s.Tick = Math.Clamp(Cfg.HideSeconds, 5, 90);
        LrKit.TellBoth(p, o, "lr.hide.how", s.Tick, Math.Clamp(Cfg.SeekSeconds, 15, 180));
        var slot = p.Slot;
        s.Timer = Round.Timer(1f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            var op = LrKit.Opp(pl);
            if (!st.Active || op == null) return;
            if (--st.Tick > 0)
            {
                LrKit.HudBoth(pl, op, "lr.contestx", Lang.T("lr.hide.hiding", st.Tick), 45, 1.2f);
                return;
            }

            LrKit.Kill(ref st.Timer);
            var oc = op.Controller!;
            Fx.Fade(oc, Color.FromArgb(255, 0, 0, 0), 1f, 0f, Fx.FadeIn | Fx.FadePurge);
            oc.SetMoveType(MoveType_t.MOVETYPE_WALK);
            LrKit.Give(op, "weapon_knife");
            st.Started = true;
            LrKit.HudBoth(pl, op, "lr.contestx", Lang.T("lr.hide.seek"), 45, 2f);
            Jb.LastRequests.StartSlayTimer(pl, Math.Clamp(Cfg.SeekSeconds, 15, 180), SlayTimerFlags.Guard);
        }, true);
    }

    // ====================================================================== Longest Jump

    private void JumpStart(JbPlayer p, JbPlayer o, ContestXState s)
    {
        s.Started = true;
        s.EndsAt = Server.CurrentTime + 45f;
        LrKit.TellBoth(p, o, "lr.jump.how", Math.Clamp(Cfg.LongestJumpAttempts, 1, 10));
        var slot = p.Slot;
        s.Timer = Round.Timer(45f, () => JumpDecide(Players.Get(slot)));
    }

    private HookResult OnJump(EventPlayerJump ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var owner = Contest(c, ContestXKind.LongestJump);
        if (owner == null || c == null) return HookResult.Continue;
        var d = P(c.Jb()!);
        if (d.Attempts >= Math.Clamp(Cfg.LongestJumpAttempts, 1, 10)) return HookResult.Continue;
        var o = c.PawnOrNull()?.AbsOrigin;
        if (o == null) return HookResult.Continue;
        d.Airborne = true;
        d.LeftGround = false;
        d.Takeoff = new Vector3(o.X, o.Y, o.Z);
        return HookResult.Continue;
    }

    private void JumpTick(JbPlayer owner)
    {
        var max = Math.Clamp(Cfg.LongestJumpAttempts, 1, 10);
        foreach (var x in new[] { owner, Players.Get(S(owner).OpponentSlot) })
        {
            var c = x.Controller;
            var d = P(x);
            if (c == null || !d.Airborne) continue;
            // The jump event comes while the player is still on the ground: land only after really leaving it.
            if (!c.OnGround())
            {
                d.LeftGround = true;
                continue;
            }

            if (!d.LeftGround) continue;
            d.Airborne = d.LeftGround = false;
            var o = c.PawnOrNull()?.AbsOrigin;
            if (o == null) continue;
            d.Attempts++;
            var dist = LrLogic.JumpDistance(d.Takeoff, new Vector3(o.X, o.Y, o.Z), 16f);
            if (dist == null)
            {
                LrKit.Tell(x, "lr.jump.invalid", d.Attempts, max);
                continue;
            }

            d.Best = MathF.Max(d.Best, dist.Value);
            d.Score = (int)MathF.Round(d.Best);
            Chat.TellAll("lr.jump.result", x.Name, dist.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), d.Attempts, max);
        }

        var other = Players.Get(S(owner).OpponentSlot);
        if (P(owner).Attempts >= max && P(other).Attempts >= max) JumpDecide(owner);
    }

    private void JumpDecide(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active || s.Kind != ContestXKind.LongestJump || s.OpponentSlot < 0) return;
        LrKit.Kill(ref s.Timer);
        var o = Players.Get(s.OpponentSlot);
        // Compare to a tenth of a unit, then a coin.
        var a = (int)MathF.Round(P(owner).Best * 10);
        var b = (int)MathF.Round(P(o).Best * 10);
        var winner = LrLogic.FinalWinner(a, b, Random.Shared.Next(2)) == 0 ? owner : o;
        var loser = winner == owner ? o : owner;
        Chat.TellAll("lr.jump.final", winner.Name, P(winner).Best.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            loser.Name, P(loser).Best.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        LrKit.Win(winner, loser);
    }

    // ====================================================================== Chicken Hunt

    private void ChickenStart(JbPlayer p, JbPlayer o, ContestXState s)
    {
        var pc = p.Controller!;
        var pawn = pc.PawnOrNull();
        var origin = pawn?.AbsOrigin;
        if (pawn == null || origin == null) return;
        foreach (var x in new[] { p, o })
        {
            Jb.LastRequests.StripWeapons(x, true);
            LrKit.SetClip(LrKit.Give(x, "weapon_deagle"), 7, 70);
        }

        var count = Math.Clamp(Cfg.ChickenCount, 3, 40);
        for (var i = 0; i < count; i++)
        {
            var pos = SpawnPoint(origin, pawn, 150f, 550f);
            var chicken = Utilities.CreateEntityByName<CBaseEntity>("chicken");
            if (chicken == null) continue;
            chicken.Teleport(pos, new QAngle(0, Random.Shared.Next(360), 0), new Vector(0, 0, 0));
            chicken.DispatchSpawn();
            s.Chickens.Add(chicken.EntityHandle.Raw);
        }

        s.Started = true;
        var seconds = Math.Clamp(Cfg.ChickenHuntSeconds, 10, 120);
        s.EndsAt = Server.CurrentTime + seconds;
        LrKit.TellBoth(p, o, "lr.chicken.how", s.Chickens.Count, seconds);
        var slot = p.Slot;
        s.Clock = Round.Timer(0.5f, () =>
        {
            var pl = Players.Get(slot);
            if (S(pl).Active && S(pl).Kind == ContestXKind.ChickenHunt) ShowScores(pl, "lr.chicken.hud");
        }, true);
        s.Timer = Round.Timer(seconds, () => DecideByScore(Players.Get(slot)));
    }

    /// <summary>A floor point min..max units from the origin, never through a wall.</summary>
    private static Vector SpawnPoint(Vector origin, CBaseEntity ignore, float min, float max)
    {
        var angle = Random.Shared.NextDouble() * Math.PI * 2;
        var dist = min + (float)Random.Shared.NextDouble() * (max - min);
        var eye = new Vector(origin.X, origin.Y, origin.Z + 40f);
        var target = new Vector(origin.X + (float)Math.Cos(angle) * dist, origin.Y + (float)Math.Sin(angle) * dist, origin.Z + 40f);
        if (Aim.WorldHit(eye, target, ignore) is { } wall)
        {
            var dx = wall.X - eye.X;
            var dy = wall.Y - eye.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            var k = len <= 40f ? 0f : (len - 30f) / len;
            target = new Vector(eye.X + dx * k, eye.Y + dy * k, eye.Z);
        }

        var floor = Aim.WorldHit(target, new Vector(target.X, target.Y, target.Z - 400f), ignore);
        return floor != null ? new Vector(floor.X, floor.Y, floor.Z + 4f) : new Vector(target.X, target.Y, origin.Z + 4f);
    }

    private HookResult OnOtherDeath(EventOtherDeath ev, GameEventInfo info)
    {
        if (!string.Equals(ev.Othertype, "chicken", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;
        var attacker = Utilities.GetPlayerFromUserid(ev.Attacker);
        var owner = Contest(attacker, ContestXKind.ChickenHunt);
        if (owner == null || attacker == null) return HookResult.Continue;
        var chicken = Utilities.GetEntityFromIndex<CBaseEntity>(ev.Otherid);
        if (chicken == null || !S(owner).Chickens.Remove(chicken.EntityHandle.Raw)) return HookResult.Continue;
        P(attacker.Jb()!).Score++;
        ShowScores(owner, "lr.chicken.hud");
        if (S(owner).Chickens.Count == 0) DecideByScore(owner);
        return HookResult.Continue;
    }

    // ====================================================================== Target Practice

    private void TargetStart(JbPlayer p, JbPlayer o, ContestXState s)
    {
        var pc = p.Controller!;
        var pawn = pc.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        var shots = Math.Clamp(Cfg.TargetShots, 3, 30);
        foreach (var x in new[] { p, o })
        {
            Jb.LastRequests.StripWeapons(x, true);
            var w = LrKit.Give(x, "weapon_deagle");
            LrKit.SetClip(w, Math.Min(shots, 7), Math.Max(0, shots - 7));
        }

        var eye = pc.EyePosition();
        var yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        var fwd = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        var from = new Vector3(eye.X, eye.Y, eye.Z);
        for (var i = 0; i < 5; i++)
        {
            var dist = 350f + (float)Random.Shared.NextDouble() * 350f;
            var side = -220f + (float)Random.Shared.NextDouble() * 440f;
            var up = (float)Random.Shared.NextDouble() * 120f;
            var centre = from + fwd * dist + right * side + new Vector3(0, 0, up);
            if (Aim.WorldHit(new Vector(from.X, from.Y, from.Z), new Vector(centre.X, centre.Y, centre.Z), pawn) is { } wall)
            {
                var hit = new Vector3(wall.X, wall.Y, wall.Z);
                centre = from + (hit - from) * 0.8f; // in front of the wall
            }

            s.Targets.Add((centre, DrawTarget(centre, fwd)));
        }

        s.Started = true;
        s.EndsAt = Server.CurrentTime + 40f;
        LrKit.TellBoth(p, o, "lr.target.how", shots);
        var slot = p.Slot;
        s.Clock = Round.Timer(0.5f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            if (!st.Active || st.Kind != ContestXKind.TargetPractice) return;
            var op = Players.Get(st.OpponentSlot);
            P(pl).Score = P(pl).Shots.Sum();
            P(op).Score = P(op).Shots.Sum();
            ShowScores(pl, "lr.target.hud");
        }, true);
        s.Timer = Round.Timer(40f, () => TargetDecide(Players.Get(slot)));
    }

    /// <summary>Two upright rings (radius 30 and 8) facing the shooters.</summary>
    private static List<CBeam> DrawTarget(Vector3 centre, Vector3 facing)
    {
        var beams = new List<CBeam>();
        var right = Vector3.Normalize(new Vector3(-facing.Y, facing.X, 0f));
        var up = Vector3.UnitZ;
        foreach (var (radius, color) in new[] { (30f, Color.FromArgb(255, 255, 255, 255)), (8f, Color.FromArgb(255, 255, 40, 40)) })
        {
            const int segments = 16;
            for (var i = 0; i < segments; i++)
            {
                var a0 = i * MathF.PI * 2 / segments;
                var a1 = (i + 1) * MathF.PI * 2 / segments;
                var p0 = centre + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * radius;
                var p1 = centre + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * radius;
                if (Fx.Beam(new Vector(p0.X, p0.Y, p0.Z), new Vector(p1.X, p1.Y, p1.Z), color, 1.5f) is { } b) beams.Add(b);
            }
        }

        return beams;
    }

    private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var owner = Contest(c, ContestXKind.TargetPractice);
        if (owner == null || c == null) return HookResult.Continue;
        var d = P(c.Jb()!);
        if (d.Shots.Count >= Math.Clamp(Cfg.TargetShots, 3, 30)) return HookResult.Continue;
        d.Shots.Add(0);
        if (d.Shots.Count >= Math.Clamp(Cfg.TargetShots, 3, 30))
        {
            var other = Other(owner, c.Jb()!);
            if (P(other).Shots.Count >= Math.Clamp(Cfg.TargetShots, 3, 30))
            {
                var slot = owner.Slot;
                Round.Timer(0.5f, () => TargetDecide(Players.Get(slot))); // let the last bullet land
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnBulletImpact(EventBulletImpact ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var owner = Contest(c, ContestXKind.TargetPractice);
        if (owner == null || c == null) return HookResult.Continue;
        var d = P(c.Jb()!);
        if (d.Shots.Count == 0) return HookResult.Continue;
        var eye = c.EyePosition();
        var from = new Vector3(eye.X, eye.Y, eye.Z);
        var to = new Vector3(ev.X, ev.Y, ev.Z);
        var best = S(owner).Targets.Select(t => LrLogic.TargetScore(LrLogic.SegmentDistance(from, to, t.Centre))).DefaultIfEmpty(0).Max();
        if (best > d.Shots[^1]) d.Shots[^1] = best;
        return HookResult.Continue;
    }

    private static void TargetDecide(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active || s.Kind != ContestXKind.TargetPractice || s.OpponentSlot < 0) return;
        var o = Players.Get(s.OpponentSlot);
        P(owner).Score = P(owner).Shots.Sum();
        P(o).Score = P(o).Shots.Sum();
        DecideByScore(owner);
    }

    // ====================================================================== tick

    private void OnTick()
    {
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.Active || !s.Started || s.OpponentSlot < 0) continue;
            if (s.Kind == ContestXKind.LongestJump) JumpTick(p);
        }
    }
}
