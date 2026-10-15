using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Games;

/// <summary>Duck Hunt rules that need no engine (unit-tested).</summary>
internal static class DuckHuntRules
{
    /// <summary>The duck gets tougher with every hunter so a big lobby cannot one-tap it straight away.</summary>
    public static int DuckHealth(int baseHealth, int perHunter, int hunters) => Math.Clamp(baseHealth + Math.Max(0, perHunter) * Math.Max(0, hunters), 1, 5000);

    /// <summary>The hunters' guns for the HunterWeapons setting ("both", "scout" or "deagle"; anything else = both).</summary>
    public static string[] HunterWeapons(string setting) => (setting ?? "").Trim().ToLowerInvariant() switch
    {
        "scout" or "ssg08" => new[] { "weapon_ssg08" },
        "deagle" => new[] { "weapon_deagle" },
        _ => new[] { "weapon_ssg08", "weapon_deagle" }
    };

    /// <summary>Only a hunter's shot on the duck counts; every other prisoner-on-prisoner or guard-on-guard hit is blocked.</summary>
    public static bool AllowHit(bool attackerIsHunter, bool victimIsDuck, bool sameTeam, bool released) =>
        !sameTeam || released && attackerIsHunter && victimIsDuck;
}

/// <summary>
/// Duck Hunt: one prisoner is the duck (fast, low gravity, can fly by holding jump and is launched back up when it lands),
/// every other player is a hunter with a no-scope scout and/or a deagle. The hunter who shoots the duck down wins; the
/// duck wins if it survives DuckSurviveSeconds or every hunter is out. Prisoners may only hurt the duck, never each other.
/// </summary>
internal sealed class DuckHuntGame : WardenGame
{
    private const string MoveKey = "games.duckhunt";
    private const string FfCvar = "mp_teammates_are_enemies";

    private int _duck = -1;
    private bool _released;
    private float _releaseAt;
    private float _releasedAt;
    private float _groundSince;
    private int _lastHitter = -1;
    private bool _duckDown;
    private bool _setFf;
    private GlowHandle? _duckGlow;

    public override string Key => "duck_hunt";
    public override string NameKey => "games.duckhunt.name";
    public override string RulesKey => "games.duckhunt.rules";
    // The game hands guns to prisoners: only the warden starts it.
    public override bool PrisonerPickable => false;

    private bool IsDuck(int slot) => slot == _duck;

    public override void Start(GameSession s)
    {
        _released = false;
        _duckDown = false;
        _lastHitter = -1;
        _groundSince = 0;
        _releaseAt = Server.CurrentTime + Math.Max(0f, s.Cfg.DuckReleaseSeconds);
        var players = s.Players.ToList();
        PickDuck(s, players[Random.Shared.Next(players.Count)]);
        // Prisoners are teammates: they can only shoot each other with this on (the damage rule limits it to the duck).
        var ff = Cvars.Get(FfCvar);
        _setFf = ff is not ("1" or "true");
        if (_setFf) Cvars.SetTemp(FfCvar, "1");
        foreach (var c in s.Alive()) ArmHunter(s, c);
        Chat.TellAll("games.duckhunt.countdown", Math.Max(0, (int)MathF.Ceiling(s.Cfg.DuckReleaseSeconds)));
    }

    /// <summary>Warden: choose the duck (before release) or release it now.</summary>
    public override void AddWardenItems(GameSession s, JbMenu m)
    {
        if (_released) return;
        m.Add(Lang.T("games.duckhunt.release_now"), p => Release(s, p));
        foreach (var slot in s.Players.Where(x => x != _duck).ToList())
        {
            if (GameSession.C(slot) is not { } c) continue;
            var target = slot;
            m.Add(Lang.T("games.duckhunt.make_duck", Hud.Escape(c.PlayerName)), p =>
            {
                if (_released || s.Over || !s.Players.Contains(target)) return;
                PickDuck(s, target);
                s.Module.RefreshControl(p);
            });
        }
    }

    private void PickDuck(GameSession s, int slot)
    {
        var old = _duck;
        _duck = slot;
        if (old >= 0 && old != slot && GameSession.C(old) is { } prev && prev.IsAliveP()) ArmHunter(s, prev);
        if (GameSession.C(slot) is not { } duck) return;
        duck.Strip(true);
        Chat.TellAll("games.duckhunt.duck_is", Chat.Name(duck));
        Hud.Set(duck, "games.order", Hud.Big(Hud.Bold(Hud.Color(Hud.Escape(Lang.T("games.duckhunt.you_are_duck")), "#FFB000"))), 40, 4f);
    }

    private void ArmHunter(GameSession s, CCSPlayerController c)
    {
        if (IsDuck(c.Slot) || !c.IsAliveP()) return;
        c.Strip(true);
        foreach (var w in DuckHuntRules.HunterWeapons(s.Cfg.HunterWeapons)) c.Give(w);
    }

    private void Release(GameSession s, CCSPlayerController? by)
    {
        if (_released || s.Over) return;
        var duck = GameSession.C(_duck);
        if (duck == null || !duck.IsAliveP()) return;
        _released = true;
        _releasedAt = Server.CurrentTime;
        _groundSince = 0;
        var hunters = s.Players.Count - 1;
        duck.SetHp(DuckHuntRules.DuckHealth(s.Cfg.DuckBaseHealth, s.Cfg.DuckHealthPerHunter, hunters));
        Movement.SetSpeed(duck, MoveKey, Math.Max(0.1f, s.Cfg.DuckSpeed));
        Movement.SetGravity(duck, MoveKey, Math.Clamp(s.Cfg.DuckGravity, 0.05f, 1f));
        Movement.SetAutoBhop(duck, MoveKey, true);
        _duckGlow = Glow.Attach(duck, Color.FromArgb(255, 255, 176, 0));
        Launch(duck, s.Cfg.DuckLaunchSpeed);
        Chat.TellAll("games.duckhunt.released", Chat.Name(duck), (int)s.Cfg.DuckSurviveSeconds);
        if (by != null) s.Module.RefreshControl(by);
    }

