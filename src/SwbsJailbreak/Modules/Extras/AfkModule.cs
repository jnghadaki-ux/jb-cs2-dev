using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "AFK" (SMPlugins AFKManager.sp v1.7).</summary>
public sealed class AfkConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>sm_seconds_before_afk: seconds without input before a player is away.</summary>
    public int SecondsBeforeAfk { get; set; } = 120;

    /// <summary>TRANSFER_WAIT_TIME: warning seconds before an away player is moved.</summary>
    public int WarningSeconds { get; set; } = 15;

    /// <summary>sm_num_free_slots_before_kicking: an away player is kicked instead of moved when this few slots are free.</summary>
    public int FreeSlotsBeforeKicking { get; set; } = 3;

    /// <summary>Admins (capability "admin") are moved to spectators but never kicked.</summary>
    public bool AdminsNeverKicked { get; set; } = true;
}

/// <summary>
/// Port of AFKManager.sp: a player with no input for SecondsBeforeAfk is away. Alive players are warned and, after
/// WarningSeconds, moved to spectators (dead players wait until they respawn, like the original). An away player is
/// kicked instead when the server is near full. Any input (buttons, looking around, moving, chat) marks them back.
/// </summary>
public sealed class AfkModule : JbModule
{
    private AfkConfig Cfg => Section<AfkConfig>("AFK");

    public override string Name => "AFK";
    public override int Order => 115;
    public override bool Enabled => Cfg.Enabled;

    private sealed class State
    {
        public float LastActive;
        public PlayerButtons LastButtons;
        public float LastYaw, LastPitch;
        public Vector? LastPos;
        public float WarnedAt = -1;
        public bool Away;
        public bool Sampled;
    }

    private readonly State?[] _states = new State?[Players.MaxSlots];

    public override void OnLoad(bool hotReload)
    {
        // AFKManager checked every 3 s (Timer_CheckForAway).
        Plugin.AddTimer(3f, Check, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        Plugin.AddCommandListener("say", OnChat);
        Plugin.AddCommandListener("say_team", OnChat);
    }

    public override void OnPlayerConnected(JbPlayer player) => _states[player.Slot] = new State { LastActive = Now };

    public override void OnPlayerDisconnect(JbPlayer player) => _states[player.Slot] = null;

    private static float Now => (float)Server.EngineTime;

    private HookResult OnChat(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null && player.IsValid) MarkActive(player.Slot);
        return HookResult.Continue;
    }

    private void MarkActive(int slot)
    {
        var s = _states[slot];
        if (s == null) return;
        s.LastActive = Now;
        if (s.WarnedAt >= 0 || s.Away)
        {
            s.WarnedAt = -1;
            s.Away = false;
            var c = Players.Get(slot).Controller;
            if (c != null) Hud.Clear(c, "afk");
        }
    }

    /// <summary>Engine-free (unit-tested): should the away player be kicked rather than moved?</summary>
    internal static bool ShouldKick(int maxPlayers, int connected, int freeSlotsBeforeKicking) =>
        maxPlayers - connected <= Math.Max(0, freeSlotsBeforeKicking);

    private void Check()
    {
        var now = Now;
        foreach (var c in Players.Humans().ToList())
        {
            var s = _states[c.Slot] ??= new State { LastActive = now };
            if (SawInput(c, s)) MarkActive(c.Slot);

            if (now - s.LastActive < Math.Max(30, Cfg.SecondsBeforeAfk)) continue;

            var playing = c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;
            if (playing && !c.IsAliveP()) continue; // OnAway: dead players are handled once they respawn

            if (s.WarnedAt < 0)
            {
                s.WarnedAt = now;
                s.Away = true;
                if (playing)
                {
                    Chat.Tell(c, "afk.warning", Cfg.WarningSeconds);
                    Hud.Set(c, "afk", Lang.T("afk.hud", Cfg.WarningSeconds), 95, Cfg.WarningSeconds);
                }

                continue;
            }

            if (now - s.WarnedAt < Math.Max(5, Cfg.WarningSeconds)) continue;
            Transfer(c, s, playing);
        }
    }

    private static bool SawInput(CCSPlayerController c, State s)
    {
        try
        {
            var pawn = c.PawnOrNull();
            var buttons = pawn != null ? c.Buttons : 0;
            var angles = pawn?.EyeAngles;
            var pos = pawn?.AbsOrigin;
            var active = false;
            if (s.Sampled)
            {
                if (buttons != 0 && buttons != s.LastButtons) active = true;
                if (angles != null && (MathF.Abs(angles.Y - s.LastYaw) > 1f || MathF.Abs(angles.X - s.LastPitch) > 1f)) active = true;
                if (pos != null && s.LastPos != null && VectorUtil.Distance(pos, s.LastPos) > 32f && c.IsAliveP()) active = true;
            }

            s.Sampled = true;
            s.LastButtons = buttons;
            if (angles != null) { s.LastYaw = angles.Y; s.LastPitch = angles.X; }
            s.LastPos = pos != null ? VectorUtil.Copy(pos) : null;
            return active;
        }
        catch
        {
            return false;
        }
    }

    private void Transfer(CCSPlayerController c, State s, bool playing)
    {
        s.WarnedAt = -1;
        s.LastActive = Now; // the next check starts a fresh period
        var connected = Players.All().Count();
        var kickable = !(Cfg.AdminsNeverKicked && Perms.IsAdmin(c));
        if (kickable && ShouldKick(Server.MaxPlayers, connected, Cfg.FreeSlotsBeforeKicking))
        {
            Jb.Log.LogEvent($"{c.PlayerName} was kicked for being AFK (server near full)", c.Jb(), null, LogType.Admin);
            Server.ExecuteCommand($"kickid {c.UserId} \"AFK when the server is near full\"");
            return;
        }

        if (!playing) return;
        if (c.IsAliveP()) c.CommitSuicide(false, true);
        c.ChangeTeam(CsTeam.Spectator);
        Hud.Clear(c, "afk");
        Chat.Tell(c, "afk.moved");
        Chat.TellAll("afk.moved_all", c.PlayerName);
    }
}
