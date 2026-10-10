using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>
/// Port of UltJB_WardenMenu.sp (v1.11, hlstriker): !wm (healing area, 4 coloured rings, client colours) and
/// !cc ring|remove. Rings and the healing area are real beam polygons; healing uses the LR SetHealth contract and
/// the logger contract like the original natives.
/// </summary>
public sealed class WardenMenuModule : JbModule
{
    private sealed class RingState
    {
        public bool Set;
        public Vector Origin = new(0, 0, 0);
        public float Size;
        public float CustomRadius;
        public List<CBeam>? Beams;
    }

    private static readonly string[] RingKeys = { "wm.ring.green", "wm.ring.red", "wm.ring.yellow", "wm.ring.blue" };
    private static readonly string[] RingHex = { "#00ff00", "#ff0000", "#ffff00", "#00ffff" };

    private static readonly Color[] RingColors =
    {
        Color.FromArgb(255, 0, 255, 0),
        Color.FromArgb(255, 255, 0, 0),
        Color.FromArgb(255, 255, 255, 0),
        Color.FromArgb(255, 0, 255, 255)
    };

    private static readonly Color SetPositionColor = Color.FromArgb(255, 0, 255, 255);

    private WardenMenuConfig Cfg => Section<WardenMenuConfig>("WardenMenu");

    public override int Order => 21;
    public override bool Enabled => Cfg.Enabled;

    private readonly RingState[] _rings = { new(), new(), new(), new() };
    private readonly PulseRings _pulses = new();

    private bool _healSet;
    private Vector _healOrigin = new(0, 0, 0);
    private List<CBeam>? _healBeams;
    private float _nextHeal;
    private float _nextRingCheck;
    private bool _resetDoneThisRound;
    private bool _noTraceWarned;

    private float RingSizeMin => Math.Max(1, Cfg.RingSizeMin);
    private float RingSizeMax => Math.Max(RingSizeMin, Cfg.RingSizeMax);
    private float RingSizeDefault => (RingSizeMax - RingSizeMin) / 2f; // original quirk: (5-1)/2 = 2

    private static string RingName(int i) => Lang.T(RingKeys[i]);

    // ================================================================= lifecycle