    private static void Launch(CCSPlayerController c, float up)
    {
        var v = c.PawnOrNull()?.AbsVelocity;
        if (v == null) return;
        c.SetVelocity(new CssVector(v.X, v.Y, Math.Max(v.Z, up)));
    }

    public override void Tick(GameSession s)
    {
        var now = Server.CurrentTime;
        if (_duckDown) { DuckDown(s); return; }
        if (!_released)
        {
            if (now >= _releaseAt) Release(s, null);
            return;
        }

        if (now - _releasedAt >= s.Cfg.DuckSurviveSeconds) { s.Win(new[] { _duck }); return; }
        if (!s.Players.Any(x => x != _duck)) { s.Win(new[] { _duck }); return; } // every hunter is out

        // Hunters: scouts stay unscoped (no-scopes only), magazines never run dry.
        foreach (var c in s.Alive())
        {
            if (IsDuck(c.Slot)) continue;
            var w = c.ActiveWeapon();
            if (w == null) continue;
            if (w.DesignerName == "weapon_ssg08" && w.NextSecondaryAttackTick != int.MaxValue)
            {
                w.NextSecondaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }

            if (w.DesignerName is "weapon_ssg08" or "weapon_deagle" && w.ReserveAmmo[0] < 30) w.SetAmmo(-1, 30);
        }

        // Duck: fly while holding jump, and never rest on the ground for long.
        var duck = GameSession.C(_duck);
        var pawn = duck?.PawnOrNull();
        if (duck == null || pawn == null || !duck.IsAliveP()) return;
        if (duck.OnGround())
        {
            if (_groundSince <= 0) _groundSince = now;
            else if (now - _groundSince >= s.Cfg.DuckGroundSeconds) { Launch(duck, s.Cfg.DuckLaunchSpeed); _groundSince = 0; }
        }
        else
        {
            _groundSince = 0;
            if ((duck.Buttons & PlayerButtons.Jump) != 0 && pawn.MoveType == MoveType_t.MOVETYPE_WALK)
            {
                var v = pawn.AbsVelocity;
                var vz = Jetpack.Climb(v.Z, s.Cfg.DuckFlyThrust, s.Cfg.DuckMaxUp);
                if (vz != v.Z) duck.SetVelocity(new CssVector(v.X, v.Y, vz));
            }
        }
    }

    public override HookResult OnDamage(GameSession s, DamageContext ctx)
    {
        var a = ctx.Attacker;
        if (a == null || ctx.IsSelf) return HookResult.Continue;
        var victim = ctx.Victim;
        var sameTeam = a.Team == victim.Team;
        var hunter = s.Players.Contains(a.Slot) && !IsDuck(a.Slot);
        var onDuck = IsDuck(victim.Slot);
        if (!DuckHuntRules.AllowHit(hunter, onDuck, sameTeam, _released)) return HookResult.Handled;
        if (hunter && onDuck) _lastHitter = a.Slot;
        return HookResult.Continue;
    }

    public override void OnOut(GameSession s, int slot)
    {
        if (slot == _duck) _duckDown = true; // resolved on the next tick (we are inside the elimination)
    }

    private void DuckDown(GameSession s)
    {
        _duckDown = false;
        if (_lastHitter >= 0 && s.Players.Contains(_lastHitter)) s.Win(new[] { _lastHitter });
        else s.Module.EndGame(GameEndReason.NoPlayers);
    }

    public override string Status(GameSession s)
    {
        var duck = GameSession.C(_duck);
        var name = duck != null ? Hud.Escape(duck.PlayerName) : "?";
        if (!_released) return Hud.Escape(Lang.T("games.duckhunt.status_wait", Math.Max(0, (int)MathF.Ceiling(_releaseAt - Server.CurrentTime)))) + "<br>" + Hud.Color(name, "#FFB000");
        var left = Math.Max(0, (int)MathF.Ceiling(s.Cfg.DuckSurviveSeconds - (Server.CurrentTime - _releasedAt)));
        return Hud.Color(name, "#FFB000") + " " + Hud.Escape(Lang.T("games.duckhunt.status_flying", duck?.GetHp() ?? 0, left));
    }

    public override void End(GameSession s)
    {
        if (_duckGlow != null) { Glow.Remove(_duckGlow); _duckGlow = null; }
        if (GameSession.C(_duck) is { } duck)
        {
            Movement.ClearSpeed(duck, MoveKey);
            Movement.ClearGravity(duck, MoveKey);
            Movement.SetAutoBhop(duck, MoveKey, false);
            if (duck.IsAliveP() && duck.GetHp() > 100) duck.SetHp(100);
        }

        // The hunters' guns belong to the game: everyone still alive goes back to a knife.
        foreach (var slot in s.Players.Append(_duck).Distinct())
            if (GameSession.C(slot) is { } c && c.IsAliveP() && c.Team == CsTeam.Terrorist)
                c.Strip(true);
        if (_setFf) Cvars.Restore(FfCvar);
        _setFf = false;
        _duck = -1;
    }
}
