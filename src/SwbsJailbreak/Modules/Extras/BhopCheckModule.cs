using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "BhopCheck" (bhc_* cvars).</summary>
public sealed class BhopCheckConfig
{
    public bool Enabled { get; set; } = true;
    public float WarnInputsPerTick { get; set; } = 0.45f; // bhc_warn_inputs (marks suspicious rows with *)
    public float WarnPerfs { get; set; } = 0.8f;          // bhc_warn_perfs
    public int MaxBhops { get; set; } = 20;               // bhc_max_bhops
    public bool AllowPerfSounds { get; set; } = false;    // bhc_allow_perf_sounds
    public string PerfSound { get; set; } = "UIPanorama.generic_button_press";

    /// <summary>Tell online admins (chat) and the jail log when a player's recent hops look scripted.</summary>
    public bool AlertAdmins { get; set; } = true;

    /// <summary>Hops looked at for the alert (the last N); alert when at least WarnPerfs of them are perfect.</summary>
    public int AlertSampleSize { get; set; } = 15;

    /// <summary>Seconds between two alerts about the same player.</summary>
    public float AlertCooldownSeconds { get; set; } = 60f;
}

internal struct BhopEntry
{
    public int Inputs, AirTicks, GroundTicks, InputTicks;
    public float InputsPerTick, Speed;
    public int Tick;
}

internal sealed class BhopState
{
    public readonly List<BhopEntry> Log = new();
    public bool InJump;
    public int AirTicks, Inputs, GroundTicks;
    public int InputTicks;
    public bool PerfSound;
    public bool WasOnGround;
    public int LastJumpTick = -100;
    public float NextAlert;
}

/// <summary>
/// SMPlugins BhopCheck: records the last N bunny hops of every player (inputs while airborne, inputs per tick,
/// ground ticks before the next jump, speed, perfect hops) for admins to inspect from console (!bhc / !bhce).
/// CS2 note: input counting samples the networked button state every server tick; CS2 sub-tick input means very fast
/// scroll-wheel presses inside one tick can be undercounted, so inputs are a lower bound (perf/ground-tick data is exact).
/// Jumps are detected from the ground -> air transition every tick (player_jump alone is not fired reliably on current
/// CS2 builds, which left the log empty); the event is still used and de-duplicated.
/// </summary>
public sealed class BhopCheckModule : JbModule
{
    private BhopCheckConfig Cfg => Section<BhopCheckConfig>("BhopCheck");

    public override string Name => "BhopCheck";
    public override int Order => 120;
    public override bool Enabled => Cfg.Enabled;

    private static BhopState S(JbPlayer p) => p.Data<BhopState>();

    public override void OnLoad(bool hotReload)
    {
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterEventHandler<EventPlayerJump>(OnJump);
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        if (player.SteamId64 != 0) S(player).PerfSound = Storage.Get<bool>("bhopcheck_perfsound", player.SteamId64.ToString());
    }

    private HookResult OnJump(EventPlayerJump ev, GameEventInfo info)
    {
        var p = Players.Get(ev.Userid);
        if (p == null || p.IsBot) return HookResult.Continue;
        Jumped(p, S(p));
        return HookResult.Continue;
    }

    private void Jumped(JbPlayer p, BhopState s)
    {
        var tick = Server.TickCount;
        if (tick - s.LastJumpTick <= 2) return; // event and tick detection saw the same jump
        s.LastJumpTick = tick;
        if (s.InJump) PushBhop(p, s);
        s.InJump = true;
        s.AirTicks = 0;
        s.Inputs = 0;
        s.GroundTicks = 0;
    }

