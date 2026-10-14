using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>A player's best time on one map (data/speedrun.json, key "map|steamid").</summary>
public sealed class SpeedrunRecord
{
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>
/// Speedrun freeday: after a countdown the prisoners race to a "Speedrun Finish" zone (drawn with the zone editor,
/// !zones). Places get DaysNew.SpeedrunRewards points, personal bests and the map record are saved, and !speedrun shows the
/// map's top times. Player damage is off during the race so nobody can be stopped by a guard.
/// </summary>
public sealed class SpeedrunDay : JbModule
{
    private NewDaysConfig Cfg => Section<NewDaysConfig>("DaysNew");

    public override string Name => "Days.Speedrun";
    public override int Order => 12;

    internal const string FinishZoneKey = "speedrun_finish";
    private const string Ns = "speedrun";

    private DayDefinition? _day;
    private bool _racing;
    private float _start;
    private readonly HashSet<int> _racers = new();
    private readonly List<int> _finished = new();
    private Timer? _countdown, _hud;
    private int _countdownLeft;
    private readonly List<CBaseModelEntity> _finishProps = new(); // workshop pack finish line model on each finish zone

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);

    private static string Map
    {
        get
        {
            try { return Server.MapName ?? "?"; }
            catch { return "?"; }
        }
    }

    public override void OnLoad(bool hotReload)
    {
        Jb.Zones.RegisterType(new SpeedrunFinishZone(this));
        if (Jb.Days is not DaysModule days || !Cfg.SpeedrunEnabled) return;
        _day = new DayDefinition
        {
            Name = "Speedrun", Type = DayType.Freeday, Flags = DayFlags.None,
            Description = "Race from the cells to the finish zone; the fastest prisoners get points.",
            CanStart = () => Jb.Zones.OfType(FinishZoneKey).Any(),
            OnStart = _ => Begin(),
            OnEnd = _ => Stop()
        };
        days.Register(_day);
        DamageService.Register(Name, 90, OnDamage);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Stop();
    }

    public override void OnPlayerDisconnect(JbPlayer player) => _racers.Remove(player.Slot);

