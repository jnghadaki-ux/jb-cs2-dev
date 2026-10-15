using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "ServerTweaks": small SMPlugins server plugins that were loaded on the jailbreak server.</summary>
public sealed class ServerTweaksConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>hlstriker_noblock (sm_enable_noblock 1): players pass through each other.</summary>
    public bool NoBlock { get; set; } = true;

    /// <summary>GrenadeNoblock: thrown grenades do not collide with players.</summary>
    public bool GrenadeNoBlock { get; set; } = true;

    /// <summary>AdminTags: admins wear this clan tag (toggle with !admintag).</summary>
    public bool AdminTags { get; set; } = true;
    public string AdminTag { get; set; } = "Swbs! Admin";

    /// <summary>ClanTagFilter: clan tags containing any of these strings are removed (configs/swoobles/clan_tag_blacklist.txt).</summary>
    public List<string> ClanTagBlacklist { get; set; } = new();

    /// <summary>BlockSpamMessages: TextMsg tokens (prefix match) that are never shown.</summary>
    public bool BlockSpamMessages { get; set; } = true;
    public List<string> BlockedTextMessages { get; set; } = new() { "#Item_Traded", "#Chat_SavePlayer", "#Player_Cash_Award_ExplainSuicide" };

    /// <summary>DisableKillFeed (not every SWBS server used it): hide the kill feed.</summary>
    public bool DisableKillFeed { get; set; } = false;

    /// <summary>
    /// Enforce <see cref="MovementCvars"/> at map start and every round start (game mode configs and server.cfg can
    /// reset them on map load). Stock CS2 has sv_airaccelerate 12 and stamina, which makes surf impossible and manual
    /// bhop lose speed on every hop.
    /// </summary>
    public bool ForceMovementCvars { get; set; } = true;

    /// <summary>
    /// Surf / bhop movement (values used by CS2 surf and bhop servers). CS2 always runs at 64 tick with sub-tick input:
    /// there is no 128 tick, these values are what makes movement feel like the old 128 tick servers.
    /// sv_autobunnyhopping is left out on purpose: the Auto Bhop day turns it on and off itself.
    /// </summary>
    public Dictionary<string, string> MovementCvars { get; set; } = DefaultMovementCvars();

    /// <summary>
    /// SMPlugins BhopCap, reworked for CS2: bunny hopping is on, but a jump taken straight off a landing cannot carry more
    /// horizontal speed than BhopCapSpeed (the rest is scaled away on the jump tick). Only that tick is touched, so
    /// surf ramps, air strafing and falling keep their speed. Not applied while auto bhop is on (Auto Bhop day / LR).
    /// </summary>
    public bool BhopCap { get; set; } = true;

    /// <summary>Maximum horizontal speed (units/s) a bunny hop leaves the ground with. Knife running speed is 250.</summary>
    public float BhopCapSpeed { get; set; } = 400f;

    /// <summary>
    /// Other server cvars the jailbreak needs, enforced like MovementCvars. mp_autokick 0: CS2 counts the plugin's
    /// credited kills (throwing knives, rockets, admin slay - the victim is slain and the kill feed rewritten) as
    /// suicides and auto-kicked players "for suiciding too many times".
    /// </summary>
    public Dictionary<string, string> ServerCvars { get; set; } = new() { ["mp_autokick"] = "0" };

    internal static Dictionary<string, string> DefaultMovementCvars() => new()
    {
        ["sv_airaccelerate"] = "150",          // surf needs 100+; strafing in the air keeps speed
        ["sv_enablebunnyhopping"] = "1",       // no speed cap on landing
        ["sv_staminamax"] = "0",               // no stamina: consecutive jumps keep full height / speed
        ["sv_staminajumpcost"] = "0",
        ["sv_staminalandcost"] = "0",
        ["sv_staminarecoveryrate"] = "0",
        ["sv_maxvelocity"] = "3500",           // surf ramps go past the default cap
        ["sv_air_max_wishspeed"] = "30",
        ["sv_accelerate_use_weapon_speed"] = "0" // knife-out and rifle air strafes accelerate the same
    };
}

