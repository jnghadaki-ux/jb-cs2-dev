using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

public sealed partial class SabotageModule
{
    private static readonly string[] MovementKeys =
        { "sab.heavy", "sab.gravity", "sab.frost", "sab.light", "sab.impulse", "sab.adrenaline", "sab.ghost", "sab.riot" };

    private int _tripSlot = -1;
    private int _lastSmoke = -1;
    private GlowHandle? _glowstick;
    private int _glowWardenSlot = -1;
    private readonly Dictionary<int, (System.Numerics.Vector3 Pos, float Since)> _still = new();
    private readonly HashSet<int> _secondWindUsed = new();
    private readonly HashSet<int> _adrenalineUsed = new();
    private readonly HashSet<int> _doubleJumpUsed = new();
    private readonly Dictionary<int, HashSet<string>> _scavenged = new();

    private static System.Numerics.Vector3 V3(Vector v) => new(v.X, v.Y, v.Z);

    /// <summary>Run <paramref name="action"/> again and again with a random wait of min-max seconds until the shop stops.</summary>
    private void Every(float min, float max, Action action)
    {
        void Schedule()
        {
            if (_stopped) return;
            var t = Round.Timer(SabotageRules.Interval(min, max, Random.Shared), () =>
            {
                if (_stopped) return;
                try { action(); }
                catch (Exception ex) { Plugin.Logger.LogWarning(ex, "[Sabotage] effect failed"); }
                Schedule();
            });
            if (t != null) _timers.Add(t);
        }

        Schedule();
    }

    private void After(float seconds, Action action)
    {
        var t = Round.Timer(seconds, () => { if (!_stopped) action(); });
        if (t != null) _timers.Add(t);
    }

    private static CCSPlayerController? RandomAliveGuard(int avoidSlot = -1)
    {
        var guards = Players.AliveGuards().ToList();
        if (guards.Count == 0) return null;
        var pool = guards.Count > 1 ? guards.Where(g => g.Slot != avoidSlot).ToList() : guards;
        return pool[Random.Shared.Next(pool.Count)];
    }

    private static void GuardNotice(CCSPlayerController g, string key) =>
        Hud.Set(g, "sabotage.hit", Hud.Bold(Hud.Color(Hud.Escape(Lang.T(key)), "#FF6A4A")), 30, 2.5f);

    // ================================================================= against the guards

    private void StartAgainstGuards(string id)
    {
        switch (id)
        {
            case "smoke":
                Every(35, 50, () =>
                {
                    if (RandomAliveGuard(_lastSmoke) is not { } g) return;
                    _lastSmoke = g.Slot;
                    Fx.Fade(g, Color.FromArgb(215, 25, 25, 25), 0.6f, 4f, Fx.FadeIn | Fx.FadePurge);
                    GuardNotice(g, "sab.notice.smoke");
                });
                break;
            case "badtrip":
                PassBadTrip();
                var trip = Round.Timer(1.5f, BadTripTick, repeat: true);
                if (trip != null) _timers.Add(trip);
                break;
            case "hotfloor":
                Every(45, 45, HotFloor);
                break;
            case "heavyboots":
                foreach (var g in Players.AliveGuards()) Movement.SetSpeed(g, "sab.heavy", 0.92f);
                break;
            case "jammed":
                Every(40, 40, () =>
                {
                    WardenJam(true);
                    if (Jb.Warden.Warden?.Controller is { IsValid: true } w) GuardNotice(w, "sab.notice.jammed");
                    After(10f, () => WardenJam(false));
                });
                break;
            case "paranoia":
                Every(30, 60, () =>
                {
                    if (RandomAliveGuard() is not { } g || Cfg.ParanoiaSounds.Count == 0) return;
                    Sound.Play(g, Cfg.ParanoiaSounds[Random.Shared.Next(Cfg.ParanoiaSounds.Count)], 0.8f);
                });
                break;
            case "gravity":
                foreach (var g in Players.AliveGuards()) Movement.SetGravity(g, "sab.gravity", 1.15f);
                break;
            case "rustyguns":
                foreach (var g in Players.AliveGuards()) Rust(g);
                break;
            case "butterfingers":
                Every(60, 60, () =>
                {
                    var g = Players.AliveGuards().Where(HasPistol).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                    if (g == null) return;
                    g.ExecuteClientCommand("slot2");
                    Server.NextFrame(() => { if (g.IsValid && g.IsAliveP()) g.DropActiveWeapon(); });
                    GuardNotice(g, "sab.notice.butterfingers");
                });
                break;
            case "echo":
                Every(20, 20, () =>
                {
                    var glows = Players.AliveGuards().Select(g => Glow.Attach(g, Color.FromArgb(255, 255, 60, 60), v => v.Team == CsTeam.Terrorist)).ToList();
                    After(3f, () => { foreach (var h in glows) Glow.Remove(h); });
                });
                break;
            case "glowstick":
                RefreshGlowstick();
                break;
            case "blackout":
                Every(45, 45, () =>
                {
                    foreach (var g in Players.AliveGuards()) Fx.Fade(g, Color.FromArgb(245, 0, 0, 0), 0.5f, 3f, Fx.FadeIn | Fx.FadePurge);
                });
                break;
        }
    }