    private void Begin()
    {
        Stop();
        if (Extras.WorkshopPackModule.Model(c => c.FinishLineModel) is { } model)
            foreach (var z in Jb.Zones.OfType(FinishZoneKey))
            {
                var b = z.Box.BottomCenter;
                if (Fx.Prop(model, new Vector(b.X, b.Y, b.Z), new QAngle(0, z.Angles.Y, 0)) is { } prop) _finishProps.Add(prop);
            }

        _racers.Clear();
        _finished.Clear();
        foreach (var c in Players.AlivePrisoners())
        {
            _racers.Add(c.Slot);
            c.SetMoveType(MoveType_t.MOVETYPE_NONE);
        }

        _countdownLeft = Math.Clamp(Cfg.SpeedrunCountdown, 1, 30);
        Chat.TellAll("days.speedrun.how");
        Hud.SetGlobal("days.speedrun", Lang.T("days.speedrun.countdown", _countdownLeft), 40, 1.2f);
        _countdown = Round.Timer(1f, () =>
        {
            if (!Active) return;
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                Hud.SetGlobal("days.speedrun", Lang.T("days.speedrun.countdown", _countdownLeft), 40, 1.2f);
                return;
            }

            try { _countdown?.Kill(); }
            catch { /* already stopped */ }
            _countdown = null;
            Go();
        }, true);
    }

    private void Go()
    {
        if (!Jb.Cells.HaveOpened) Jb.Cells.ForceOpen();
        foreach (var slot in _racers)
        {
            var c = Players.Get(slot).Controller;
            if (c != null && c.IsAliveP() && c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) c.SetMoveType(MoveType_t.MOVETYPE_WALK);
        }

        _racing = true;
        _start = Server.CurrentTime;
        Hud.SetGlobal("days.speedrun", Lang.T("days.speedrun.go"), 40, 1.5f);
        _hud = Round.Timer(0.5f, ShowClock, true);
    }

    private void Stop()
    {
        foreach (var p in _finishProps) Fx.SafeRemove(p);
        _finishProps.Clear();
        if (_countdown != null)
        {
            // Ended during the countdown: nobody may stay frozen.
            foreach (var slot in _racers)
            {
                var c = Players.Get(slot).Controller;
                if (c != null && c.IsAliveP() && c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            }
        }

        foreach (var t in new[] { _countdown, _hud })
        {
            try { t?.Kill(); }
            catch { /* already stopped */ }
        }

        _countdown = _hud = null;
        _racing = false;
        Hud.ClearGlobal("days.speedrun");
        Hud.ClearPrefix("days.speedrun");
    }

    private void ShowClock()
    {
        if (!Active || !_racing) return;
        var t = DayLogic.RaceTime(Server.CurrentTime - _start);
        foreach (var slot in _racers)
        {
            if (_finished.Contains(slot)) continue;
            var c = Players.Get(slot).Controller;
            if (c != null && c.IsAliveP() && !c.IsBot) Hud.Set(c, "days.speedrun.clock", Lang.T("days.speedrun.clock", t), 25, 1f);
        }
    }

    internal void OnFinishZone(CCSPlayerController c)
    {
        if (!Active || !_racing || !_racers.Contains(c.Slot) || _finished.Contains(c.Slot) || !c.IsAliveP()) return;
        var seconds = Math.Round(Server.CurrentTime - _start, 2);
        _finished.Add(c.Slot);
        var place = _finished.Count;
        var p = c.Jb();
        var points = DayLogic.PlaceReward(Cfg.SpeedrunRewards, place);
        Hud.Clear(c, "days.speedrun.clock");
        Chat.TellAll("days.speedrun.finished", place, c.PlayerName, DayLogic.RaceTime(seconds));
        Fx.BeaconPulse(c, Color.FromArgb(255, 60, 230, 90));

        if (p != null && p.SteamId64 != 0)
        {
            var map = Map;
            var record = BestOnMap(map);
            var key = map + "|" + p.SteamId64;
            var own = Storage.Get<SpeedrunRecord>(Ns, key);
            if (DayLogic.IsBetter(seconds, own?.Seconds))
            {
                Storage.Set(Ns, key, new SpeedrunRecord { Name = c.PlayerName, Seconds = seconds });
                if (own != null) Chat.Tell(c, "days.speedrun.personal_best", DayLogic.RaceTime(seconds));
            }

            if (record != null && seconds < record.Seconds)
            {
                Chat.TellAll("days.speedrun.map_record", c.PlayerName, DayLogic.RaceTime(seconds), DayLogic.RaceTime(record.Seconds), record.Name);
                points += Math.Max(0, Cfg.SpeedrunRecordPoints);
            }
        }

        if (p != null && points > 0) Jb.Points.AddPoints(p, points, "Speedrun");

        var stillRacing = _racers.Count(s => !_finished.Contains(s) && Players.Get(s).Controller is { } rc && rc.IsAliveP());
        if (stillRacing == 0)
        {
            _racing = false;
            Chat.TellAll("days.speedrun.all_done");
        }
    }

    private static SpeedrunRecord? BestOnMap(string map) =>
        Storage.All<SpeedrunRecord>(Ns)
            .Where(kv => kv.Key.StartsWith(map + "|", StringComparison.OrdinalIgnoreCase) && kv.Value.Seconds > 0)
            .Select(kv => kv.Value)
            .OrderBy(r => r.Seconds)
            .FirstOrDefault();

    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active || !_racing && _countdown == null) return HookResult.Continue;
        if (ctx.IsFall && Cfg.SpeedrunNoFallDamage) return HookResult.Handled;
        return ctx.Attacker != null && !ctx.IsSelf ? HookResult.Handled : HookResult.Continue;
    }

    [ConsoleCommand("css_speedrun", "sm_speedrun - best Speedrun day times on this map")]
    [ConsoleCommand("css_sr", "sm_sr - best Speedrun day times on this map")]
    public void CmdSpeedrun(CCSPlayerController? caller, CommandInfo info)
    {
        var map = Map;
        var top = Storage.All<SpeedrunRecord>(Ns)
            .Where(kv => kv.Key.StartsWith(map + "|", StringComparison.OrdinalIgnoreCase) && kv.Value.Seconds > 0)
            .Select(kv => kv.Value)
            .OrderBy(r => r.Seconds)
            .Take(10)
            .ToList();
        if (top.Count == 0)
        {
            Chat.ReplyT(caller, info, "days.speedrun.no_times", map);
            return;
        }

        Chat.ReplyT(caller, info, "days.speedrun.top_title", map);
        for (var i = 0; i < top.Count; i++) Chat.ReplyT(caller, info, "days.speedrun.top_line", i + 1, top[i].Name, DayLogic.RaceTime(top[i].Seconds));
    }
}

/// <summary>Zone type "Speedrun Finish": touching it ends a prisoner's Speedrun day race.</summary>
internal sealed class SpeedrunFinishZone : ZoneType
{
    private readonly SpeedrunDay _owner;

    public SpeedrunFinishZone(SpeedrunDay owner) => _owner = owner;

    public override string Key => SpeedrunDay.FinishZoneKey;
    public override string DisplayName => "Speedrun Finish";
    public override string Description => "The finish line of the Speedrun freeday.";

    public override void OnEnter(Zone zone, CCSPlayerController player) => _owner.OnFinishZone(player);
}
