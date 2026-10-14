using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>Section "Admin". Defaults follow the SourceMod 1.6 cvars the SWBS server ran with.</summary>
public sealed class AdminConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>SourceMod sm_show_activity: 0 = silent, 1 = show action as "ADMIN", 2 = show admin name.</summary>
    public int ShowActivity { get; set; } = 2;

    public float BeaconRadius { get; set; } = 375f;          // sm_beacon_radius
    public int TimeBombTicks { get; set; } = 10;              // sm_timebomb_ticks
    public float TimeBombRadius { get; set; } = 600f;         // sm_timebomb_radius
    public int TimeBombMode { get; set; } = 0;                // sm_timebomb_mode 0 target, 1 team, 2 everyone
    public float BurnDuration { get; set; } = 20f;            // sm_burn_duration
    public int BurnDamagePerSecond { get; set; } = 4;
    public int FireBombTicks { get; set; } = 10;              // sm_firebomb_ticks
    public float FireBombRadius { get; set; } = 600f;         // sm_firebomb_radius
    public int FireBombMode { get; set; } = 0;                // sm_firebomb_mode
    public float FreezeDuration { get; set; } = 10f;          // sm_freeze_duration
    public int FreezeBombTicks { get; set; } = 10;            // sm_freezebomb_ticks
    public float FreezeBombRadius { get; set; } = 600f;       // sm_freezebomb_radius
    public int FreezeBombMode { get; set; } = 0;              // sm_freezebomb_mode

    /// <summary>Blind with the flashbang overlay instead of a black screen fade (use if fades do not render).</summary>
    public bool BlindUseFlash { get; set; } = false;

    // Sound events / particles (stock CS2 names; change here if a CS2 update renames them)
    public string BeaconSound { get; set; } = "UIPanorama.popup_accept_match_beep";
    public string BombBeepSound { get; set; } = "UIPanorama.popup_accept_match_beep";
    public string BombFinalSound { get; set; } = "c4.click";
    public string BombExplodeSound { get; set; } = "c4.explode";
    public string SlapSound { get; set; } = "Player.DamageBody.Onlooker";
    public string ExplosionParticle { get; set; } = "particles/explosions_fx/explosion_basic.vpcf";
    public string BurnParticle { get; set; } = "particles/burning_fx/burning_character_b.vpcf";

    /// <summary>Maps offered in the !admin "Change map" menu (workshop ids allowed, e.g. "workshop/3070212801").</summary>
    public List<string> MapList { get; set; } = new();

    /// <summary>Cvars that require the "rcon" capability to read/change with !cvar.</summary>
    public List<string> ProtectedCvars { get; set; } = new() { "rcon_password", "sv_password", "sv_setsteamaccount" };
}

/// <summary>Per-player admin effect state; everything is torn down on death and every round.</summary>
internal sealed class AdminFx : IRoundScoped
{
    public Timer? Beacon, TimeBomb, FireBomb, FreezeBomb, Freeze, Burn, Drug, Blind;
    public int BombTicks;
    public bool God;
    public bool Frozen;
    public bool Tinted;
    public int BlindAlpha;
    public CParticleSystem? BurnFx;

    public void ResetForRound()
    {
        Beacon = TimeBomb = FireBomb = FreezeBomb = Freeze = Burn = Drug = Blind = null;
        BombTicks = 0;
        God = false;
        Frozen = false;
        Tinted = false;
        BlindAlpha = 0;
        BurnFx = null;
    }

    public void KillAll()
    {
        foreach (var t in new[] { Beacon, TimeBomb, FireBomb, FreezeBomb, Freeze, Burn, Drug, Blind })
        {
            try { t?.Kill(); }
            catch { /* already stopped */ }
        }

        Fx.SafeRemove(BurnFx);
        ResetForRound();
    }
}

/// <summary>
/// Admin & fun commands: SourceMod 1.6 funcommands / playercommands / basecommands / basecomm / basebans / basechat,
/// the SMPlugins Respawn, Heal, Teamswitch, TimedMuteGag and LogAdminActivity plugins, and the commands from the
/// owner's SimpleAdmin plugin (bring/goto/hp/give/team/god/warn...). Every effect is a real CS2 mechanism.
/// </summary>
public sealed partial class AdminModule : JbModule
{
    internal AdminConfig Cfg => Section<AdminConfig>("Admin");