    private void PassBadTrip()
    {
        var current = _tripSlot >= 0 ? Players.Get(_tripSlot).Controller : null;
        if (current is { IsValid: true } && current.IsAliveP() && current.Team == CsTeam.CounterTerrorist) return;
        if (current is { IsValid: true }) Fx.ClearFade(current);
        _tripSlot = RandomAliveGuard()?.Slot ?? -1;
        if (_tripSlot >= 0 && Players.Get(_tripSlot).Controller is { } g) GuardNotice(g, "sab.notice.badtrip");
    }

    private void BadTripTick()
    {
        if (_stopped || !_active.Contains("badtrip")) return;
        PassBadTrip();
        if (_tripSlot < 0 || Players.Get(_tripSlot).Controller is not { IsValid: true } g) return;
        var r = Random.Shared;
        Fx.Shake(g, 2.5f, 3f, 1.4f);
        Fx.Fade(g, Color.FromArgb(55, r.Next(256), r.Next(256), r.Next(256)), 1.4f, 0f, Fx.FadeIn | Fx.FadePurge);
    }

    private void StopBadTrip()
    {
        if (_tripSlot >= 0 && Players.Get(_tripSlot).Controller is { IsValid: true } g) Fx.ClearFade(g);
        _tripSlot = -1;
    }

    /// <summary>Burn the guard who has stood still the longest for 3 s: 2 HP every 0.5 s, never below 1 HP.</summary>
    private void HotFloor()
    {
        var target = Players.AliveGuards().Where(g => _still.ContainsKey(g.Slot)).OrderBy(g => _still[g.Slot].Since).FirstOrDefault()
                     ?? RandomAliveGuard();
        if (target == null) return;
        var slot = target.Slot;
        var particle = Section<Admin.AdminConfig>("Admin").BurnParticle;
        if (target.PawnOrNull()?.AbsOrigin is { } o)
        {
            var fx = Fx.Particle(particle, new Vector(o.X, o.Y, o.Z + 30f), 3.5f);
            fx?.AcceptInput("SetParent", target.PawnOrNull(), target.PawnOrNull(), "!activator");
        }

        target.SetColor(Color.FromArgb(255, 255, 140, 40));
        GuardNotice(target, "sab.notice.hotfloor");
        for (var i = 1; i <= 6; i++)
            After(0.5f * i, () =>
            {
                if (Players.Get(slot).Controller is not { IsValid: true } c || !c.IsAliveP()) return;
                c.SetHp(Math.Max(1, c.GetHp() - 2), false);
            });
        After(3.2f, () => { if (Players.Get(slot).Controller is { IsValid: true } c) c.SetColor(Color.White); });
        _still.Remove(slot);
    }

