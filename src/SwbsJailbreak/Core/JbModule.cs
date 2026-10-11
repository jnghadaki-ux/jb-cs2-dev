using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>
/// Base class for every jailbreak subsystem. The SMPlugins original shipped each subsystem as its own
/// SourceMod plugin talking through natives/forwards; here each one is a module inside a single CSS plugin
/// and talks to the others through <see cref="Jb"/> services and <see cref="JbEvents"/>.
///
/// Modules may also use CSS attributes ([GameEventHandler], [ConsoleCommand], [RequiresPermissions],
/// [CommandHelper], [EntityOutputHook]) on public methods - the plugin registers them with RegisterAllAttributes.
/// </summary>
public abstract class JbModule
{
    public JailbreakPlugin Plugin { get; internal set; } = null!;

    public ILogger Logger => Plugin.Logger;

    public JailbreakConfig Config => Plugin.Config;

    /// <summary>Get this module's config section (created with defaults in SwbsJailbreak.json on first run).</summary>
    protected T Section<T>(string name) where T : class, new() => Plugin.Config.Section<T>(name);

    /// <summary>Display name used in logs.</summary>
    public virtual string Name => GetType().Name;

    /// <summary>Lower loads first. Core services use 0-9, gameplay 10-99, extras 100+.</summary>
    public virtual int Order => 100;

    /// <summary>When false the module is skipped entirely (no attributes registered, no callbacks).</summary>
    public virtual bool Enabled => true;

    public virtual void OnLoad(bool hotReload) { }
    public virtual void OnUnload(bool hotReload) { }

    /// <summary>Called once every module has run OnLoad. Safe place to subscribe to other modules' events.</summary>
    public virtual void OnAllModulesLoaded(bool hotReload) { }

    public virtual void OnMapStart(string mapName) { }
    public virtual void OnMapEnd() { }

    /// <summary>round_start (post). Round-scoped JbPlayer state was already reset.</summary>
    public virtual void OnRoundStart() { }

    /// <summary>round_freeze_end (post) - freeze time is over, players can move.</summary>
    public virtual void OnRoundFreezeEnd() { }

    /// <summary>round_end (pre-dispatch order, called from the post hook).</summary>
    public virtual void OnRoundEnd(CsTeam winner, int reason) { }

    /// <summary>Player fully in server (OnClientPutInServer), JbPlayer freshly reset.</summary>
    public virtual void OnPlayerConnected(JbPlayer player) { }

    /// <summary>Player is leaving. Controller may already be invalid - use JbPlayer cached data.</summary>
    public virtual void OnPlayerDisconnect(JbPlayer player) { }

    /// <summary>player_spawn, delivered one frame later so the pawn is valid. Only for alive T/CT.</summary>
    public virtual void OnPlayerSpawn(JbPlayer player) { }

    /// <summary>player_death (post).</summary>
    public virtual void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev) { }

    /// <summary>player_team (post). oldTeam/newTeam as CsTeam.</summary>
    public virtual void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect) { }
}