internal sealed class TagState
{
    public bool HideAdminTag;
    public float NextToggle;
}

/// <summary>NoBlock, GrenadeNoblock, AdminTags, ClanTagFilter, BlockSpamMessages, DisableKillFeed.</summary>
public sealed class ServerTweaksModule : JbModule
{
    private ServerTweaksConfig Cfg => Section<ServerTweaksConfig>("ServerTweaks");

    public override string Name => "ServerTweaks";
    public override int Order => 110;
    public override bool Enabled => Cfg.Enabled;

    private bool _textMsgHooked;

    public override void OnLoad(bool hotReload)
    {
        Plugin.RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        Plugin.RegisterListener<Listeners.OnTick>(BhopCapTick);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
        Plugin.AddTimer(10f, EnforceTags, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        if (hotReload)
        {
            HookTextMsg();
            Server.NextFrame(ApplyMovementCvars);
        }
    }

    public override void OnMapStart(string mapName)
    {
        Server.NextFrame(HookTextMsg);
        Server.NextFrame(ApplyMovementCvars);
    }

    public override void OnRoundStart() => ApplyMovementCvars();

    // ------------------------------------------------------------------ bhop cap

    private readonly bool[] _wasOnGround = new bool[Players.MaxSlots];

    /// <summary>Engine-free: the horizontal velocity after the cap (unchanged when at or under it).</summary>
    internal static (float X, float Y) CapHorizontal(float x, float y, float cap)
    {
        var speed = MathF.Sqrt(x * x + y * y);
        if (cap <= 0 || speed <= cap) return (x, y);
        var k = cap / speed;
        return (x * k, y * k);
    }

    private void BhopCapTick()
    {
        if (!Cfg.BhopCap) return;
        var auto = Cvars.Get("sv_autobunnyhopping");
        var autoOn = auto is "1" or "true";
        foreach (var c in Players.All())
        {
            var slot = c.Slot;
            if (slot is < 0 or >= Players.MaxSlots) continue;
            var pawn = c.PawnOrNull();
            if (pawn == null || !c.IsAliveP())
            {
                _wasOnGround[slot] = false;
                continue;
            }

            var onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
            var was = _wasOnGround[slot];
            _wasOnGround[slot] = onGround;
            if (!was || onGround || autoOn || pawn.MoveType != MoveType_t.MOVETYPE_WALK) continue;
            var v = pawn.AbsVelocity;
            if (v == null || v.Z < 200f) continue; // left the ground without jumping (ledge, push, ramp)
            if (Movement.HasAutoBhop(c)) continue;
            var (x, y) = CapHorizontal(v.X, v.Y, Cfg.BhopCapSpeed);
            if (x == v.X && y == v.Y) continue;
            pawn.Teleport(null, null, new Vector(x, y, v.Z));
        }
    }

    private void ApplyMovementCvars()
    {
        var changed = new List<string>();
        if (Cfg.ForceMovementCvars) ApplyCvars(Cfg.MovementCvars, changed);
        ApplyCvars(Cfg.ServerCvars, changed);
        // Logged so an owner can see in the server console that surf / bhop settings really are in effect.
        if (changed.Count > 0) Logger.LogInformation("[ServerTweaks] cvars set: {Cvars}", string.Join(", ", changed));
    }

    private static void ApplyCvars(Dictionary<string, string> cvars, List<string> changed)
    {
        foreach (var (rawName, rawValue) in cvars)
        {
            if (string.IsNullOrWhiteSpace(rawName) || rawValue == null) continue;
            var name = rawName.Trim();
            var value = rawValue.Trim();
            var cur = Cvars.Get(name);
            if (cur == value || cur != null && float.TryParse(cur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && Math.Abs(a - b) < 0.0001f) continue;
            Cvars.Set(name, value);
            changed.Add($"{name} {value}");
        }
    }

    // ------------------------------------------------------------------ noblock

    public override void OnPlayerSpawn(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null) return;
        if (Cfg.NoBlock) SetCollision(c.PawnOrNull(), CollisionGroup.COLLISION_GROUP_DISSOLVING);
        ApplyTag(player);
    }

    private static void SetCollision(CBaseEntity? ent, CollisionGroup group)
    {
        if (ent == null || !ent.IsValid) return;
        var col = ent.Collision;
        if (col == null) return;
        col.CollisionGroup = (byte)group;
        col.CollisionAttribute.CollisionGroup = (byte)group;
        Utilities.SetStateChanged(ent, "CCollisionProperty", "m_CollisionGroup");
        Utilities.SetStateChanged(ent, "CCollisionProperty", "m_collisionAttribute");
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!Cfg.GrenadeNoBlock) return;
        var name = entity.DesignerName;
        if (!name.EndsWith("_projectile", StringComparison.Ordinal)) return;
        if (name is not ("hegrenade_projectile" or "smokegrenade_projectile" or "incgrenade_projectile" or "decoy_projectile"
            or "molotov_projectile" or "flashbang_projectile")) return;
        var ent = entity.As<CBaseEntity>();
        Server.NextFrame(() =>
        {
            if (ent.IsValid) SetCollision(ent, CollisionGroup.COLLISION_GROUP_DEBRIS);
        });
    }

