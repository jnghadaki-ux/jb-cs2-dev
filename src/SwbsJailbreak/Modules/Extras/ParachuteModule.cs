using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "Parachute" (SMPlugins Parachute.sp).</summary>
public sealed class ParachuteConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>true: everyone has a parachute. false: only players given one with !giveparachute (the original).</summary>
    public bool Everyone { get; set; } = true;

    /// <summary>Fall speed (units/s) while the parachute is open.</summary>
    public float FallSpeed { get; set; } = 90f;

    /// <summary>Also usable during days (never during last requests: the fall damage contest would be trivial).</summary>
    public bool AllowDuringDays { get; set; } = false;
}

/// <summary>
/// Port of Parachute.sp. The original equipped CS:GO's Danger Zone parachute through gamedata signatures; CS2 has no
/// parachute, so holding USE (E) while falling caps the fall speed instead. Admins can give / remove parachutes when
/// Everyone is off (sm_giveparachute / sm_removeparachute, @css/ban like the original ADMFLAG_BAN).
/// </summary>
public sealed class ParachuteModule : JbModule
{
    private ParachuteConfig Cfg => Section<ParachuteConfig>("Parachute");

    public override string Name => "Parachute";
    public override int Order => 117;
    public override bool Enabled => Cfg.Enabled;

    private readonly bool[] _given = new bool[Players.MaxSlots];

    public override void OnLoad(bool hotReload) => Plugin.RegisterListener<Listeners.OnTick>(OnTick);

    public override void OnPlayerDisconnect(JbPlayer player) => _given[player.Slot] = false;

    public override void OnMapStart(string mapName) => Array.Clear(_given);

    private bool Has(CCSPlayerController c) => Cfg.Everyone || _given[c.Slot];

    /// <summary>Engine-free (unit-tested): the new vertical speed, or null when the parachute does not act.</summary>
    internal static float? Glide(float verticalSpeed, bool holdingUse, bool onGround, float fallSpeed)
    {
        if (!holdingUse || onGround) return null;
        var cap = -Math.Abs(fallSpeed);
        return verticalSpeed < cap ? cap : null;
    }

    private void OnTick()
    {
        if (!Cfg.AllowDuringDays && Jb.Days.IsInProgress) return;
        foreach (var c in Players.Alive())
        {
            if (c.IsBot || !Has(c)) continue;
            try
            {
                var pawn = c.PawnOrNull();
                if (pawn == null) continue;
                var holding = (c.Buttons & PlayerButtons.Use) != 0;
                if (!holding) continue;
                var v = pawn.AbsVelocity;
                var newZ = Glide(v.Z, holding, c.OnGround(), Cfg.FallSpeed);
                if (newZ == null) continue;
                if (c.Jb() is { } p && Jb.LastRequests.IsInLastRequest(p)) continue;
                pawn.Teleport(null, null, new Vector(v.X, v.Y, newZ.Value));
            }
            catch
            {
                // pawn went away mid-tick
            }
        }
    }

    [ConsoleCommand("css_giveparachute", "sm_giveparachute <target> - give a parachute (when Parachute.Everyone is off)")]
    public void CmdGive(CCSPlayerController? caller, CommandInfo info) => SetParachute(caller, info, true);

    [ConsoleCommand("css_removeparachute", "sm_removeparachute <target> - remove a parachute")]
    public void CmdRemove(CCSPlayerController? caller, CommandInfo info) => SetParachute(caller, info, false);

    private void SetParachute(CCSPlayerController? caller, CommandInfo info, bool give)
    {
        if (!Perms.Has(caller, "fun")) { Chat.ReplyT(caller, info, "core.no_access"); return; } // Level 3+ (give items)
        if (Cfg.Everyone) { Chat.ReplyT(caller, info, "chute.everyone"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", give ? "!giveparachute <target>" : "!removeparachute <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        foreach (var c in t.Players)
        {
            _given[c.Slot] = give;
            Chat.Tell(c, give ? "chute.given" : "chute.removed");
        }

        Chat.ReplyT(caller, info, give ? "chute.gave" : "chute.took", t.Description);
        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} {(give ? "gave a parachute to" : "removed the parachute of")} {t.Description}", caller.Jb(), null, LogType.Admin);
    }
}
