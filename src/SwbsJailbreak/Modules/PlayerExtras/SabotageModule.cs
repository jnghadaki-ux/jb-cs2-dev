using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>What an effect is: bought against the guards (Sabotage, Curse), for yourself (Perk, Skill) or by the prisoners together (Aura).</summary>
internal enum EffectKind { Sabotage, Curse, Perk, Skill, Aura }

/// <summary>One shop effect (engine-free catalogue, unit-tested).</summary>
internal sealed record ShopEffect(string Id, EffectKind Kind, string Name, string Description, int Cost, bool OnByDefault);

/// <summary>Per-effect settings an owner (or !sabotagesetup) changes.</summary>
public sealed class ShopEffectSettings
{
    public bool Enabled { get; set; }
    public int Cost { get; set; }
}

/// <summary>Section "Sabotage": the prisoners' effect shop (!sabotage, !perk).</summary>
public sealed class SabotageConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Effects can be bought this many seconds into the round (after freeze time).</summary>
    public int BuyWindowSeconds { get; set; } = 120;
    /// <summary>Sabotages / curses one prisoner may buy per round.</summary>
    public int SabotagesPerPlayer { get; set; } = 1;
    /// <summary>Perks / skills one prisoner may buy per round.</summary>
    public int PerksPerPlayer { get; set; } = 1;
    /// <summary>Different sabotages / curses active at once per round (each effect only once), so guards are never buried.</summary>
    public int MaxSabotagesPerRound { get; set; } = 2;
    /// <summary>Sabotages stop when this many guards or fewer are alive (no ganging up on the last guard).</summary>
    public int StopAtGuardsAlive { get; set; } = 1;
    /// <summary>Points spent on sabotages / curses are shared at round end by the guards still alive.</summary>
    public bool PotToSurvivingGuards { get; set; } = true;
    /// <summary>Amounts offered when chipping in for an aura.</summary>
    public List<int> AuraAmounts { get; set; } = new() { 100, 250, 500 };
    /// <summary>Sounds the Paranoia sabotage plays near a guard (sound events).</summary>
    public List<string> ParanoiaSounds { get; set; } = new() { "Player.DamageBody.Onlooker", "BaseGrenade.Explode" };
    /// <summary>Every effect by id: Enabled and Cost. Missing ids are added with their defaults.</summary>
    public Dictionary<string, ShopEffectSettings> Effects { get; set; } = new();
}

