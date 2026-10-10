using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Jihad;

/// <summary>Section "Jihad" (ultjb_jihad_* cvars).</summary>
public sealed class JihadConfig
{
    public bool Enabled { get; set; } = true;
    public int GuardsNeeded { get; set; } = 3;              // ultjb_jihad_guards_needed
    public float BombTimer { get; set; } = 4.2f;            // ultjb_jihad_bomb_timer
    public int PercentChanceToGive { get; set; } = 40;      // ultjb_jihad_percent_chance_to_give
    public float ExplodeRadius { get; set; } = 750f;        // ultjb_jihad_explode_radius
    public float MaxDamage { get; set; } = 235f;            // ultjb_jihad_max_damage
    public float DamagePercentToTeammates { get; set; } = 0.2f; // ultjb_jihad_damage_percent_to_teammates

    /// <summary>The breach charge does not exist in CS2; the C4 is the bomb item.</summary>
    public string Item { get; set; } = "weapon_c4";

    /// <summary>Tell the new jihad how to use the bomb (the original showed nothing).</summary>
    public bool Notify { get; set; } = true;

    public string HeadModel { get; set; } = "weapons/models/c4/weapon_c4.vmdl";
    public float HeadModelScale { get; set; } = 1.5f;
    public string Particle { get; set; } = "particles/explosions_fx/explosion_c4_500.vpcf";
    public string SoundActivate { get; set; } = "c4.plant";
    public string SoundExplode { get; set; } = "c4.explode";

    /// <summary>The original "akbar" mp3 needs a custom workshop soundevent in CS2; empty = none.</summary>
    public string SoundAkbar { get; set; } = "";
}

/// <summary>Carry-over semantics: not round scoped (an unused jihad keeps the bomb next round).</summary>
internal sealed class JihadState
{
    public bool IsJihad, Activated, Detonated;
    public uint BombWeapon;
    public uint HeadProp;
    public Timer? BombTimer;
    public float ArmAt;
    public uint LastActiveWeapon;
    public bool Coloured;
}

/// <summary>
/// Port of UltJB_Jihad.sp: a random prisoner gets a bomb; once the cells are open, right click (+attack2) arms it,
/// a bomb appears on their head and it explodes after the fuse, damaging everyone in range (no line of sight,
/// armor ignored, teammates 20 %, freedays immune). Detonates exactly once (fixes the original double explosion).
/// </summary>
public sealed class JihadModule : JbModule, IJihadService
{
    private JihadConfig Cfg => Section<JihadConfig>("Jihad");

    public override string Name => "Jihad";
    public override int Order => 40;
    public override bool Enabled => Cfg.Enabled;

    private bool _allowDropping = true;
    private readonly Dictionary<int, int> _killCredit = new(); // victim slot -> bomber slot
    private readonly HashSet<int> _startedAsJihad = new();

    private static JihadState S(JbPlayer p) => p.Data<JihadState>();

    public override void OnLoad(bool hotReload)
    {
        Jb.Jihad = this;
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(m =>
        {
            if (!string.IsNullOrWhiteSpace(Cfg.HeadModel)) m.AddResource(Cfg.HeadModel);
            if (!string.IsNullOrWhiteSpace(Cfg.Particle)) m.AddResource(Cfg.Particle);
        });
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
        Plugin.AddCommandListener("drop", OnDrop, HookMode.Pre);
        JbEvents.SettingsSpawnPost += OnSpawnPost;
        JbEvents.DayStarted += OnDayStarted;
        JbEvents.DayEnded += OnDayEnded;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.SettingsSpawnPost -= OnSpawnPost;
        JbEvents.DayStarted -= OnDayStarted;
        JbEvents.DayEnded -= OnDayEnded;
        foreach (var p in Players.Connected()) ClearJihad(p);
    }

    public override void OnMapStart(string mapName)
    {
        foreach (var p in Enumerable.Range(0, Players.MaxSlots).Select(Players.Get))
        {
            var s = S(p);
            s.IsJihad = s.Activated = s.Detonated = false;
            s.BombWeapon = s.HeadProp = 0;
            s.BombTimer = null;
        }
    }

    // ================================================================== round flow

    public override void OnRoundStart()
    {
        _allowDropping = true;
        foreach (var p in Players.Connected()) S(p).Detonated = false;
        if (!Jb.Cells.DoExist || Random.Shared.Next(1, 101) > Math.Clamp(Cfg.PercentChanceToGive, 0, 100)) return;
        // Spawn equipment is handed out around round start; give the bomb a second later so it is not stripped.
        Round.Timer(1f, PickRandomJihad);
    }

