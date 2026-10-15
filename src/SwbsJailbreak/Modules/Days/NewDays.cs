using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "DaysNew": the days added in 0.14 (each can be turned off; per-map toggles still work in !daysedit).</summary>
public sealed class NewDaysConfig
{
    // ---------------------------------------------------------------- Juggernaut (warday)
    public bool JuggernautEnabled { get; set; } = true;
    public string JuggernautWeapon { get; set; } = "weapon_negev";
    public int JuggernautBaseHealth { get; set; } = 500;
    /// <summary>Extra Juggernaut health for every alive prisoner when the day starts.</summary>
    public int JuggernautHealthPerPrisoner { get; set; } = 125;
    public int JuggernautArmor { get; set; } = 200;
    public float JuggernautSpeed { get; set; } = 0.9f;
    /// <summary>What the hunting prisoners get (plus a knife).</summary>
    public string JuggernautPrisonerWeapon { get; set; } = "weapon_deagle";

    // ---------------------------------------------------------------- Dodgeball (warday)
    public bool DodgeballEnabled { get; set; } = true;
    /// <summary>Seconds after a throw until the next decoy is handed out.</summary>
    public float DodgeballRefillSeconds { get; set; } = 1f;

    // ---------------------------------------------------------------- HE Day (warday)
    public bool HeDayEnabled { get; set; } = true;
    public float HeDayRefillSeconds { get; set; } = 1.5f;
    public float HeDayGravity { get; set; } = 0.5f;

    // ---------------------------------------------------------------- Headshot Only (warday)
    public bool HeadshotOnlyEnabled { get; set; } = true;
    public string HeadshotOnlyWeapon { get; set; } = "weapon_deagle";

    // ---------------------------------------------------------------- Teleport Freeday
    public bool TeleportFreedayEnabled { get; set; } = true;
    public int TeleportIntervalSeconds { get; set; } = 30;
    /// <summary>Guards can be swapped with prisoners too (never guard with guard).</summary>
    public bool TeleportIncludeGuards { get; set; } = true;

    /// <summary>Played from both players of a swap.</summary>
    public string TeleportSound { get; set; } = "";

    // ---------------------------------------------------------------- Gun Game (warday)
    public bool GunGameEnabled { get; set; } = true;
    /// <summary>The levels in order; the knife is always added as the last level.</summary>
    public List<string> GunGameWeapons { get; set; } = new()
    {
        "weapon_glock", "weapon_p250", "weapon_deagle", "weapon_mp9", "weapon_p90", "weapon_ump45", "weapon_nova",
        "weapon_xm1014", "weapon_famas", "weapon_ak47", "weapon_m4a1_silencer", "weapon_ssg08", "weapon_awp"
    };
    public float GunGameRespawnSeconds { get; set; } = 2f;
    /// <summary>Levels a knifed player loses.</summary>
    public int GunGameKnifeDemote { get; set; } = 1;
    /// <summary>When nobody has won after this long, the highest level wins.</summary>
    public int GunGameMinutes { get; set; } = 6;
    public int GunGameWinPoints { get; set; } = 50;

    // ---------------------------------------------------------------- Spy Day (warday)
    public bool SpyDayEnabled { get; set; } = true;
    /// <summary>One spy for every this many prisoners (at least one).</summary>
    public int SpyRatio { get; set; } = 4;
    /// <summary>At most this many spies (0 = no cap).</summary>
    public int SpyMax { get; set; } = 4;
    public string SpyWeapon { get; set; } = "weapon_deagle";
    /// <summary>Share of the damage a guard deals to an innocent prisoner that is turned back on the guard.</summary>
    public float SpyMisfireShare { get; set; } = 0.5f;
    /// <summary>Hide the kill feed during Spy Day so kills do not give the spies away.</summary>
    public bool SpyHideKillFeed { get; set; } = true;

