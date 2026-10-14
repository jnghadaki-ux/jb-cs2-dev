using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "Points" (UltJB_Points.sp).</summary>
public sealed class PointsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Points are only given with at least this many players in game (bots count, like the original).</summary>
    public int MinPlayers { get; set; } = 10;

    public int GuardKill { get; set; } = 50;
    public int WardenKill { get; set; } = 100;
}

/// <summary>
/// Port of UltJB_Points.sp (v1.3, RussianLightning): points for killing guards (double for the warden). The original
/// paid into the Swoobles UserPoints store plugin; that store is not part of this project, so points are kept in the
/// plugin's JSON storage and shown with !points. A store can use IPointsService later.
/// </summary>
public sealed class PointsModule : JbModule, IPointsService
{
    private const string Ns = "points";

    private PointsConfig Cfg => Section<PointsConfig>("Points");

    public override string Name => "Points";
    public override int Order => 40;
    public override bool Enabled => Cfg.Enabled;

    private bool _enabledThisRound;
    private int _deadWardenSlot = -1;

    public override void OnLoad(bool hotReload)
    {
        Jb.Points = this;
        JbEvents.WardenDeath += OnWardenDeath;
    }

    public override void OnUnload(bool hotReload) => JbEvents.WardenDeath -= OnWardenDeath;

    public override void OnRoundStart()
    {
        _enabledThisRound = Utilities.GetPlayers().Count(p => p.IsValid && !p.IsHLTV) >= Cfg.MinPlayers;
        _deadWardenSlot = -1;
    }

    private void OnWardenDeath(JbPlayer warden) => _deadWardenSlot = warden.Slot;

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (!_enabledThisRound || Jb.Days.IsInProgress || attacker == null || attacker.Slot == victim.Slot) return;
        if (victim.Controller?.Team != CsTeam.CounterTerrorist) return;
        if (victim.Slot == _deadWardenSlot)
        {
            _deadWardenSlot = -1;
            AddPoints(attacker, Cfg.WardenKill);
            Chat.Print(attacker, Lang.T("points.warden_kill", Cfg.WardenKill), false);
            return;
        }

        AddPoints(attacker, Cfg.GuardKill);
        Chat.Print(attacker, Lang.T("points.guard_kill", Cfg.GuardKill), false);
    }

    public int GetPoints(JbPlayer player) =>
        player.IsBot || player.SteamId64 == 0 ? 0 : Storage.Get<int>(Ns, player.SteamId64.ToString());

    public void AddPoints(JbPlayer player, int amount, string? reason = null)
    {
        if (player.IsBot) return;
        AddPoints(player.SteamId64, amount);
    }

    public void AddPoints(ulong steamId64, int amount)
    {
        if (steamId64 == 0 || amount == 0) return;
        var key = steamId64.ToString();
        Storage.Set(Ns, key, Math.Max(0, Storage.Get<int>(Ns, key) + amount));
    }

    [ConsoleCommand("css_points", "sm_points - show your jailbreak points")]
    public void CmdPoints(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        var p = caller.Jb();
        if (p != null) Chat.Tell(caller, "points.yours", GetPoints(p));
    }
}