    // ------------------------------------------------------------------ admin tags + clan tag filter

    [ConsoleCommand("css_admintag", "sm_admintag - toggle your admin clan tag")]
    public void CmdAdminTag(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Perms.Has(caller, "admin")) { Chat.Tell(caller, "core.no_access"); return; }
        var p = caller.Jb()!;
        var s = p.Data<TagState>();
        var now = (float)Server.EngineTime;
        if (now < s.NextToggle) { Chat.Tell(caller, "tweaks.admintag.wait"); return; }
        s.NextToggle = now + 0.7f;
        s.HideAdminTag = !s.HideAdminTag;
        if (p.SteamId64 != 0) Storage.Set("admintag_hidden", p.SteamId64.ToString(), s.HideAdminTag);
        if (s.HideAdminTag)
        {
            caller.SetClanTag("");
            Chat.Tell(caller, "tweaks.admintag.hidden");
        }
        else
        {
            ApplyTag(p);
            Chat.Tell(caller, "tweaks.admintag.shown");
        }
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        if (player.SteamId64 != 0)
            player.Data<TagState>().HideAdminTag = Storage.Get<bool>("admintag_hidden", player.SteamId64.ToString());
    }

    private void ApplyTag(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || c.IsBot) return;
        if (Cfg.AdminTags && Perms.IsAdmin(c) && !p.Data<TagState>().HideAdminTag)
        {
            if (c.Clan != Cfg.AdminTag) c.SetClanTag(Cfg.AdminTag);
            return;
        }

        var tag = c.Clan ?? "";
        if (tag.Length > 0 && Cfg.ClanTagBlacklist.Any(b => b.Length >= 3 && tag.Contains(b, StringComparison.OrdinalIgnoreCase)))
            c.SetClanTag("");
    }

    private void EnforceTags()
    {
        foreach (var p in Players.Connected()) ApplyTag(p);
    }

    // ------------------------------------------------------------------ spam messages / kill feed

    private void HookTextMsg()
    {
        if (_textMsgHooked || !Cfg.BlockSpamMessages) return;
        try
        {
            var id = UserMessage.FindIdByName("CUserMessageTextMsg");
            if (id <= 0) return;
            Plugin.HookUserMessage(id, OnTextMsg, HookMode.Pre);
            _textMsgHooked = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ServerTweaks: could not hook TextMsg - spam message blocking disabled");
        }
    }

    private HookResult OnTextMsg(UserMessage msg)
    {
        try
        {
            if (msg.GetRepeatedFieldCount("param") == 0) return HookResult.Continue;
            var token = msg.ReadString("param", 0);
            foreach (var blocked in Cfg.BlockedTextMessages)
                if (token.StartsWith(blocked, StringComparison.Ordinal))
                    return HookResult.Stop;
        }
        catch
        {
            // field layout changed - never break chat
        }

        return HookResult.Continue;
    }

    private HookResult OnDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        if (Cfg.DisableKillFeed) info.DontBroadcast = true;
        return HookResult.Continue;
    }
}