/// <summary>The effect catalogue and shop rules (engine-free, unit-tested).</summary>
internal static class SabotageRules
{
    public static readonly ShopEffect[] Catalogue =
    {
        // ---- sabotages (on): repeat or last all round against the guards
        new("smoke", EffectKind.Sabotage, "Smoke screen", "A random guard is partly blinded for 4 s every 35-50 s", 600, true),
        new("badtrip", EffectKind.Sabotage, "Bad trip", "One guard has a mild drug sway all round; it passes on if they die", 700, true),
        new("hotfloor", EffectKind.Sabotage, "Hot floor", "Every 45 s the guard standing still longest burns for 3 s (cannot kill)", 650, true),
        new("heavyboots", EffectKind.Sabotage, "Heavy boots", "All guards move 8% slower all round", 800, true),
        new("jammed", EffectKind.Sabotage, "Jammed radio", "The warden's pointer and beacon black out 10 s of every 40 s", 600, true),
        new("paranoia", EffectKind.Sabotage, "Paranoia", "Fake sounds play near a random guard every 30-60 s", 450, true),
        // ---- perks (on): for the buyer, all round
        new("thickskin", EffectKind.Perk, "Thick skin", "+15 max HP and 1 HP every 3 s up to 115", 500, true),
        new("lightfeet", EffectKind.Perk, "Light feet", "+8% speed and 15% less fall damage", 450, true),
        new("shadow", EffectKind.Perk, "Shadow", "Guards deal 15% less damage to you while you crouch", 550, true),
        new("secondwind", EffectKind.Perk, "Second wind", "The first time you would drop below 20 HP you heal to 50", 600, true),
        // ---- curses (off by default)
        new("frostbite", EffectKind.Curse, "Frostbite", "Guards hit by a knife or grenade are slowed 30% for 2 s", 500, false),
        new("static", EffectKind.Curse, "Static", "1 in 8 guard shots flashes their screen white for a moment", 500, false),
        new("gravity", EffectKind.Curse, "Gravity well", "Guards have 15% more gravity all round", 600, false),
        new("rustyguns", EffectKind.Curse, "Rusty guns", "Guards lose 40% of their reserve ammo", 550, false),
        new("butterfingers", EffectKind.Curse, "Butterfingers", "Every 60 s a random guard drops their pistol", 650, false),
        new("echo", EffectKind.Curse, "Echo", "Every 20 s the guards flash up for 3 s through walls for prisoners", 500, false),
        new("glowstick", EffectKind.Curse, "Glowstick", "The warden glows for prisoners through walls", 450, false),
        new("blackout", EffectKind.Curse, "Blackout pulse", "Every 45 s the guards' screens go dark for 3 s", 600, false),
        // ---- skills (off by default): for the buyer, all round
        new("vampire", EffectKind.Skill, "Vampire", "Damage you deal to guards heals you 25% of it", 700, false),
        new("longjump", EffectKind.Skill, "Long jump", "Running jumps go 25% further", 450, false),
        new("impulse", EffectKind.Skill, "Impulse", "Taking damage gives +20% speed for 1.5 s", 450, false),
        new("lucky", EffectKind.Skill, "Lucky", "10% chance to ignore a hit", 600, false),
        new("adrenaline", EffectKind.Skill, "Adrenaline", "Below 30 HP: +15% speed and 1 HP/s for 10 s (once)", 500, false),
        new("ghoststep", EffectKind.Skill, "Ghost step", "Crouch-walking is 25% faster", 400, false),
        new("scavenger", EffectKind.Skill, "Scavenger", "Picking up a weapon heals 10 HP (once per weapon)", 350, false),
        new("doublejump", EffectKind.Skill, "Double jump", "One extra jump in the air", 550, false),
        new("ironlungs", EffectKind.Skill, "Iron lungs", "No fall damage, half drowning damage", 300, false),
        new("contraband", EffectKind.Skill, "Contraband", "You get a flashbang", 500, false),
        // ---- auras (off by default): prisoners chip in; active for everyone once the total reaches the cost
        new("riot", EffectKind.Aura, "Riot", "With 3+ rebels alive, every prisoner is 5% faster", 1500, false),
        new("solidarity", EffectKind.Aura, "Solidarity", "When a prisoner dies, prisoners within 300 units heal 15 HP", 1200, false),
        new("underground", EffectKind.Aura, "Underground", "3 minutes into the round a random door or vent opens", 2000, false)
    };

    public static ShopEffect? Find(string id) => Catalogue.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsAgainstGuards(EffectKind k) => k is EffectKind.Sabotage or EffectKind.Curse;
    public static bool IsForSelf(EffectKind k) => k is EffectKind.Perk or EffectKind.Skill;

    /// <summary>Adds missing effects with their defaults (owners keep their own values).</summary>
    public static bool FillDefaults(Dictionary<string, ShopEffectSettings> map)
    {
        var changed = false;
        foreach (var e in Catalogue)
            if (!map.ContainsKey(e.Id)) { map[e.Id] = new ShopEffectSettings { Enabled = e.OnByDefault, Cost = e.Cost }; changed = true; }
        return changed;
    }

    /// <summary>Why a prisoner may not buy an effect now (null = may). Engine-free.</summary>
    public static string? CannotBuy(EffectKind kind, float elapsed, int window, bool joinedThisRound, int boughtOfKind, int perPlayer,
        bool alreadyActive, int activeAgainstGuards, int maxAgainstGuards)
    {
        if (elapsed > window) return "sab.err.window";
        if (joinedThisRound) return "sab.err.joined";
        if (boughtOfKind >= Math.Max(1, perPlayer)) return IsAgainstGuards(kind) ? "sab.err.one_sabotage" : "sab.err.one_perk";
        if (IsAgainstGuards(kind))
        {
            if (alreadyActive) return "sab.err.active";
            if (activeAgainstGuards >= Math.Max(1, maxAgainstGuards)) return "sab.err.max_round";
        }
        else if (alreadyActive) return "sab.err.have";

        return null;
    }

