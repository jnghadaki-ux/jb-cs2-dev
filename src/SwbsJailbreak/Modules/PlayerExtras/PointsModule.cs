using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
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
/// plugin's JSON storage and shown with !points; the store (!store) spends them. Admins with the "points" capability
/// (default @css/root) can change anyone's balance with !givepoints / !takepoints / !setpoints and see it with
/// !points &lt;target&gt;.
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

    [ConsoleCommand("css_points", "sm_points [target] - show your jailbreak points (admins: anyone's)")]
    public void CmdPoints(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount >= 2 && Perms.Has(caller, "points"))
        {
            var r = Targets.FindOrReply(caller, info, info.GetArg(1), respectImmunity: false);
            if (r == null) return;
            foreach (var t in r.Players.Where(t => !t.IsBot))
                if (t.Jb() is { } tp) Chat.ReplyT(caller, info, "points.of", t.PlayerName, GetPoints(tp));
            return;
        }

        if (caller == null) return;
        var p = caller.Jb();
        if (p != null) Chat.Tell(caller, "points.yours", GetPoints(p));
    }

    internal enum PointsOp { Give, Take, Set }

    /// <summary>Engine-free (unit-tested): the balance after an admin change; never below 0.</summary>
    internal static int AdminChange(int current, PointsOp op, int amount) => op switch
    {
        PointsOp.Give => (int)Math.Clamp((long)current + amount, 0, int.MaxValue),
        PointsOp.Take => (int)Math.Clamp((long)current - amount, 0, int.MaxValue),
        _ => Math.Max(0, amount)
    };

    [ConsoleCommand("css_givepoints", "sm_givepoints <target> <amount> - give jailbreak points")]
    public void CmdGivePoints(CCSPlayerController? caller, CommandInfo info) => AdminPoints(caller, info, PointsOp.Give, "css_givepoints");

    [ConsoleCommand("css_takepoints", "sm_takepoints <target> <amount> - take jailbreak points away")]
    public void CmdTakePoints(CCSPlayerController? caller, CommandInfo info) => AdminPoints(caller, info, PointsOp.Take, "css_takepoints");

    [ConsoleCommand("css_setpoints", "sm_setpoints <target> <amount> - set a player's jailbreak points")]
    public void CmdSetPoints(CCSPlayerController? caller, CommandInfo info) => AdminPoints(caller, info, PointsOp.Set, "css_setpoints");

    private void AdminPoints(CCSPlayerController? caller, CommandInfo info, PointsOp op, string command)
    {
        if (!Perms.Has(caller, "points")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var amount) || amount < 0)
        {
            Chat.ReplyT(caller, info, "core.usage", $"{command} <#steamid|#userid|name|@me|@all> <amount>");
            return;
        }

        var r = Targets.FindOrReply(caller, info, info.GetArg(1), respectImmunity: false);
        if (r == null) return;
        var admin = caller?.PlayerName ?? "Console";
        foreach (var t in r.Players.Where(t => !t.IsBot))
        {
            var tp = t.Jb();
            if (tp == null || tp.SteamId64 == 0) continue;
            var before = GetPoints(tp);
            var after = AdminChange(before, op, amount);
            Storage.Set(Ns, tp.SteamId64.ToString(), after);
            Chat.ReplyT(caller, info, "points.admin_changed", t.PlayerName, before, after);
            if (caller == null || t.Slot != caller.Slot) Chat.Tell(t, "points.admin_changed_you", admin, after);
            Logger.LogInformation("\"{Admin}\" changed {Target}'s points {Before} -> {After}", admin, t.PlayerName, before, after);
            Jb.Log.LogEvent($"{admin} changed {t.PlayerName}'s points {before} -> {after}", caller?.Jb(), tp, LogType.Admin);
        }
    }
}