    private static bool HasPistol(CCSPlayerController g)
    {
        var weapons = g.PawnOrNull()?.WeaponServices?.MyWeapons;
        return weapons != null && weapons.Any(w => w.Value is { IsValid: true } x && Weapons.IsPistol(x.DesignerName));
    }

    private static void Rust(CCSPlayerController g)
    {
        var weapons = g.PawnOrNull()?.WeaponServices?.MyWeapons;
        if (weapons == null) return;
        foreach (var h in weapons)
        {
            if (h.Value is not { IsValid: true } w || Weapons.IsKnife(w.DesignerName) || Weapons.IsGrenade(w.DesignerName)) continue;
            var reserve = w.ReserveAmmo[0];
            if (reserve > 0) w.SetAmmo(-1, (int)(reserve * 0.6f));
        }
    }

    private void RefreshGlowstick()
    {
        if (_stopped || !_active.Contains("glowstick")) return;
        var w = Jb.Warden.Warden?.Controller;
        var slot = w is { IsValid: true } && w.IsAliveP() ? w.Slot : -1;
        if (slot == _glowWardenSlot && _glowstick != null) return;
        if (_glowstick != null) { Glow.Remove(_glowstick); _glowstick = null; }
        _glowWardenSlot = slot;
        if (slot >= 0 && w != null) _glowstick = Glow.Attach(w, Color.FromArgb(255, 80, 255, 120), v => v.Team == CsTeam.Terrorist);
    }

    private static void WardenJam(bool on) => Warden.WardenModule.PointerJammedUntil = on ? Server.CurrentTime + 10f : 0f;

    // ================================================================= for yourself

    private void StartSelf(CCSPlayerController c, string id)
    {
        switch (id)
        {
            case "thickskin": c.SetHp(Math.Min(115, c.GetHp() + 15)); break;
            case "lightfeet": Movement.SetSpeed(c, "sab.light", 1.08f); break;
            case "contraband": c.Give("weapon_flashbang"); break;
        }
    }

    private void StartAura(string id)
    {
        if (id != "underground") return;
        var wait = Math.Max(1f, 180f - Round.Elapsed);
        After(wait, () =>
        {
            var doors = Utilities.GetAllEntities()
                .Where(e => e is { IsValid: true } && e.DesignerName is "func_door" or "func_door_rotating" or "prop_door_rotating" or "func_breakable")
                .ToList();
            if (doors.Count == 0) return;
            var pick = doors[Random.Shared.Next(doors.Count)];
            pick.AcceptInput(pick.DesignerName == "func_breakable" ? "Break" : "Open");
            Chat.TellAll("sab.underground");
        });
    }

    /// <summary>Per-tick upkeep: regen, crouch speed, double-jump reset, riot speed, still-standing tracking, warden glow.</summary>
    private void TickPerks()
    {
        var tick = Server.TickCount;
        if (tick % 8 == 0)
        {
            foreach (var (slot, set) in _perks)
            {
                if (Players.Get(slot).Controller is not { IsValid: true } c || !c.IsAliveP()) continue;
                if (set.Contains("ghoststep"))
                {
                    var ducking = c.PawnOrNull() is { } p && (p.Flags & (uint)PlayerFlags.FL_DUCKING) != 0 && c.OnGround();
                    if (ducking) Movement.SetSpeed(c, "sab.ghost", 1.25f);
                    else Movement.ClearSpeed(c, "sab.ghost");
                }

                if (set.Contains("doublejump") && c.OnGround()) _doubleJumpUsed.Remove(slot);
            }
        }

        if (tick % 192 == 0)
            foreach (var (slot, set) in _perks)
                if (set.Contains("thickskin") && Players.Get(slot).Controller is { IsValid: true } c && c.IsAliveP() && c.GetHp() < 115)
                    c.AddHp(1, 115);

        if (tick % 64 == 0)
        {
            if (_active.Contains("hotfloor"))
            {
                var now = Server.CurrentTime;
                foreach (var g in Players.AliveGuards())
                {
                    if (g.PawnOrNull()?.AbsOrigin is not { } o) continue;
                    var pos = V3(o);
                    if (!_still.TryGetValue(g.Slot, out var s) || System.Numerics.Vector3.Distance(s.Pos, pos) > 24f) _still[g.Slot] = (pos, now);
                }
            }

            if (_active.Contains("glowstick")) RefreshGlowstick();
            if (_auras.Contains("riot"))
            {
                var rebels = Players.AlivePrisoners().Count(c => c.Jb() is { } p && Jb.Rebels.IsRebel(p));
                foreach (var t in Players.AlivePrisoners())
                    if (rebels >= 3) Movement.SetSpeed(t, "sab.riot", 1.05f);
                    else Movement.ClearSpeed(t, "sab.riot");
            }
        }
    }