    public override string Name => "Admin";
    public override int Order => 90;
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        DamageService.Register(Name, 200, OnDamage);
        Plugin.AddCommandListener("say", OnSay);
        Plugin.AddCommandListener("say_team", OnSayTeam);
        Plugin.RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        Plugin.RegisterListener<Listeners.OnClientConnect>(OnClientConnect);
        Targets.RegisterMagic("@admins", "admins", c => Perms.IsAdmin(c));
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        foreach (var p in Players.Connected()) Fxs(p).KillAll();
    }

    internal static AdminFx Fxs(JbPlayer p) => p.Data<AdminFx>();

    public override void OnPlayerConnected(JbPlayer player) => ApplyStoredComms(player);

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        var fx = Fxs(victim);
        var c = victim.Controller;
        if (fx.Blind != null || fx.BlindAlpha > 0)
            if (c != null) Fx.ClearFade(c);
        if (fx.Drug != null && c != null) Fx.SetFov(c, 0);
        fx.KillAll();
        HandleTeamswitchDeath(victim);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        Fxs(player).KillAll();
        _switchOnDeath.Remove(player.Slot);
        _switchOnRoundEnd.Remove(player.Slot);
    }

    public override void OnPlayerSpawn(JbPlayer player)
    {
        // A pawn can be re-used across respawns in CS2: make sure nothing we applied survives.
        var c = player.Controller;
        if (c == null) return;
        var fx = Fxs(player);
        if (!fx.Frozen && c.IsFrozen()) c.Unfreeze();
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        foreach (var p in Players.Connected())
        {
            var fx = Fxs(p);
            var c = p.Controller;
            if (c != null)
            {
                if (fx.Frozen) c.Unfreeze();
                if (fx.Tinted) c.ResetColor();
                if (fx.BlindAlpha > 0) Fx.ClearFade(c);
                if (fx.Drug != null) Fx.SetFov(c, 0);
            }

            fx.KillAll();
        }

        Hud.ClearPrefix("admin.");
        HandleTeamswitchRoundEnd();
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        var p = Players.Get(ctx.Victim);
        return p != null && Fxs(p).God ? HookResult.Handled : HookResult.Continue;
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Permission gate with an informative denial (capability names from Perms.Defaults).</summary>
    internal bool Require(CCSPlayerController? caller, CommandInfo info, string capability)
    {
        if (Perms.Has(caller, capability)) return true;
        Chat.ReplyT(caller, info, "core.no_access");
        return false;
    }

    internal static string ArgsFrom(CommandInfo info, int index)
    {
        var parts = new List<string>();
        for (var i = index; i < info.ArgCount; i++) parts.Add(info.GetArg(i));
        return string.Join(' ', parts).Trim();
    }

    internal static string AdminName(CCSPlayerController? c) => c == null ? "Console" : c.PlayerName;

    /// <summary>SourceMod ShowActivity2 + LogAdminActivity: tell players and append to the admin activity log.</summary>
    internal void Activity(CCSPlayerController? admin, string actionKey, params object?[] args)
    {
        var action = Lang.T(actionKey, args);
        var who = Cfg.ShowActivity switch
        {
            0 => null,
            1 => Lang.T("admin.activity.anon"),
            _ => AdminName(admin)
        };
        if (who != null) Chat.PrintAll($"{{lightred}}{who}{{default}}: {action}");
        else if (admin != null) Chat.Print(admin, action);
        LogActivity(admin, Colors.Strip(action));
    }

    internal void LogActivity(CCSPlayerController? admin, string text)
    {
        Logger.LogInformation("[admin] {Admin} ({Steam}): {Text}", AdminName(admin), admin?.SteamID.ToString() ?? "console", text);
        // Persisted by the AdminActivity module (daily files, optional Discord) through the logger.
        Jb.Log.LogEvent(text, admin.Jb(), null, LogType.Admin);
    }

    /// <summary>Accepts SteamID64, STEAM_X:Y:Z and [U:1:N]. Returns 0 when invalid.</summary>
    internal static ulong ParseSteamId(string input)
    {
        input = input.Trim().Trim('"');
        if (ulong.TryParse(input, out var id64) && id64 > 76561197960265728UL) return id64;
        try
        {
            if (input.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input[6..].Split(':');
                if (parts.Length == 3 && ulong.TryParse(parts[1], out var y) && ulong.TryParse(parts[2], out var z))
                    return 76561197960265728UL + z * 2 + y;
            }

            if (input.StartsWith("[U:1:", StringComparison.OrdinalIgnoreCase) && input.EndsWith(']'))
                if (ulong.TryParse(input[5..^1], out var n)) return 76561197960265728UL + n;
        }
        catch
        {
            // invalid
        }

        return 0;
    }

    internal static string FormatDuration(int minutes) =>
        minutes <= 0 ? Lang.T("admin.time.permanent") : minutes < 60 ? Lang.T("admin.time.minutes", minutes)
            : minutes % 1440 == 0 ? Lang.T("admin.time.days", minutes / 1440) : Lang.T("admin.time.hours", Math.Round(minutes / 60.0, 1));

    internal static void Kick(CCSPlayerController target, string reason)
    {
        if (!target.IsValid) return;
        var userId = target.UserId;
        if (userId.HasValue)
            Server.ExecuteCommand($"kickid {userId.Value} \"{reason.Replace("\"", "'")}\"");
        else
            target.Disconnect(CounterStrikeSharp.API.ValveConstants.Protobuf.NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
    }

    internal static Color TeamColor(CCSPlayerController c) => c.Team switch
    {
        CsTeam.Terrorist => Color.FromArgb(255, 255, 40, 40),
        CsTeam.CounterTerrorist => Color.FromArgb(255, 60, 120, 255),
        _ => Color.FromArgb(255, 60, 255, 60)
    };

    // ------------------------------------------------------------------ chat hooks (gag + '@' admin chat)

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info) => HandleChat(player, info, false);

    private HookResult OnSayTeam(CCSPlayerController? player, CommandInfo info) => HandleChat(player, info, true);

    private HookResult HandleChat(CCSPlayerController? player, CommandInfo info, bool team)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        var p = Players.Get(player);
        var text = info.GetArg(1);
        if (p is { AdminChatGagged: true })
        {
            // Commands still work while gagged (SourceMod behaviour); plain chat is blocked.
            if (text.StartsWith('!') || text.StartsWith('/')) return HookResult.Continue;
            Chat.Tell(player, "admin.gag.blocked");
            return HookResult.Handled;
        }

        // basechat: "@message" in team chat goes to admins (anyone may send, admins receive).
        if (team && text.StartsWith('@') && text.Length > 1 && !text.StartsWith("@@"))
        {
            var msg = text[1..].Trim();
            if (msg.Length == 0) return HookResult.Handled;
            var line = Lang.T("admin.chat.to_admins", player.PlayerName, msg);
            foreach (var c in Players.Humans().Where(c => Perms.IsAdmin(c) || c.Slot == player.Slot))
                Chat.Print(c, line, false);
            LogActivity(player, "(to admins) " + msg);
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }
}
