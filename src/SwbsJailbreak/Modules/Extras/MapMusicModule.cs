using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.UserMessages;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "MapMusic" (SMPlugins StopMapMusic.sp).</summary>
public sealed class MapMusicConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Map entities whose sounds count as map music / ambience. CS2 maps play them through these classes.</summary>
    public List<string> SoundEntityClasses { get; set; } = new()
    {
        "point_soundevent", "snd_event_point", "snd_event_alignedbox", "snd_event_orientedbox", "snd_event_sphere",
        "snd_event_path_corner", "ambient_generic"
    };
}

/// <summary>
/// Port of StopMapMusic.sp (!music / !stopmusic). The original offered a volume / pitch menu for map ambient_generic
/// music; CS2 cannot change one sound's volume for a single player from the server, so this is an on/off switch per
/// player, remembered in data/prefs.json. Sounds started by map sound entities are not sent to players who switched
/// them off, and turning music off also stops the map sounds already playing for that player.
/// </summary>
public sealed class MapMusicModule : JbModule
{
    private const string PrefsNs = "prefs";

    private MapMusicConfig Cfg => Section<MapMusicConfig>("MapMusic");

    public override string Name => "MapMusic";
    public override int Order => 116;
    public override bool Enabled => Cfg.Enabled;

    private readonly bool[] _muted = new bool[Players.MaxSlots];

    /// <summary>Recently started map sounds (guid), so they can be stopped for a player who turns music off.</summary>
    private readonly LinkedList<int> _recentGuids = new();

    private int _startId = -1;
    private int _stopId = -1;
    private HashSet<string> _classes = new(StringComparer.OrdinalIgnoreCase);

    public override void OnLoad(bool hotReload)
    {
        _classes = new HashSet<string>(Cfg.SoundEntityClasses, StringComparer.OrdinalIgnoreCase);
        try
        {
            _startId = UserMessage.FindIdByName("CMsgSosStartSoundEvent");
            _stopId = UserMessage.FindIdByName("CMsgSosStopSoundEvent");
            if (_startId > 0) Plugin.HookUserMessage(_startId, OnStartSound, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MapMusic] sound event messages unavailable - !music only remembers the setting");
        }
    }

    public override void OnMapStart(string mapName) => _recentGuids.Clear();

    public override void OnPlayerConnected(JbPlayer player) =>
        _muted[player.Slot] = !player.IsBot && player.SteamId64 != 0 && Storage.Get<bool>(PrefsNs, Key(player.SteamId64));

    public override void OnPlayerDisconnect(JbPlayer player) => _muted[player.Slot] = false;

    private static string Key(ulong steamId) => steamId + ".map_music_off";

    private HookResult OnStartSound(UserMessage msg)
    {
        try
        {
            var idx = msg.ReadInt("source_entity_index");
            if (idx <= 0) return HookResult.Continue;
            var ent = Utilities.GetEntityFromIndex<CBaseEntity>(idx);
            if (ent == null || !ent.IsValid || !_classes.Contains(ent.DesignerName)) return HookResult.Continue;

            var guid = msg.ReadInt("soundevent_guid");
            if (guid != 0)
            {
                _recentGuids.AddLast(guid);
                while (_recentGuids.Count > 64) _recentGuids.RemoveFirst();
            }

            foreach (var c in msg.Recipients.ToList())
                if (c.IsValid && _muted[c.Slot])
                    msg.Recipients.Remove(c);
        }
        catch
        {
            // message layout changed - never break sounds
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_music", "sm_music - turn map music on or off for you")]
    [ConsoleCommand("css_stopmusic", "sm_stopmusic - turn map music on or off for you")]
    public void CmdMusic(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || caller.IsBot) return;
        var off = !_muted[caller.Slot];
        _muted[caller.Slot] = off;
        if (caller.SteamID != 0)
        {
            if (off) Storage.Set(PrefsNs, Key(caller.SteamID), true);
            else Storage.Delete(PrefsNs, Key(caller.SteamID));
        }

        if (off) StopPlaying(caller);
        Chat.Tell(caller, off ? "music.off" : "music.on");
    }

    /// <summary>Stop the map sounds already playing for this player.</summary>
    private void StopPlaying(CCSPlayerController c)
    {
        if (_stopId <= 0) return;
        foreach (var guid in _recentGuids)
        {
            try
            {
                using var stop = UserMessage.FromId(_stopId);
                stop.SetInt("soundevent_guid", guid);
                stop.Recipients.Add(c);
                stop.Send();
            }
            catch
            {
                return;
            }
        }
    }
}