    private void OnButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if ((pressed & PlayerButtons.Jump) == 0 || !Has(player, "doublejump") || player.OnGround() || !player.IsAliveP()) return;
        if (!_doubleJumpUsed.Add(player.Slot)) return;
        var v = player.PawnOrNull()?.AbsVelocity;
        if (v == null) return;
        player.SetVelocity(new Vector(v.X, v.Y, 300f));
    }

    [GameEventHandler]
    public HookResult OnJump(EventPlayerJump ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !Has(c, "longjump")) return HookResult.Continue;
        Server.NextFrame(() =>
        {
            if (!c.IsValid || c.PawnOrNull()?.AbsVelocity is not { } v) return;
            var flat = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            if (flat < 200f) return; // only running jumps
            var scale = Math.Min(1.25f, 650f / flat);
            c.SetVelocity(new Vector(v.X * scale, v.Y * scale, v.Z));
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || c.Team != CsTeam.CounterTerrorist || !Active("static") || Random.Shared.Next(8) != 0) return HookResult.Continue;
        if (Weapons.IsKnife(ev.Weapon ?? "")) return HookResult.Continue;
        Fx.Fade(c, Color.FromArgb(230, 255, 255, 255), 0.3f, 0.05f, Fx.FadeIn | Fx.FadePurge);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !Has(c, "scavenger")) return HookResult.Continue;
        var item = ev.Item ?? "";
        if (item.Length == 0 || Weapons.IsKnife("weapon_" + item)) return HookResult.Continue;
        if (!_scavenged.TryGetValue(c.Slot, out var seen)) _scavenged[c.Slot] = seen = new HashSet<string>();
        if (seen.Add(item)) c.AddHp(10, 100);
        return HookResult.Continue;
    }

    public override void OnPlayerSpawn(JbPlayer player)
    {
        if (_stopped || player.Controller is not { IsValid: true } c || c.Team != CsTeam.CounterTerrorist) return;
        if (_active.Contains("heavyboots")) Movement.SetSpeed(c, "sab.heavy", 0.92f);
        if (_active.Contains("gravity")) Movement.SetGravity(c, "sab.gravity", 1.15f);
        if (_active.Contains("rustyguns")) After(1f, () => { if (c.IsValid && c.IsAliveP()) Rust(c); });
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        _perks.Remove(victim.Slot); // perks last until you die
        if (victim.Controller is { IsValid: true } vc)
            foreach (var key in MovementKeys) { Movement.ClearSpeed(vc, key); Movement.ClearGravity(vc, key); }
        if (victim.Slot == _tripSlot) PassBadTrip();
        if (!AuraOn("solidarity") || victim.Controller is not { } dead || dead.Team != CsTeam.Terrorist || dead.PawnOrNull()?.AbsOrigin is not { } o) return;
        var at = V3(o);
        foreach (var t in Players.AlivePrisoners())
            if (t.PawnOrNull()?.AbsOrigin is { } p && System.Numerics.Vector3.Distance(V3(p), at) <= 300f) t.AddHp(15, 100);
    }

    // ================================================================= damage

    private HookResult OnDamage(DamageContext ctx)
    {
        if (_stopped) return HookResult.Continue;
        var victim = ctx.Victim;
        var attacker = ctx.Attacker;
        var guardHitsPrisoner = attacker != null && attacker.Team == CsTeam.CounterTerrorist && victim.Team == CsTeam.Terrorist;
        var prisonerHitsGuard = attacker != null && attacker.Team == CsTeam.Terrorist && victim.Team == CsTeam.CounterTerrorist;

        // ---- victim's own perks / skills
        if (_perks.TryGetValue(victim.Slot, out var mine))
        {
            if (ctx.IsFall && mine.Contains("ironlungs")) { ctx.Damage = 0; return HookResult.Continue; }
            if (ctx.IsFall && mine.Contains("lightfeet")) ctx.Damage *= 0.85f;
            if (mine.Contains("ironlungs") && (ctx.Info.BitsDamageType & DamageTypes_t.DMG_DROWN) != 0) ctx.Damage *= 0.5f;
            if (guardHitsPrisoner && mine.Contains("shadow") && ctx.VictimPawn is { } vp && (vp.Flags & (uint)PlayerFlags.FL_DUCKING) != 0) ctx.Damage *= 0.85f;
            if (attacker != null && !ctx.IsSelf && mine.Contains("lucky") && Random.Shared.Next(10) == 0)
            {
                ctx.Damage = 0;
                Hud.Set(victim, "sabotage.hit", Hud.Bold(Hud.Color(Hud.Escape(Lang.T("sab.notice.lucky")), "#7CFF7C")), 30, 1.5f);
                return HookResult.Continue;
            }

            var hp = victim.GetHp();
            var after = hp - (int)ctx.Damage;
            if (mine.Contains("secondwind") && after < 20 && _secondWindUsed.Add(victim.Slot))
            {
                ctx.Damage = Math.Max(0, hp - 50);
                var slot = victim.Slot;
                Server.NextFrame(() => { if (Players.Get(slot).Controller is { IsValid: true } c && c.IsAliveP() && c.GetHp() < 50) c.SetHp(50, false); });
                Hud.Set(victim, "sabotage.hit", Hud.Bold(Hud.Color(Hud.Escape(Lang.T("sab.notice.secondwind")), "#7CFF7C")), 30, 2f);
                after = 50;
            }

            if (mine.Contains("impulse") && ctx.Damage > 0)
            {
                Movement.SetSpeed(victim, "sab.impulse", 1.2f);
                var slot = victim.Slot;
                After(1.5f, () => Movement.ClearSpeed(Players.Get(slot).Controller, "sab.impulse"));
            }

            if (mine.Contains("adrenaline") && after is > 0 and < 30 && _adrenalineUsed.Add(victim.Slot))
            {
                Movement.SetSpeed(victim, "sab.adrenaline", 1.15f);
                var slot = victim.Slot;
                for (var i = 1; i <= 10; i++) After(i, () => { if (Players.Get(slot).Controller is { IsValid: true } c && c.IsAliveP()) c.AddHp(1, 100); });
                After(10f, () => Movement.ClearSpeed(Players.Get(slot).Controller, "sab.adrenaline"));
            }
        }

        // ---- attacker's skills and curses on guards
        if (prisonerHitsGuard && attacker != null)
        {
            if (Has(attacker, "vampire") && ctx.Damage > 0) attacker.AddHp(Math.Max(1, (int)(ctx.Damage * 0.25f)), 100);
            if (Active("frostbite") && (Weapons.IsKnife(ctx.Weapon) || Weapons.IsGrenade(ctx.Weapon)))
            {
                Movement.SetSpeed(victim, "sab.frost", 0.7f);
                var slot = victim.Slot;
                After(2f, () => Movement.ClearSpeed(Players.Get(slot).Controller, "sab.frost"));
            }
        }

        return HookResult.Continue;
    }
}