    private void OnTick()
    {
        foreach (var c in Players.Humans())
        {
            var p = c.Jb();
            if (p == null) continue;
            var s = S(p);
            var pawn = c.PawnOrNull();
            if (pawn == null) continue;
            var jumpHeld = (c.Buttons & PlayerButtons.Jump) != 0;
            s.InputTicks = jumpHeld ? s.InputTicks + 1 : 0;
            var onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
            var wasOnGround = s.WasOnGround;
            s.WasOnGround = onGround;
            // Left the ground moving up fast: a jump (walking off a ledge or being pushed has no upward speed).
            if (wasOnGround && !onGround && pawn.MoveType == MoveType_t.MOVETYPE_WALK && (pawn.AbsVelocity?.Z ?? 0) > 200f)
            {
                Jumped(p, s);
                s.AirTicks++;
                continue;
            }

            if (!s.InJump) continue;
            if (!c.IsAliveP() || pawn.MoveType is MoveType_t.MOVETYPE_NOCLIP or MoveType_t.MOVETYPE_LADDER)
            {
                s.InJump = false;
                continue;
            }

            if ((pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0)
            {
                if (s.GroundTicks++ > 20) { s.InJump = false; continue; }
            }
            else
            {
                if (s.GroundTicks > 0) { s.InJump = false; continue; }
                s.AirTicks++;
            }

            if (s.InputTicks == 1) s.Inputs++;
        }
    }

    private void PushBhop(JbPlayer p, BhopState s)
    {
        var c = p.Controller;
        var e = new BhopEntry
        {
            Inputs = s.Inputs, AirTicks = s.AirTicks, GroundTicks = s.GroundTicks, InputTicks = s.InputTicks,
            InputsPerTick = s.AirTicks > 0 ? (float)s.Inputs / s.AirTicks : 0f,
            Speed = c?.HorizontalSpeed() ?? 0f,
            Tick = Server.TickCount
        };
        if (Cfg.AllowPerfSounds && s.PerfSound && e.GroundTicks == 1 && e.InputTicks == 1) Sound.Play(c, Cfg.PerfSound);
        s.Log.Insert(0, e);
        if (s.Log.Count > Math.Clamp(Cfg.MaxBhops, 20, 100)) s.Log.RemoveAt(s.Log.Count - 1);
        CheckAlert(p, s);
    }

    /// <summary>Engine-free: share of perfect hops (landed and jumped on the same tick) in the newest <paramref name="sample"/> entries.</summary>
    internal static float PerfRatio(IReadOnlyList<BhopEntry> log, int sample)
    {
        var n = Math.Min(log.Count, sample);
        if (n == 0) return 0f;
        var perfs = 0;
        for (var i = 0; i < n; i++) if (log[i].GroundTicks <= 1) perfs++;
        return (float)perfs / n;
    }

    private void CheckAlert(JbPlayer p, BhopState s)
    {
        if (!Cfg.AlertAdmins) return;
        var sample = Math.Clamp(Cfg.AlertSampleSize, 5, 100);
        if (s.Log.Count < sample) return;
        var c = p.Controller;
        if (c == null || Movement.HasAutoBhop(c)) return;
        var auto = Cvars.Get("sv_autobunnyhopping");
        if (auto is "1" or "true") return; // auto bhop day: every hop is perfect by design
        var now = Server.CurrentTime;
        if (now < s.NextAlert) return;
        var ratio = PerfRatio(s.Log, sample);
        if (ratio < Cfg.WarnPerfs) return;
        s.NextAlert = now + Math.Max(5f, Cfg.AlertCooldownSeconds);
        var pct = (int)MathF.Round(ratio * 100);
        var line = Lang.T("bhc.alert", c.PlayerName, pct, sample);
        foreach (var admin in Players.Humans().Where(x => Perms.IsAdmin(x))) Chat.Print(admin, line);
        Logger.LogWarning("[BhopCheck] {Name} ({SteamId}): {Pct}% perfect hops in the last {Sample}", c.PlayerName, p.SteamId64, pct, sample);
        Jb.Log.LogEvent($"BhopCheck: {c.PlayerName} hit {pct}% perfect bhops over the last {sample} hops", p, null, LogType.Admin);
    }

    [ConsoleCommand("css_bhopcheck", "sm_bhopcheck <#userid|name> - bhop log (console)")]
    [ConsoleCommand("css_bhc", "sm_bhc <#userid|name> - bhop log (console)")]
    public void CmdBhc(CCSPlayerController? caller, CommandInfo info) => Print(caller, info, false);

    [ConsoleCommand("css_bhce", "sm_bhce <#userid|name> - extended bhop log (console)")]
    public void CmdBhce(CCSPlayerController? caller, CommandInfo info) => Print(caller, info, true);

    private void Print(CCSPlayerController? caller, CommandInfo info, bool extended)
    {
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", (extended ? "css_bhce" : "css_bhc") + " <#userid|name>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        var log = S(target.Jb()!).Log;
        void W(string line) => Chat.PrintToConsole(caller, line);
        W($"\n\t\tBhopCheck (SWBS Jailbreak CS2)");
        W($"\tName: {target.PlayerName}\n\tSteam ID: {target.SteamID}\n\tMap: {Server.MapName}");
        W($"\t{DateTime.Now:yyyy-MM-dd-HH:mm:ss}");
        if (log.Count == 0)
        {
            W("\n\tNo bhops logged for this player");
            if (caller != null && info.CallingContext == CommandCallingContext.Chat) Chat.Tell(caller, "bhc.none", target.PlayerName);
            return;
        }
        if (extended)
        {
            W("\t----------------- Bhop Log Extended -----------------");
            W("\tInputs\tAir\tI/T\tGround\tSpeed\tTick\tHeld");
            W("\t-----------------------------------------------------");
            foreach (var e in log)
                W($"\t{e.Inputs}\t{e.AirTicks}\t{(e.InputsPerTick >= Cfg.WarnInputsPerTick ? "*" : " ")}{e.InputsPerTick:0.00}\t{(e.GroundTicks <= 1 ? "# " : "  ")}{e.GroundTicks}\t{e.Speed:0.0}\t{e.Tick}\t{(e.InputTicks <= 2 ? "N " : "A ")}{e.InputTicks}");
        }
        else
        {
            W("\t------------- Bhop Log -------------");
            W("\tInputs\tI/T\tGround\tSpeed\tType");
            W("\t------------------------------------");
            foreach (var e in log)
                W($"\t{e.Inputs}\t{e.InputsPerTick:0.00}\t{(e.GroundTicks <= 1 ? "# " : "  ")}{e.GroundTicks}\t{e.Speed:0.0}\t{(e.InputTicks <= 2 ? "Normal " : "Auto ")}");
        }

        var n = (float)log.Count;
        var air = log.Sum(e => e.AirTicks);
        var perfs = log.Count(e => e.GroundTicks <= 1) / n;
        W("\n\t----\t----\tAvg\t----\t----");
        W("\tInputs\tI/T\tGround\tSpeed\tPerfs");
        W($"\t{log.Sum(e => e.Inputs) / n:0.0}\t{(air > 0 ? log.Sum(e => e.Inputs) / (float)air : 0):0.00}\t{log.Sum(e => e.GroundTicks) / n:0.0}\t{log.Sum(e => e.Speed) / n:0.0}\t{perfs * 100:0}%{(perfs >= Cfg.WarnPerfs ? "  (above warn threshold)" : "")}");
        if (caller != null && info.CallingContext == CommandCallingContext.Chat) Chat.Tell(caller, "bhc.printed");
    }

    [ConsoleCommand("css_perfsound", "sm_perfsound - toggle the perfect-jump sound")]
    public void CmdPerfSound(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Cfg.AllowPerfSounds) { Chat.Tell(caller, "bhc.perfsound.disabled"); return; }
        var p = caller.Jb()!;
        var s = S(p);
        s.PerfSound = !s.PerfSound;
        if (p.SteamId64 != 0) Storage.Set("bhopcheck_perfsound", p.SteamId64.ToString(), s.PerfSound);
        Chat.Tell(caller, s.PerfSound ? "bhc.perfsound.on" : "bhc.perfsound.off");
    }
}