    // ---------------------------------------------------------------- Speedrun (freeday)
    public bool SpeedrunEnabled { get; set; } = true;
    public int SpeedrunCountdown { get; set; } = 5;
    /// <summary>Points for the 1st, 2nd, 3rd... finisher.</summary>
    public List<int> SpeedrunRewards { get; set; } = new() { 30, 20, 10 };
    /// <summary>Extra points for beating the map record.</summary>
    public int SpeedrunRecordPoints { get; set; } = 25;
    public bool SpeedrunNoFallDamage { get; set; } = true;
}

/// <summary>
/// Smaller 0.14 days: Juggernaut, Dodgeball, HE Day and Headshot Only (wardays) and Teleport Freeday.
/// Gun Game, Spy Day and Speedrun have their own files.
/// </summary>
public sealed class NewDays : JbModule
{
    private NewDaysConfig Cfg => Section<NewDaysConfig>("DaysNew");

    public override string Name => "Days.New";
    public override int Order => 12;

    private DayDefinition? _jugg, _dodge, _he, _headshot, _teleport;
    private int _juggSlot = -1;
    private GlowHandle? _juggGlow;
    private Timer? _juggHud;
    private Timer? _teleportTimer;
    private int _teleportCountdown;

    private static bool Active(DayDefinition? d) => d != null && ReferenceEquals(Jb.Days.Current, d);
    private static bool HasFreeday(JbPlayer? p) => p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;
    private static string KnifeFor(CCSPlayerController c) => c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t";

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule days) return;
        const DayFlags stripAll = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying;

        if (Cfg.JuggernautEnabled)
        {
            _jugg = new DayDefinition
            {
                Name = "Juggernaut", Type = DayType.Warday,
                Flags = stripAll | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.GivePrisonersInfiniteAmmo,
                Description = "One guard becomes a huge Juggernaut; every prisoner hunts them. The other guards sit out.",
                CanStart = () => Players.CountAlive(CsTeam.CounterTerrorist) > 0 && Players.CountAlive(CsTeam.Terrorist) > 0,
                OnStart = _ => JuggStart(),
                OnFreezeEnd = JuggRelease,
                OnEnd = _ => JuggEnd()
            };
            days.Register(_jugg);
        }

        if (Cfg.DodgeballEnabled)
        {
            _dodge = new DayDefinition
            {
                Name = "Dodgeball", Type = DayType.Warday, AllowFreeForAll = true, Flags = stripAll,
                Description = "Decoys only; one hit kills. A new decoy after every throw.",
                OnStart = _ => { },
                OnFreezeEnd = () => { foreach (var p in DayUtil.AliveAll()) GiveIfMissing(p, "weapon_decoy"); }
            };
            days.Register(_dodge);
        }

        if (Cfg.HeDayEnabled)
        {
            _he = new DayDefinition
            {
                Name = "HE Day", Type = DayType.Warday, AllowFreeForAll = true, Flags = stripAll,
                Description = "HE grenades only, refilled after every throw, in low gravity.",
                OnStart = _ => { foreach (var p in DayUtil.AliveAll()) Movement.SetGravity(p.Controller, "day.he", Cfg.HeDayGravity); },
                OnFreezeEnd = () => { foreach (var p in DayUtil.AliveAll()) GiveIfMissing(p, "weapon_hegrenade"); },
                OnEnd = _ => Movement.ClearKeyAll("day.he")
            };
            days.Register(_he);
        }

        if (Cfg.HeadshotOnlyEnabled)
        {
            _headshot = new DayDefinition
            {
                Name = "Headshot Only", Type = DayType.Warday, AllowFreeForAll = true,
                Flags = stripAll | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.GivePrisonersInfiniteAmmo,
                Description = "Deagles only, and only headshots deal damage.",
                OnStart = _ => { },
                OnFreezeEnd = () => { foreach (var p in DayUtil.AliveAll()) DayUtil.Give(p, Cfg.HeadshotOnlyWeapon, true); }
            };
            days.Register(_headshot);
        }

        if (Cfg.TeleportFreedayEnabled)
        {
            _teleport = new DayDefinition
            {
                Name = "Teleport", Type = DayType.Freeday, Flags = DayFlags.AllowWeaponPickups | DayFlags.AllowWeaponDrops,
                Description = "Every few seconds each prisoner swaps places with a random player.",
                OnStart = _ => TeleportStart(),
                OnEnd = _ => TeleportStop()
            };
            days.Register(_teleport);
        }

        JbEvents.DaySpawnPost += OnDaySpawn;
        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        Plugin.RegisterEventHandler<EventDecoyStarted>(OnDecoyStarted);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.DaySpawnPost -= OnDaySpawn;
        DamageService.Unregister(OnDamage);
        TeleportStop();
        JuggEnd();
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (Active(_jugg) && player.Slot == _juggSlot)
        {
            // The Juggernaut left: the prisoners win.
            _juggSlot = -1;
            Chat.TellAll("days.jugg.left");
            if (Round.IsActive) Round.End(CsTeam.Terrorist);
        }
    }

    private void OnDaySpawn(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (Active(_he)) Movement.SetGravity(c, "day.he", Cfg.HeDayGravity);
    }

    private static void GiveIfMissing(JbPlayer p, string weapon)
    {
        var c = p.Controller;
        if (c == null || !c.IsAliveP() || HasFreeday(p) || c.HasWeapon(weapon)) return;
        DayUtil.Give(p, weapon, true);
    }

    // ====================================================================== Juggernaut

    private void JuggStart()
    {
        _juggSlot = -1;
        var guards = Players.AliveGuards().ToList();
        if (guards.Count == 0) return;
        // The warden is the natural pick when there is one, otherwise a random guard.
        var pick = guards.FirstOrDefault(g => Jb.Warden.IsWarden(g)) ?? guards[Random.Shared.Next(guards.Count)];
        _juggSlot = pick.Slot;
        Chat.TellAll("days.jugg.picked", pick.PlayerName);
    }

    private void JuggRelease()
    {
        var jugg = Players.Get(_juggSlot).Controller;
        if (jugg == null || !jugg.IsAliveP())
        {
            Chat.TellAll("days.jugg.left");
            if (Round.IsActive) Round.End(CsTeam.Terrorist);
            return;
        }

        var prisoners = Players.CountAlive(CsTeam.Terrorist);
        var jp = jugg.Jb()!;
        DayUtil.Give(jp, "weapon_knife");
        DayUtil.Give(jp, Cfg.JuggernautWeapon, true);
        Jb.LastRequests.SetHealth(jp, Math.Max(1, Cfg.JuggernautBaseHealth + Cfg.JuggernautHealthPerPrisoner * prisoners));
        jugg.SetArmor(Math.Max(0, Cfg.JuggernautArmor), true);
        Movement.SetSpeed(jugg, "day.jugg", Cfg.JuggernautSpeed);
        _juggGlow = Glow.Attach(jugg, Color.FromArgb(255, 255, 60, 30));

        foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist))
        {
            if (HasFreeday(p)) continue;
            DayUtil.Give(p, "weapon_knife_t");
            DayUtil.Give(p, Cfg.JuggernautPrisonerWeapon, true);
        }

        foreach (var g in Players.AliveGuards().Where(g => g.Slot != _juggSlot)) Chat.Tell(g, "days.jugg.sit_out");
        Chat.TellAll("days.jugg.go", jugg.PlayerName, jugg.GetHp());
        _juggHud = Round.Timer(0.5f, () =>
        {
            var j = Players.Get(_juggSlot).Controller;
            if (!Active(_jugg) || j == null || !j.IsAliveP()) return;
            Hud.SetGlobal("days.jugg", Lang.T("days.jugg.hud", Hud.Escape(j.PlayerName), j.GetHp(), Players.CountAlive(CsTeam.Terrorist)), 20, 1f);
        }, true);
    }

    private void JuggEnd()
    {
        try { _juggHud?.Kill(); }
        catch { /* already stopped */ }
        _juggHud = null;
        Glow.Remove(_juggGlow);
        _juggGlow = null;
        _juggSlot = -1;
        Movement.ClearKeyAll("day.jugg");
        Hud.ClearGlobal("days.jugg");
    }

    // ====================================================================== Teleport Freeday

    private void TeleportStart()
    {
        TeleportStop();
        _teleportCountdown = Math.Max(5, Cfg.TeleportIntervalSeconds);
        Chat.TellAll("days.teleport.how", _teleportCountdown);
        _teleportTimer = Round.Timer(1f, TeleportTick, true);
    }

    private void TeleportStop()
    {
        try { _teleportTimer?.Kill(); }
        catch { /* already stopped */ }
        _teleportTimer = null;
        Hud.ClearGlobal("days.teleport");
    }

    private void TeleportTick()
    {
        if (!Active(_teleport))
        {
            TeleportStop();
            return;
        }

        _teleportCountdown--;
        if (_teleportCountdown <= 3 && _teleportCountdown > 0)
            Hud.SetGlobal("days.teleport", Lang.T("days.teleport.hud", _teleportCountdown), 30, 1.2f);
        if (_teleportCountdown > 0) return;
        _teleportCountdown = Math.Max(5, Cfg.TeleportIntervalSeconds);
        SwapEveryone();
    }

    private void SwapEveryone()
    {
        var alive = Players.Alive()
            .Where(c => c.Team == CsTeam.Terrorist || Cfg.TeleportIncludeGuards && c.Team == CsTeam.CounterTerrorist)
            .Where(c => c.Jb() is { } p && !Jb.LastRequests.IsInLastRequest(p))
            .Where(c => c.PawnOrNull() is { AbsOrigin: not null, MoveType: not MoveType_t.MOVETYPE_NOCLIP })
            .ToList();
        var slots = alive.Select(c => c.Slot).OrderBy(_ => Random.Shared.Next()).ToList();
        // Guard with guard never swaps: the pair is used only when a prisoner is in it.
        var pairs = DayLogic.SwapPairs(slots, s => Players.Get(s).Controller?.Team == CsTeam.Terrorist);
        var swapped = 0;
        foreach (var (a, b) in pairs)
        {
            var ca = Players.Get(a).Controller;
            var cb = Players.Get(b).Controller;
            var pa = ca?.PawnOrNull();
            var pb = cb?.PawnOrNull();
            if (pa?.AbsOrigin == null || pb?.AbsOrigin == null) continue;
            var posA = VectorUtil.Copy(pa.AbsOrigin);
            var posB = VectorUtil.Copy(pb.AbsOrigin);
            var angA = new QAngle(pa.EyeAngles.X, pa.EyeAngles.Y, 0);
            var angB = new QAngle(pb.EyeAngles.X, pb.EyeAngles.Y, 0);
            var still = new Vector(0, 0, 0);
            pa.Teleport(posB, angB, still);
            pb.Teleport(posA, angA, still);
            if (Cfg.TeleportSound.Length > 0)
            {
                Sound.PlayAt(ca!, Cfg.TeleportSound);
                Sound.PlayAt(cb!, Cfg.TeleportSound);
            }
            Fx.PulseAt(posA, Color.FromArgb(255, 160, 60, 255), 10f, 120f, 0.4f);
            Fx.PulseAt(posB, Color.FromArgb(255, 160, 60, 255), 10f, 120f, 0.4f);
            swapped++;
        }

        if (swapped > 0) Hud.SetGlobal("days.teleport", Lang.T("days.teleport.swapped"), 30, 1.5f);
    }

    // ====================================================================== damage / grenades

    private HookResult OnDamage(DamageContext ctx)
    {
        if (ctx.Attacker == null) return HookResult.Continue;
        var a = ctx.Attacker.Jb();
        var v = ctx.Victim.Jb();
        if (HasFreeday(a) || HasFreeday(v)) return HookResult.Continue;

        if (Active(_dodge))
        {
            if (ctx.IsSelf || !Jb.Days.IsFreeForAll && ctx.Attacker.Team == ctx.Victim.Team) return HookResult.Continue;
            if (ctx.Weapon == "decoy_projectile" || ctx.Weapon == "weapon_decoy") ctx.Damage = 999f;
            return HookResult.Continue;
        }

        if (Active(_headshot))
        {
            if ((ctx.Info.BitsDamageType & DamageTypes_t.DMG_BULLET) == 0) return HookResult.Continue;
            return ctx.HitGroup == HitGroup_t.HITGROUP_HEAD ? HookResult.Continue : HookResult.Handled;
        }

        if (Active(_jugg))
        {
            // Guards other than the Juggernaut are out of the fight: they neither deal nor take player damage.
            var attackerOut = ctx.Attacker.Team == CsTeam.CounterTerrorist && ctx.Attacker.Slot != _juggSlot;
            var victimOut = ctx.Victim.Team == CsTeam.CounterTerrorist && ctx.Victim.Slot != _juggSlot;
            if (!ctx.IsSelf && (attackerOut || victimOut)) return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid) return HookResult.Continue;
        var weapon = ev.Weapon ?? "";
        if (Active(_dodge) && weapon.Contains("decoy")) Refill(c, "weapon_decoy", Cfg.DodgeballRefillSeconds, _dodge!);
        else if (Active(_he) && weapon.Contains("hegrenade")) Refill(c, "weapon_hegrenade", Cfg.HeDayRefillSeconds, _he!);
        return HookResult.Continue;
    }

    private static void Refill(CCSPlayerController c, string weapon, float delay, DayDefinition day)
    {
        var slot = c.Slot;
        Round.Timer(Math.Max(0.1f, delay), () =>
        {
            if (!Active(day)) return;
            var p = Players.Get(slot);
            if (p.Controller is { IsValid: true } pc && pc.IsAliveP()) GiveIfMissing(p, weapon);
        });
    }

    /// <summary>
    /// Dodgeball hit check on the projectile itself: a CS2 decoy hit does not always arrive as damage, so a decoy in flight
    /// inside another player's hull kills them (credited to the thrower) and disappears.
    /// </summary>
    private void OnTick()
    {
        if (!Active(_dodge)) return;
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("decoy_projectile"))
        {
            if (!e.IsValid || e.AbsOrigin == null) continue;
            var v = e.AbsVelocity;
            if (v.X * v.X + v.Y * v.Y + v.Z * v.Z < 150f * 150f) continue; // rolling or resting: harmless
            var ownerPawn = e.OwnerEntity.Value;
            var thrower = ownerPawn is { IsValid: true } && ownerPawn.DesignerName == "player"
                ? ownerPawn.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>()
                : null;
            var pos = e.AbsOrigin;
            foreach (var c in Players.Alive())
            {
                if (thrower != null && c.Slot == thrower.Slot) continue;
                if (HasFreeday(c.Jb())) continue;
                if (!Jb.Days.IsFreeForAll && thrower != null && c.Team == thrower.Team) continue;
                var o = c.PawnOrNull()?.AbsOrigin;
                if (o == null || Math.Abs(pos.X - o.X) > 20f || Math.Abs(pos.Y - o.Y) > 20f || pos.Z < o.Z - 4f || pos.Z > o.Z + 76f) continue;
                DamageService.KillWithCredit(c, thrower is { IsValid: true } ? thrower : null, "decoy");
                e.AcceptInput("Kill");
                break;
            }
        }
    }

    /// <summary>Dodgeball decoys only matter in flight: remove them when they land instead of letting them fake gunfire.</summary>
    private HookResult OnDecoyStarted(EventDecoyStarted ev, GameEventInfo info)
    {
        if (!Active(_dodge)) return HookResult.Continue;
        var e = Utilities.GetEntityFromIndex<CBaseEntity>(ev.Entityid);
        if (e is { IsValid: true }) e.AcceptInput("Kill");
        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Active(_jugg)) return HookResult.Continue;
        var victim = ev.Userid;
        if (victim == null || victim.Slot != _juggSlot) return HookResult.Continue;
        var killer = ev.Attacker;
        Chat.TellAll("days.jugg.down", killer is { IsValid: true } && killer.Slot != victim.Slot ? killer.PlayerName : "?");
        Server.NextFrame(() =>
        {
            if (Active(_jugg) && Round.IsActive) Round.End(CsTeam.Terrorist);
        });
        return HookResult.Continue;
    }
}
