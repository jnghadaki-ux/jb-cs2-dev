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
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeathPre, HookMode.Pre);
        Plugin.AddTimer(10f, EnforceTags, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        if (hotReload) HookTextMsg();
    }

    public override void OnMapStart(string mapName) => Server.NextFrame(HookTextMsg);

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