    public override void OnLoad(bool hotReload)
    {
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecache);
        RemoveAllRings(null);
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.WardenSelected += OnWardenSelected;
        JbEvents.WardenRemoved += OnWardenRemoved;
        JbEvents.LastRequestInitialized += OnLastRequestInitialized;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.WardenSelected -= OnWardenSelected;
        JbEvents.WardenRemoved -= OnWardenRemoved;
        JbEvents.LastRequestInitialized -= OnLastRequestInitialized;
        RemoveHealingArea();
        RemoveAllRings(null);
        _pulses.Clear();
    }

    public override void OnMapStart(string mapName)
    {
        // Entities from the previous map are gone; drop the stale references.
        foreach (var r in _rings) r.Beams = null;
        _healBeams = null;
        _healSet = false;
        RemoveAllRings(null);
        _pulses.Clear();
        _resetDoneThisRound = false;
    }

    public override void OnMapEnd()
    {
        RemoveHealingArea();
        RemoveAllRings(null);
        _pulses.Clear();
    }

    public override void OnRoundStart() => _resetDoneThisRound = false;

    private void OnPrecache(ResourceManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(Cfg.HealingParticle)) manifest.AddResource(Cfg.HealingParticle);
    }

    /// <summary>cs_pre_restart (original). Not confirmed to fire every round in CS2, so round_prestart does the same.</summary>
    [GameEventHandler]
    public HookResult OnCsPreRestart(EventCsPreRestart ev, GameEventInfo info)
    {
        RoundReset();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundPrestart(EventRoundPrestart ev, GameEventInfo info)
    {
        RoundReset();
        return HookResult.Continue;
    }

    /// <summary>Remove the healing area, all rings (silently) and every prisoner colour (once per round transition).</summary>
    private void RoundReset()
    {
        if (_resetDoneThisRound) return;
        _resetDoneThisRound = true;
        RemoveHealingArea();
        RemoveAllRings(null);
        // Faithful quirk: every still-alive prisoner gets the "Your color has been removed." hint.
        SetClientsColor(true, false, false);
    }

    private void OnWardenSelected(JbPlayer warden) => Chat.Print(warden.Controller, WText.T("wm.type_wm"));

    private void OnWardenRemoved(JbPlayer warden) => RemoveHealingArea();

    private void OnLastRequestInitialized(JbPlayer prisoner)
    {
        var c = prisoner.Controller;
        if (c != null) PurgeFade(c);
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        var c = victim.Controller;
        if (c != null) PurgeFade(c);
    }

    // ================================================================= commands

    private static bool IsWarden(CCSPlayerController? c) => c != null && Jb.Warden.IsWarden(c);

    private static void MustBeWarden(CCSPlayerController c) => Chat.Print(c, WText.T("wm.must_be_warden"));

    [ConsoleCommand("css_wm", "Opens the warden's menu.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void OnWardenMenuCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;
        ShowWardenMenu(player);
    }

    [ConsoleCommand("css_cc", "Sets the clients render color.")]
    [CommandHelper(0, "<ring|remove>", CommandUsage.CLIENT_ONLY)]
    public void OnClientColorCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;
        if (!IsWarden(player))
        {
            MustBeWarden(player);
            return;
        }

        var arg = info.ArgCount > 1 ? info.GetArg(1) : "";
        if (arg.Equals("ring", StringComparison.OrdinalIgnoreCase))
        {
            SetClientsColor(false, true, true);
            Chat.Print(player, WText.T("wm.cc.ring"));
        }
        else if (arg.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            SetClientsColor(true, false, false);
            Chat.Print(player, WText.T("wm.cc.remove"));
        }
        else
        {
            Chat.Reply(player, info, WText.T("wm.cc.usage"));
        }
    }

    // ================================================================= menus
    // "Reopen at the same page" = rebuild and reopen the menu (labels / enabled states change). SourceMod's
    // ExitBack maps to JbMenu.Parent (key 7 / R), built fresh whenever the child menu is built.

    private static void Show(CCSPlayerController player, Func<JbMenu> build)
    {
        if (!IsWarden(player))
        {
            MustBeWarden(player);
            return;
        }

        build().Open(player);
    }

    /// <summary>Wrap a menu action: re-check warden status, run it, then optionally reopen a menu.</summary>
    private static Action<CCSPlayerController> Guarded(Action<CCSPlayerController> action, Func<JbMenu>? reopen = null) => c =>
    {
        if (!IsWarden(c))
        {
            MustBeWarden(c);
            return;
        }

        action(c);
        if (reopen != null) Show(c, reopen);
    };

    private void ShowWardenMenu(CCSPlayerController player) => Show(player, BuildWardenMenu);

    /// <summary>"Warden Menu" (exit only, no back).</summary>
    private JbMenu BuildWardenMenu()
    {
        var m = new JbMenu(Lang.T("wm.title.main"));
        m.Add(Lang.T(_healSet ? "wm.item.remove_heal" : "wm.item.create_heal"), Guarded(c =>
        {
            if (_healSet)
            {
                RemoveHealingArea();
                Chat.Print(c, WText.T("wm.heal.removed"));
            }
            else
            {
                SetPosition(c, -1);
            }
        }, BuildWardenMenu));
        m.Add(Lang.T("wm.item.rings"), Guarded(c => Show(c, BuildRingMenu)));
        m.Add(Lang.T("wm.item.colors"), Guarded(c => Show(c, BuildClientColorMenu)));

        // ---- additions (not in UltJB_WardenMenu.sp; they only call other modules' contracts) ----
        if (Cfg.ShowDaysItem)
            m.Add(Lang.T("wm.item.days"), Guarded(c => Jb.Days.OpenMenu(c)));
        if (Cfg.ShowOpenCellsItem)
            m.Add(Lang.T("wm.item.cells"), Guarded(c =>
            {
                if (!Jb.Cells.DoExist) Chat.Print(c, WText.T("wm.cells.none"));
                else if (Jb.Cells.HaveOpened) Chat.Print(c, WText.T("wm.cells.already"));
                else if (!Jb.Cells.ForceOpen(c)) Chat.Print(c, WText.T("wm.cells.failed"));
            }, BuildWardenMenu), Jb.Cells.DoExist && !Jb.Cells.HaveOpened);

        return m;
    }

    /// <summary>"Rings Menu" (back = Warden Menu).</summary>
    private JbMenu BuildRingMenu()
    {
        var m = new JbMenu(Lang.T("wm.title.rings")) { Parent = BuildWardenMenu() };
        m.Add(Lang.T("wm.item.remove_all_rings"), Guarded(RemoveAllRings, BuildRingMenu));
        for (var i = 0; i < _rings.Length; i++)
        {
            var idx = i;
            m.Add(WText.T("wm.item.manage_ring", RingName(idx)), Guarded(c => Show(c, () => BuildManageRing(idx))));
        }

        return m;
    }

    /// <summary>"Manage Ring: {Name}" (back = Rings Menu). The original spacer is omitted (JbMenu has none).</summary>
    private JbMenu BuildManageRing(int idx)
    {
        var r = _rings[idx];
        JbMenu Self() => BuildManageRing(idx);
        var m = new JbMenu(WText.T("wm.title.manage", RingName(idx))) { Parent = BuildRingMenu() };
        m.Add(Lang.T("wm.item.set_pos"), Guarded(c => SetPosition(c, idx), Self));
        m.Add(Lang.T("wm.item.inc"), Guarded(_ => IncreaseSize(idx), Self), r.Set && r.Size < RingSizeMax);
        m.Add(Lang.T("wm.item.dec"), Guarded(_ => DecreaseSize(idx), Self), r.Set && r.Size > RingSizeMin);
        m.Add(Lang.T("wm.item.remove_ring"), Guarded(c => RemoveRing(c, idx), Self), r.Set);
        return m;
    }

    /// <summary>"Client Color Menu" (back = Warden Menu).</summary>
    private JbMenu BuildClientColorMenu()
    {
        var m = new JbMenu(Lang.T("wm.title.colors")) { Parent = BuildWardenMenu() };
        m.Add(Lang.T("wm.item.set_ring_color"), Guarded(_ => SetClientsColor(false, true, true), BuildClientColorMenu));
        m.Add(Lang.T("wm.item.remove_ring_color"), Guarded(_ => SetClientsColor(true, true, false), BuildClientColorMenu));
        m.Add(Lang.T("wm.item.remove_all_colors"), Guarded(_ => SetClientsColor(true, false, false), BuildClientColorMenu));
        return m;
    }

    // ================================================================= positioning

    /// <summary>
    /// SetPosition(type, ring): trace from the eyes (ignoring players), show the set-position effect and require a
    /// flat surface (normal.z >= 0.85). ringIndex -1 = healing area. net8.0 build (no ray trace): the warden's own
    /// feet are used and "on the ground" stands in for the flat-surface test.
    /// </summary>
    private void SetPosition(CCSPlayerController c, int ringIndex)
    {
        Vector pos;
        bool flat;
        if (WTrace.EyeTrace(c, out var end, out var normal))
        {
            pos = end;
            flat = normal.Z >= 0.85f;
        }
        else if (!WTrace.Available)
        {
            var o = c.Origin();
            if (o == null) return;
            pos = VectorUtil.Copy(o);
            flat = c.OnGround();
            if (!_noTraceWarned)
            {
                _noTraceWarned = true;
                Chat.Print(c, WText.T("wm.no_trace"));
            }
        }
        else
        {
            Chat.Print(c, WText.T("wm.flat")); // aiming at nothing (sky)
            return;
        }

        // Set-position effect: TE ring diameter 35 -> 2 over 0.25 s (radius 17.5 -> 1), cyan, width 2.
        _pulses.Spawn(new Vector(pos.X, pos.Y, pos.Z + 1f), 17.5f, 1f, 0.25f, SetPositionColor, 2f, 16);

        if (!flat)
        {
            Chat.Print(c, WText.T("wm.flat"));
            return;
        }

        if (ringIndex < 0)
        {
            InitHealingArea(pos);
            Chat.Print(c, WText.T("wm.heal.created"));
        }
        else
        {
            var p = Players.Get(c);
            if (p != null) SetRingPosition(p, ringIndex, pos);
        }
    }

    /// <summary>SetRingPosition (also UltJB_WardenMenu_CreateRing via IWardenService.CreateRing, always custom radius).</summary>
    public void SetRingPosition(JbPlayer client, int ringIndex, Vector origin, bool useCustomRadius = false, float customRadius = 0f)
    {
        if (ringIndex < 0 || ringIndex >= _rings.Length) return;
        var r = _rings[ringIndex];
        var baseRadius = Math.Max(1f, Cfg.RingRadius);
        if (useCustomRadius)
        {
            r.CustomRadius = customRadius;
            if (r.CustomRadius > baseRadius * RingSizeMax)
            {
                r.Size = RingSizeMax;
                r.CustomRadius = 0f;
            }
            else if (r.CustomRadius < baseRadius * RingSizeMin)
            {
                r.Size = RingSizeMin;
                r.CustomRadius = 0f;
            }
        }

        r.Origin = VectorUtil.Copy(origin);
        r.Set = true;
        DrawRing(ringIndex);
        Chat.Print(client.Controller, WText.T("wm.ring.set", RingName(ringIndex)));
    }

    private float RingRadiusOf(RingState r) => r.CustomRadius != 0f ? r.CustomRadius : Math.Max(1f, Cfg.RingRadius) * r.Size;

    private void IncreaseSize(int idx)
    {
        var r = _rings[idx];
        if (r.Size >= RingSizeMax) return;
        r.Size++;
        r.CustomRadius = 0f;
        DrawRing(idx);
    }

    private void DecreaseSize(int idx)
    {
        var r = _rings[idx];
        if (r.Size <= RingSizeMin) return;
        r.Size--;
        r.CustomRadius = 0f;
        DrawRing(idx);
    }

    private void RemoveRing(CCSPlayerController? client, int idx)
    {
        var r = _rings[idx];
        if (r.Set && client != null && client.IsValid) Chat.Print(client, WText.T("wm.ring.removed", RingName(idx)));
        r.Set = false;
        r.Size = RingSizeDefault;
        r.CustomRadius = 0f;
        WDraw.Remove(r.Beams);
        r.Beams = null;
    }

    private void RemoveAllRings(CCSPlayerController? client)
    {
        for (var i = 0; i < _rings.Length; i++) RemoveRing(client, i);
    }

    /// <summary>Persistent ring polygon at origin + 10z (moved/resized instead of being re-sent every 0.3 s).</summary>
    private void DrawRing(int idx)
    {
        var r = _rings[idx];
        if (!r.Set) return;
        var segments = Math.Clamp(Cfg.RingSegments, 3, 64);
        var center = new Vector(r.Origin.X, r.Origin.Y, r.Origin.Z + 10f);
        var radius = RingRadiusOf(r);
        if (WDraw.RingValid(r.Beams, segments))
        {
            WDraw.MoveRing(r.Beams!, center, radius);
            return;
        }

        WDraw.Remove(r.Beams);
        r.Beams = WDraw.Ring(center, radius, RingColors[idx], Math.Max(0.5f, Cfg.RingWidth), segments);
    }

    // ================================================================= healing area

    private void InitHealingArea(Vector pos)
    {
        _healOrigin = new Vector(pos.X, pos.Y, pos.Z + 5f);
        _healSet = true;
        _nextHeal = 0f;
        WDraw.Remove(_healBeams);
        _healBeams = null;
        UpdateHealingArea();
    }

    private void RemoveHealingArea()
    {
        _healSet = false;
        WDraw.Remove(_healBeams);
        _healBeams = null;
    }

    private void OnTick()
    {
        _pulses.Tick();
        var now = Server.CurrentTime;

        if (now >= _nextRingCheck)
        {
            _nextRingCheck = now + 0.3f; // RING_UPDATE_TIME: safety redraw if round cleanup removed a beam
            var segments = Math.Clamp(Cfg.RingSegments, 3, 64);
            for (var i = 0; i < _rings.Length; i++)
                if (_rings[i].Set && !WDraw.RingValid(_rings[i].Beams, segments))
                    DrawRing(i);
        }

        if (_healSet && now >= _nextHeal) UpdateHealingArea();
    }

    /// <summary>Every 0.5 s: re-tint the area marker (255,r,r) and heal alive prisoners inside the radius.</summary>
    private void UpdateHealingArea()
    {
        _nextHeal = Server.CurrentTime + Math.Max(0.1f, Cfg.HealingInterval);
        var r = Random.Shared.Next(65, 161);
        var tint = Color.FromArgb(255, 255, r, r);
        var radius = Math.Max(1f, Cfg.HealingRadius);
        var segments = Math.Clamp(Cfg.HealingRingSegments, 3, 64);

        if (WDraw.RingValid(_healBeams, segments)) WDraw.ColorRing(_healBeams!, tint);
        else
        {
            WDraw.Remove(_healBeams);
            _healBeams = WDraw.Ring(_healOrigin, radius, tint, Math.Max(0.5f, Cfg.HealingRingWidth), segments);
        }

        if (!string.IsNullOrWhiteSpace(Cfg.HealingParticle)) SpawnHealingParticle(tint);

        var max = Math.Max(1, Cfg.HealingMaxHealth);
        CCSPlayerController? firstHealed = null;
        foreach (var c in Players.AlivePrisoners())
        {
            var o = c.Origin();
            if (o == null || VectorUtil.Distance(o, _healOrigin) > radius) continue;
            var hp = c.GetHp();
            if (hp >= max) continue;
            var newHp = Math.Min(max, hp + Math.Max(1, Cfg.HealingAmount));
            var p = Players.Get(c);
            if (p == null) continue;
            Jb.LastRequests.SetHealth(p, newHp);
            Jb.Log.LogEvent(Colors.Strip(WText.T("wm.heal.log", c.PlayerName, newHp - hp)), p, null, LogType.Attack);
            firstHealed ??= c;
        }

        if (firstHealed != null && !string.IsNullOrWhiteSpace(Cfg.HealSound)) PlayHealSound(firstHealed);
    }

    private void SpawnHealingParticle(Color tint)
    {
        try
        {
            var p = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (p == null) return;
            p.EffectName = Cfg.HealingParticle;
            p.StartActive = true;
            p.Tint = tint;
            p.Teleport(_healOrigin, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            p.DispatchSpawn();
            p.AcceptInput("Start");
            Fx.RemoveLater(p, Math.Max(0.5f, Cfg.HealingInterval * 2f));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Healing particle failed");
        }
    }

    private void PlayHealSound(CCSPlayerController healed)
    {
        var sound = Cfg.HealSound;
        if (sound.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) || sound.Contains('/'))
        {
            foreach (var c in Players.Humans())
            {
                var o = c.Origin();
                if (o != null && VectorUtil.Distance(o, _healOrigin) <= Cfg.HealSoundRadius) Sound.Play(c, sound);
            }

            return;
        }

        Sound.PlayAt(healed, sound, 0.6f);
    }

    // ================================================================= client colours

    /// <summary>SetClientsColor(remove, inRingsOnly, useRingColor) over every alive prisoner.</summary>
    private void SetClientsColor(bool remove, bool inRingsOnly, bool useRingColor)
    {
        foreach (var c in Players.AlivePrisoners())
        {
            var ringIndex = -1;
            var color = Color.FromArgb(255, 255, 255, 255);
            if (inRingsOnly)
            {
                ringIndex = GetClientsRingIndex(c);
                if (ringIndex == -1) continue;
                if (useRingColor) color = RingColors[ringIndex];
            }

            if (remove)
            {
                c.SetColor(Color.FromArgb(255, 255, 255, 255));
                PurgeFade(c);
                Hud.Set(c, "wm.color", Lang.T("wm.color.removed.hint"), 50, 5);
            }
            else
            {
                if (ringIndex < 0) continue; // only reachable through the ring path
                c.SetColor(color);
                // Permanent subtle screen tint in the ring colour (FFADE_STAYOUT | FFADE_PURGE, alpha 20).
                Fx.Fade(c, Color.FromArgb(20, color.R, color.G, color.B), 0f, 0f, Fx.FadeStayOut | Fx.FadePurge);
                Hud.Set(c, "wm.color", WText.T("wm.color.set.hint", RingHex[ringIndex], RingName(ringIndex)), 50, 5);
            }
        }
    }

    /// <summary>First set ring (index order) whose radius contains the player (3D, ring origin at ground level).</summary>
    private int GetClientsRingIndex(CCSPlayerController c)
    {
        var o = c.Origin();
        if (o == null) return -1;
        for (var i = 0; i < _rings.Length; i++)
        {
            var r = _rings[i];
            if (!r.Set) continue;
            if (VectorUtil.Distance(o, r.Origin) <= RingRadiusOf(r)) return i;
        }

        return -1;
    }

    private static void PurgeFade(CCSPlayerController c) =>
        Fx.Fade(c, Color.FromArgb(255, 1, 1, 1), 0f, 0f, Fx.FadePurge);
}