    private void PickRandomJihad()
    {
        var guards = Players.AliveGuards().Count();
        if (guards < Math.Max(1, Cfg.GuardsNeeded)) return;
        var candidates = Players.AlivePrisoners().Select(c => c.Jb()!).Where(p => p != null && !S(p).IsJihad && !IsFreeday(p)).ToList();
        if (candidates.Count == 0) return;
        SetJihad(candidates[Random.Shared.Next(candidates.Count)]);
    }

    private static bool IsFreeday(JbPlayer p) =>
        p.IsFreeday || (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    private void OnSpawnPost(JbPlayer player)
    {
        var s = S(player);
        if (!s.IsJihad) return;
        var c = player.Controller;
        if (s.Activated || c == null || c.Team != CsTeam.Terrorist)
        {
            ClearJihad(player);
            return;
        }

        RemoveBombWeapon(player);
        GiveBomb(player);
    }

    private void OnDayStarted(JbPlayer? starter, DayType type, bool ffa)
    {
        _startedAsJihad.Clear();
        foreach (var p in Players.Connected())
        {
            if (!S(p).IsJihad) continue;
            _startedAsJihad.Add(p.Slot);
            ClearJihad(p);
        }
    }

    private void OnDayEnded()
    {
        foreach (var slot in _startedAsJihad)
        {
            var p = Players.Get(slot);
            if (p.Controller is { IsValid: true } c && c.IsAliveP()) SetJihad(p);
        }

        _startedAsJihad.Clear();
    }

    // ================================================================== API

    public bool IsJihad(JbPlayer player) => S(player).IsJihad;

    public void SetAllowBombDropping(bool allow) => _allowDropping = allow;

    public void SetJihad(JbPlayer player)
    {
        var s = S(player);
        if (s.IsJihad) return;
        s.IsJihad = true;
        s.Activated = false;
        s.Detonated = false;
        s.LastActiveWeapon = 0;
        GiveBomb(player);
        if (Cfg.Notify) Chat.Tell(player, "jihad.notify");
    }

    public void ClearJihad(JbPlayer player)
    {
        var s = S(player);
        s.IsJihad = false;
        KillTimer(s);
        RemoveBombWeapon(player);
        RemoveHeadProp(s);
        if (s.Coloured)
        {
            s.Coloured = false;
            player.Controller?.ResetColor();
        }
    }

    private void GiveBomb(JbPlayer player)
    {
        var w = Jb.WeaponSelection.GiveWeapon(player, Cfg.Item);
        if (w == null)
        {
            player.Controller?.Give(Cfg.Item);
            w = player.Controller?.Weapons().FirstOrDefault(x => x.DesignerName == Cfg.Item);
        }

        if (w == null || !w.IsValid)
        {
            Logger.LogWarning("[Jihad] could not give {Item} to {Player}", Cfg.Item, player.Name);
            return;
        }

        w.Clip1 = 1;
        S(player).BombWeapon = w.EntityHandle.Raw;
    }

    private static void RemoveBombWeapon(JbPlayer player)
    {
        var s = S(player);
        if (s.BombWeapon == 0) return;
        var w = new CHandle<CBasePlayerWeapon>(s.BombWeapon).Value;
        s.BombWeapon = 0;
        if (w == null || !w.IsValid) return;
        try
        {
            Jb.Settings.StripWeaponFromOwner(w);
        }
        catch
        {
            // already gone
        }
    }

    private static void RemoveHeadProp(JihadState s)
    {
        if (s.HeadProp == 0) return;
        var prop = new CHandle<CBaseEntity>(s.HeadProp).Value;
        s.HeadProp = 0;
        Fx.SafeRemove(prop);
    }

    private static void KillTimer(JihadState s)
    {
        try { s.BombTimer?.Kill(); }
        catch { /* already stopped */ }
        s.BombTimer = null;
    }

    // ================================================================== bomb handling (every tick, jihads only)

    private void OnTick()
    {
        var now = (float)Server.EngineTime;
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.IsJihad || s.Activated || s.BombWeapon == 0) continue;
            var c = p.Controller;
            var pawn = c?.PawnOrNull();
            if (c == null || pawn == null || !c.IsAliveP()) continue;
            var active = pawn.WeaponServices?.ActiveWeapon.Raw ?? 0u;
            var deployed = active == s.BombWeapon;
            if (deployed && active != s.LastActiveWeapon) s.ArmAt = now + 0.5f; // arming delay after switching to it
            s.LastActiveWeapon = active;
            if (!deployed) continue;

            var w = new CHandle<CBasePlayerWeapon>(s.BombWeapon).Value;
            if (w == null || !w.IsValid)
            {
                ClearJihad(p);
                continue;
            }

            // The C4 must never be planted/thrown: keep its primary attack blocked.
            if (w.NextPrimaryAttackTick != int.MaxValue)
            {
                w.NextPrimaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }

            if ((c.Buttons & PlayerButtons.Attack2) != 0) TryActivate(p, s, now);
        }
    }

    private void TryActivate(JbPlayer p, JihadState s, float now)
    {
        if (now < s.ArmAt) return;
        if (!Jb.Cells.HaveOpened)
        {
            s.ArmAt = now + 1.5f;
            p.Controller?.PrintToChat(" " + Colors.Strip(Lang.T("jihad.cells_closed")));
            return;
        }

        if (s.Activated) return;
        s.Activated = true;
        Sound.PlayAt(p.Controller!, Cfg.SoundActivate);
        var slot = p.Slot;
        s.BombTimer = Round.Timer(0.3f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            st.BombTimer = null;
            if (!st.IsJihad || pl.Controller is not { IsValid: true } ctl || !ctl.IsAliveP()) return;
            RemoveBombWeapon(pl);
            CreateBomb(pl, st);
        });
    }

    private void CreateBomb(JbPlayer p, JihadState s)
    {
        var c = p.Controller!;
        var pawn = c.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(Cfg.HeadModel))
            {
                var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
                if (prop != null)
                {
                    prop.Collision.SolidType = SolidType_t.SOLID_NONE;
                    if (prop.CBodyComponent?.SceneNode != null) prop.CBodyComponent.SceneNode.Scale = Cfg.HeadModelScale;
                    prop.DispatchSpawn();
                    prop.SetModel(Cfg.HeadModel);
                    var o = pawn.AbsOrigin;
                    var yaw = pawn.AbsRotation?.Y ?? 0f;
                    prop.Teleport(new Vector(o.X, o.Y, o.Z + 90f), new QAngle(270f, yaw + 180f, 0f), new Vector(0, 0, 0));
                    prop.AcceptInput("SetParent", pawn, prop, "!activator");
                    s.HeadProp = prop.EntityHandle.Raw;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Jihad] head bomb prop failed");
        }

        c.SetColor(Color.FromArgb(255, 255, 0, 199));
        s.Coloured = true;
        if (!string.IsNullOrWhiteSpace(Cfg.SoundAkbar)) Sound.PlayAt(c, Cfg.SoundAkbar, 0.27f);
        var slot = p.Slot;
        s.BombTimer = Round.Timer(Math.Max(0f, Cfg.BombTimer), () =>
        {
            var pl = Players.Get(slot);
            S(pl).BombTimer = null;
            Detonate(pl, null);
        });
    }

    // ================================================================== detonation

    private HookResult OnDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        var victim = Players.Get(ev.Userid);
        if (victim == null) return HookResult.Continue;
        if (_killCredit.TryGetValue(victim.Slot, out var bomberSlot))
        {
            var bomber = Players.Get(bomberSlot).Controller;
            if (bomber != null && bomber.IsValid)
            {
                ev.Attacker = bomber;
                ev.Weapon = "c4";
            }

            return HookResult.Continue;
        }

        var s = S(victim);
        if (!s.IsJihad) return HookResult.Continue;
        var activated = s.Activated && !s.Detonated;
        var origin = victim.Controller?.PawnOrNull()?.AbsOrigin is { } o ? new Vector(o.X, o.Y, o.Z) : null;
        ClearJihad(victim);
        if (activated)
        {
            var slot = victim.Slot;
            Server.NextFrame(() => Detonate(Players.Get(slot), origin));
        }

        return HookResult.Continue;
    }

    private void Detonate(JbPlayer bomber, Vector? deathOrigin)
    {
        var s = S(bomber);
        if (s.Detonated) return;
        s.Detonated = true;
        s.IsJihad = false;
        RemoveHeadProp(s);
        if (s.Coloured)
        {
            s.Coloured = false;
            bomber.Controller?.ResetColor();
        }

        var bc = bomber.Controller;
        var pos = deathOrigin ?? (bc?.PawnOrNull()?.AbsOrigin is { } o ? new Vector(o.X, o.Y, o.Z) : null);
        if (pos == null || bc == null) return;
        var origin = new Vector(pos.X, pos.Y, pos.Z + 32f);

        var particle = string.IsNullOrWhiteSpace(Cfg.Particle) ? null : Fx.Particle(Cfg.Particle, origin, 5f);
        try
        {
            if (!string.IsNullOrWhiteSpace(Cfg.SoundExplode))
            {
                if (particle != null) particle.EmitSound(Cfg.SoundExplode);
                else bc.PawnOrNull()?.EmitSound(Cfg.SoundExplode);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Jihad] explosion sound failed");
        }

        var ffa = Jb.Days.IsInProgress && Jb.Days.IsFreeForAll;
        var bomberTeam = bc.Team;
        int aliveT = 0, aliveCt = 0;
        var radius = Math.Max(1f, Cfg.ExplodeRadius);
        foreach (var c in Players.Alive().ToList())
        {
            if (c.Slot == bomber.Slot) continue;
            var p = c.Jb();
            if (p == null || IsFreeday(p)) continue;
            var alive = true;
            var pawn = c.PawnOrNull();
            if (pawn?.AbsOrigin != null)
            {
                var d = VectorUtil.Distance(origin, pawn.AbsOrigin);
                if (d <= radius)
                {
                    var dmg = Cfg.MaxDamage * (1f - d / radius);
                    if (!ffa && c.Team == bomberTeam) dmg *= Math.Clamp(Cfg.DamagePercentToTeammates, 0f, 1f);
                    var idmg = (int)MathF.Round(dmg);
                    if (idmg > 0)
                    {
                        if (pawn.Health <= idmg)
                        {
                            _killCredit[c.Slot] = bomber.Slot;
                            c.CommitSuicide(true, true);
                            alive = false;
                        }
                        else
                        {
                            pawn.Health -= idmg;
                            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                            Jb.Log.LogEvent(Lang.T("jihad.log_damage", bomber.Name, p.Name, idmg), bomber, p, LogType.Attack);
                        }
                    }
                }
            }

            if (!alive) continue;
            if (c.Team == CsTeam.Terrorist) aliveT++;
            else if (c.Team == CsTeam.CounterTerrorist) aliveCt++;
        }

        if (bc.IsAliveP())
        {
            _killCredit[bomber.Slot] = bomber.Slot;
            bc.CommitSuicide(true, true);
        }

        Server.NextFrame(() => _killCredit.Clear());
        Jb.Log.LogEvent(Lang.T("jihad.log_exploded", bomber.Name), bomber, null, LogType.Any);

        if (!ffa && (aliveT == 0 || aliveCt == 0) && Round.Phase == RoundPhase.Active)
        {
            var reason = aliveCt > 0 ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin;
            Jb.Settings.SetNextRoundEndReason(true, reason);
            Round.End(reason, 4f);
        }
    }

    // ================================================================== dropping

    private HookResult OnDrop(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        var p = player.Jb();
        if (p == null) return HookResult.Continue;
        var s = S(p);
        if (!s.IsJihad || s.BombWeapon == 0) return HookResult.Continue;
        var active = player.PawnOrNull()?.WeaponServices?.ActiveWeapon.Raw ?? 0u;
        if (active != s.BombWeapon) return HookResult.Continue;
        if (_allowDropping && !s.Activated) ShowDropMenu(player);
        return HookResult.Handled;
    }

    private void ShowDropMenu(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("jihad.drop.title")) { ExitButton = false, Subtitle = Lang.T("jihad.drop.subtitle") };
        m.Add(Lang.T("jihad.drop.no"), _ => { });
        m.AddInfo(" ");
        m.AddInfo(" ");
        m.Add(Lang.T("jihad.drop.yes"), x =>
        {
            var p = x.Jb();
            if (p == null || !_allowDropping || S(p).Activated) return;
            ClearJihad(p);
        });
        m.Open(c);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        ClearJihad(player);
        _startedAsJihad.Remove(player.Slot);
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        // Activated bombs do not survive the round; an unused bomb carries over (re-given on spawn).
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.Activated) continue;
            KillTimer(s);
            RemoveHeadProp(s);
            s.IsJihad = false;
            if (s.Coloured)
            {
                s.Coloured = false;
                p.Controller?.ResetColor();
            }
        }
    }
}