    /// <summary>A random wait in [min, max] seconds.</summary>
    public static float Interval(float min, float max, Random rng) => min + (float)rng.NextDouble() * Math.Max(0f, max - min);
}

/// <summary>
/// The prisoners' effect shop: !sabotage (effects on the guards that repeat or last all round), !perk (lasting bonuses
/// for yourself) and team auras the prisoners fund together. Bought in the first 2 minutes of a round, never in days or
/// LRs; everything stops when last requests or a day start or the guards are down to their last man. Sabotage points are
/// shared by the guards who survive the round. Lead admins switch each effect on/off with !sabotagesetup.
/// </summary>
public sealed partial class SabotageModule : JbModule
{
    private SabotageConfig Cfg => Section<SabotageConfig>("Sabotage");
    public override string Name => "Sabotage";
    public override int Order => 93;
    public override bool Enabled => Cfg.Enabled;

    internal static SabotageModule? Instance { get; private set; }

    private readonly HashSet<string> _active = new();                       // effects against guards running this round
    private readonly Dictionary<int, HashSet<string>> _perks = new();       // slot -> own effects
    private readonly Dictionary<ulong, (int Against, int Self)> _bought = new();
    private readonly Dictionary<string, Dictionary<ulong, int>> _auraFunds = new();
    private readonly HashSet<string> _auras = new();
    private readonly Dictionary<int, int> _joinedRound = new();
    private readonly List<Timer> _timers = new();
    private int _pot;
    private bool _stopped;

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        Config.Update<SabotageConfig>("Sabotage", c => SabotageRules.FillDefaults(c.Effects));
        DamageService.Register(Name, 20, OnDamage);
        JbEvents.LastRequestStarted += OnLrStarted;
        JbEvents.DayStarted += OnDayStarted;
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtons);
    }

    public override void OnUnload(bool hotReload)
    {
        StopAll(null);
        DamageService.Unregister(OnDamage);
        JbEvents.LastRequestStarted -= OnLrStarted;
        JbEvents.DayStarted -= OnDayStarted;
        if (Instance == this) Instance = null;
    }

    private ShopEffectSettings Settings(ShopEffect e) =>
        Cfg.Effects.TryGetValue(e.Id, out var s) ? s : new ShopEffectSettings { Enabled = e.OnByDefault, Cost = e.Cost };

    private bool IsOn(string id) => SabotageRules.Find(id) is { } e && Settings(e).Enabled;

    internal bool Active(string id) => !_stopped && _active.Contains(id);
    internal bool Has(CCSPlayerController c, string id) => !_stopped && _perks.TryGetValue(c.Slot, out var s) && s.Contains(id);
    private bool AuraOn(string id) => !_stopped && _auras.Contains(id);

    // ================================================================= round lifecycle

    public override void OnPlayerConnected(JbPlayer player) => _joinedRound[player.Slot] = Round.Number;

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _perks.Remove(player.Slot);
        _joinedRound.Remove(player.Slot);
    }

    public override void OnRoundStart()
    {
        StopAll(null);
        _active.Clear();
        _perks.Clear();
        _bought.Clear();
        _auraFunds.Clear();
        _auras.Clear();
        _pot = 0;
        _stopped = false;
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        StopAll(null);
        RefundUnfinishedAuras();
        PayPot();
    }

    private void OnLrStarted(JbPlayer p, LrFlags flags) => StopAll("sab.stopped_lr");
    private void OnDayStarted(JbPlayer? p, DayType t, bool ffa) => StopAll("sab.stopped_day");

    /// <summary>End every running effect (the pot is still paid at round end).</summary>
    private void StopAll(string? messageKey)
    {
        var wasRunning = !_stopped && (_active.Count > 0 || _perks.Count > 0 || _auras.Count > 0);
        _stopped = true;
        foreach (var t in _timers) { try { t.Kill(); } catch { /* gone */ } }
        _timers.Clear();
        foreach (var c in Players.All())
        {
            foreach (var key in MovementKeys) { Movement.ClearSpeed(c, key); Movement.ClearGravity(c, key); }
            Hud.Clear(c, "sabotage");
        }

        StopBadTrip();
        if (_glowstick != null) { Glow.Remove(_glowstick); _glowstick = null; }
        WardenJam(false);
        if (wasRunning && messageKey != null) Chat.TellAll(messageKey);
    }

    private void PayPot()
    {
        if (!Cfg.PotToSurvivingGuards || _pot <= 0) return;
        var guards = Players.AliveGuards().Where(c => !c.IsBot && c.Jb() != null).ToList();
        if (guards.Count == 0) guards = Players.Guards().Where(c => !c.IsBot && c.Jb() != null).ToList();
        if (guards.Count == 0) { _pot = 0; return; }
        var shares = PoolRules.Split(_pot, guards.Count);
        for (var i = 0; i < guards.Count; i++) Jb.Points.AddPoints(guards[i].Jb()!, shares[i], "sabotage pot");
        Chat.TellAll("sab.pot_paid", _pot, guards.Count, shares[^1]);
        _pot = 0;
    }

    private void RefundUnfinishedAuras()
    {
        foreach (var (id, funds) in _auraFunds)
            if (!_auras.Contains(id))
                foreach (var (steamId, amount) in funds) Jb.Points.AddPoints(steamId, amount);
        _auraFunds.Clear();
    }

    private void OnTick()
    {
        if (_stopped || (_active.Count == 0 && _perks.Count == 0 && _auras.Count == 0)) return;
        // The last guards standing are left alone.
        if (_active.Count > 0 && Players.AliveGuards().Count() <= Cfg.StopAtGuardsAlive) { StopAll("sab.stopped_last_guard"); return; }
        TickPerks();
        if (Server.TickCount % 128 == 0) ShowGuardHud();
    }

    private void ShowGuardHud()
    {
        if (_active.Count == 0) return;
        var names = string.Join(", ", _active.Select(id => SabotageRules.Find(id)?.Name ?? id));
        var html = Hud.Color(Hud.Escape(Lang.T("sab.hud", names)), "#FF6A4A");
        foreach (var g in Players.Guards().Where(c => !c.IsBot)) Hud.Set(g, "sabotage", html, 4, 2.5f);
    }

    // ================================================================= shop commands

    [ConsoleCommand("css_sabotage", "sm_sabotage - (prisoners) buy an effect on the guards, a perk, or chip in for a team aura")]
    [ConsoleCommand("css_sab", "sm_sab - (prisoners) the sabotage shop")]
    [OpenCommand]
    public void CmdSabotage(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        OpenShop(caller, null);
    }

    [ConsoleCommand("css_perk", "sm_perk - (prisoners) buy a perk or skill for this round")]
    [OpenCommand]
    public void CmdPerk(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        OpenShop(caller, EffectKind.Perk);
    }

    private string? ShopClosed(CCSPlayerController c)
    {
        if (Jb.Points is NullServices) return "sab.err.no_points";
        if (!Round.IsActive) return "sab.err.round";
        if (Jb.Days.IsInProgress) return "sab.err.day";
        if (Jb.LastRequests.NumStartedIgnore(LrFlags.None) > 0) return "sab.err.lr";
        if (c.Team != CsTeam.Terrorist) return "sab.err.prisoners";
        return null;
    }

    private void OpenShop(CCSPlayerController c, EffectKind? only)
    {
        if (ShopClosed(c) is { } why) { Chat.Tell(c, why, Cfg.BuyWindowSeconds); return; }
        var have = c.Jb() is { } jp ? Jb.Points.GetPoints(jp) : 0;
        var m = new JbMenu(Lang.T("sab.title")) { Subtitle = Hud.Escape(Lang.T("sab.you_have", have, Math.Max(0, (int)(Cfg.BuyWindowSeconds - Round.Elapsed)))) };
        var kinds = only == EffectKind.Perk
            ? new[] { EffectKind.Perk, EffectKind.Skill }
            : new[] { EffectKind.Sabotage, EffectKind.Curse, EffectKind.Perk, EffectKind.Skill, EffectKind.Aura };
        foreach (var kind in kinds)
        {
            var list = SabotageRules.Catalogue.Where(e => e.Kind == kind && Settings(e).Enabled).ToList();
            if (list.Count == 0) continue;
            var k = kind;
            m.Add(Lang.T("sab.cat." + kind.ToString().ToLowerInvariant(), list.Count), p => OpenKind(p, k));
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("sab.none_on"));
        m.Open(c);
    }

    private void OpenKind(CCSPlayerController c, EffectKind kind)
    {
        var m = new JbMenu(Lang.T("sab.cat_title." + kind.ToString().ToLowerInvariant()));
        foreach (var e in SabotageRules.Catalogue.Where(e => e.Kind == kind && Settings(e).Enabled))
        {
            var effect = e;
            var cost = Settings(e).Cost;
            string label;
            if (kind == EffectKind.Aura)
            {
                var raised = _auraFunds.TryGetValue(e.Id, out var f) ? f.Values.Sum() : 0;
                label = _auras.Contains(e.Id) ? Lang.T("sab.aura_active", e.Name) : Lang.T("sab.aura_item", e.Name, raised, cost);
            }
            else label = Lang.T("sab.item", e.Name, cost);
            m.Add(Hud.Escape(label), p =>
            {
                Chat.Tell(p, "sab.describe", effect.Name, effect.Description);
                if (kind == EffectKind.Aura) OpenAuraAmounts(p, effect);
                else Buy(p, effect);
            }, kind != EffectKind.Aura || !_auras.Contains(e.Id));
        }

        m.Open(c);
    }

    private void Buy(CCSPlayerController c, ShopEffect e)
    {
        if (ShopClosed(c) is { } why) { Chat.Tell(c, why, Cfg.BuyWindowSeconds); return; }
        var jp = c.Jb();
        if (jp == null) return;
        if (SabotageRules.IsForSelf(e.Kind) && !c.IsAliveP()) { Chat.Tell(c, "sab.err.alive"); return; }
        var cost = Settings(e).Cost;
        var steamId = c.SteamID;
        var bought = _bought.GetValueOrDefault(steamId);
        var against = SabotageRules.IsAgainstGuards(e.Kind);
        var cannot = SabotageRules.CannotBuy(e.Kind, Round.Elapsed, Cfg.BuyWindowSeconds,
            _joinedRound.TryGetValue(c.Slot, out var jr) && jr == Round.Number,
            against ? bought.Against : bought.Self, against ? Cfg.SabotagesPerPlayer : Cfg.PerksPerPlayer,
            against ? _active.Contains(e.Id) : Has(c, e.Id), _active.Count, Cfg.MaxSabotagesPerRound);
        if (cannot != null) { Chat.Tell(c, cannot, Cfg.BuyWindowSeconds); return; }
        if (Jb.Points.GetPoints(jp) < cost) { Chat.Tell(c, "sab.err.points", cost); return; }

        Jb.Points.AddPoints(jp, -cost, "sabotage " + e.Id);
        _stopped = false;
        if (against)
        {
            _bought[steamId] = (bought.Against + 1, bought.Self);
            _active.Add(e.Id);
            _pot += cost;
            StartAgainstGuards(e.Id);
            // The buyer stays anonymous: everyone is told what is running, not who bought it.
            Chat.TellAll("sab.started", e.Name, e.Description);
            ShowGuardHud();
        }
        else
        {
            _bought[steamId] = (bought.Against, bought.Self + 1);
            if (!_perks.TryGetValue(c.Slot, out var set)) _perks[c.Slot] = set = new HashSet<string>();
            set.Add(e.Id);
            StartSelf(c, e.Id);
            Chat.Tell(c, "sab.perk_bought", e.Name, e.Description);
        }

        Logger.LogInformation("[Sabotage] {Player} bought {Effect} ({Cost})", c.PlayerName, e.Id, cost);
    }

    private void OpenAuraAmounts(CCSPlayerController c, ShopEffect e)
    {
        var m = new JbMenu(Lang.T("sab.aura_title", e.Name));
        foreach (var a in Cfg.AuraAmounts.Where(a => a > 0).Distinct().OrderBy(a => a))
        {
            var amount = a;
            m.Add(amount.ToString(), p => ChipIn(p, e, amount));
        }

        m.Open(c);
    }

    private void ChipIn(CCSPlayerController c, ShopEffect e, int amount)
    {
        if (ShopClosed(c) is { } why) { Chat.Tell(c, why, Cfg.BuyWindowSeconds); return; }
        if (Round.Elapsed > Cfg.BuyWindowSeconds) { Chat.Tell(c, "sab.err.window", Cfg.BuyWindowSeconds); return; }
        if (_auras.Contains(e.Id)) { Chat.Tell(c, "sab.aura_already", e.Name); return; }
        var jp = c.Jb();
        if (jp == null) return;
        if (!_auraFunds.TryGetValue(e.Id, out var funds)) _auraFunds[e.Id] = funds = new Dictionary<ulong, int>();
        if (funds.ContainsKey(c.SteamID)) { Chat.Tell(c, "sab.err.chipped"); return; }
        if (Jb.Points.GetPoints(jp) < amount) { Chat.Tell(c, "sab.err.points", amount); return; }
        Jb.Points.AddPoints(jp, -amount, "aura " + e.Id);
        funds[c.SteamID] = amount;
        var total = funds.Values.Sum();
        var cost = Settings(e).Cost;
        if (total >= cost)
        {
            _stopped = false;
            _auras.Add(e.Id);
            StartAura(e.Id);
            Chat.TellAll("sab.aura_on", e.Name, e.Description);
        }
        else Chat.Tell(c, "sab.aura_chipped", amount, e.Name, total, cost);
    }

    // ================================================================= admin: !sabotagesetup

    [ConsoleCommand("css_sabotagesetup", "sm_sabotagesetup - (admin) switch shop effects on/off for this server")]
    public void CmdSetup(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "config")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (caller == null)
        {
            foreach (var e in SabotageRules.Catalogue)
                info.ReplyToCommand($"{(Settings(e).Enabled ? "[on] " : "[off]")} {e.Kind,-8} {e.Id,-14} {Settings(e).Cost,5}  {e.Name}");
            return;
        }

        OpenSetup(caller, null);
    }

    private void OpenSetup(CCSPlayerController c, EffectKind? kind)
    {
        if (kind == null)
        {
            var root = new JbMenu(Lang.T("sab.setup_title"));
            foreach (var k in Enum.GetValues<EffectKind>())
            {
                var kk = k;
                var all = SabotageRules.Catalogue.Where(e => e.Kind == k).ToList();
                root.Add(Lang.T("sab.setup_cat", Lang.T("sab.kind." + k.ToString().ToLowerInvariant()), all.Count(e => Settings(e).Enabled), all.Count), p => OpenSetup(p, kk));
            }

            root.Open(c);
            return;
        }

        var m = new JbMenu(Lang.T("sab.kind." + kind.Value.ToString().ToLowerInvariant()));
        foreach (var e in SabotageRules.Catalogue.Where(e => e.Kind == kind))
        {
            var effect = e;
            m.Add(Hud.Escape($"{(Settings(e).Enabled ? "[ON] " : "[OFF] ")}{e.Name} ({Settings(e).Cost})"), p =>
            {
                Config.Update<SabotageConfig>("Sabotage", cfg =>
                {
                    SabotageRules.FillDefaults(cfg.Effects);
                    cfg.Effects[effect.Id].Enabled = !cfg.Effects[effect.Id].Enabled;
                });
                Config.SaveIfDirty(Logger);
                Jb.Log.LogEvent($"{p.PlayerName} switched shop effect {effect.Name} {(IsOn(effect.Id) ? "on" : "off")}", p.Jb(), null, LogType.Admin);
                OpenSetup(p, effect.Kind);
            });
        }

        m.Open(c);
    }
}
