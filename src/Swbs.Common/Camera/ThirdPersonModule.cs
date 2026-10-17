using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Swbs.Common;

/// <summary>
/// !3p / !thirdperson / !camera: the <see cref="ThirdPerson"/> follow camera on every SWBS server. Who may use the command
/// depends on the mode (CommonHost.ThirdPersonCommandDefault: everyone on the movement servers, admins on jailbreak, to
/// look at models, hats and effects); styles such as Bhop's Third Person use the camera directly either way.
/// </summary>
public sealed class ThirdPersonModule : SwbsModule
{
    internal ThirdPersonConfig Cfg => Section<ThirdPersonConfig>("ThirdPerson");

    public override string Name => "ThirdPerson";
    public override int Order => 6;
    public override bool Enabled => Cfg.Enabled;

    private string Access => (Cfg.Command.Length > 0 ? Cfg.Command : CommonHost.ThirdPersonCommandDefault).Trim().ToLowerInvariant();

    public override void OnLoad(bool hotReload)
    {
        ThirdPerson.Settings = Cfg;
        Plugin.RegisterListener<Listeners.OnTick>(ThirdPerson.Tick);
        Plugin.RegisterListener<Listeners.OnServerPreEntityThink>(ThirdPerson.PreThink);
        Plugin.RegisterListener<Listeners.OnServerPostEntityThink>(ThirdPerson.PostThink);
        Plugin.RegisterListener<Listeners.CheckTransmit>(ThirdPerson.FilterTransmit);
    }

    public override void OnUnload(bool hotReload) => ThirdPerson.RemoveAll();

    public override void OnMapEnd() => ThirdPerson.Forget();

    public override void OnPlayerDisconnect(SwbsPlayer player) => ThirdPerson.Clear(player.Slot);

    public override void OnPlayerDeath(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (victim.Controller is { } c) ThirdPerson.Update(c);
    }

    // Respawned players get their camera back (the pawn and its camera services are new).
    public override void OnPlayerSpawn(SwbsPlayer player)
    {
        var slot = player.Slot;
        Server.NextFrame(() => { if (Players.Get(slot).Controller is { IsValid: true } c) ThirdPerson.Update(c); });
    }

    private bool MayUse(CCSPlayerController c) => Access switch
    {
        "everyone" or "all" => true,
        "admin" or "admins" => Perms.Has(c, "admin"),
        _ => false
    };

    [ConsoleCommand("css_3p", "sm_3p - third person camera on or off")]
    [ConsoleCommand("css_thirdperson", "sm_thirdperson - third person camera on or off")]
    [ConsoleCommand("css_camera", "sm_camera - third person camera on or off")]
    public void CmdThirdPerson(CCSPlayerController? c, CommandInfo info)
    {
        if (c == null || !c.IsValid || c.IsBot) return;
        var on = ThirdPerson.Has(c, "command");
        if (!on && (!MayUse(c) || !ThirdPerson.Allowed(c)))
        {
            Chat.Tell(c, Access is "admin" or "admins" && !Perms.Has(c, "admin") ? "tp.admins_only" : "tp.not_now");
            return;
        }

        if (!on && !c.IsAliveP())
        {
            Chat.Tell(c, "tp.dead");
            return;
        }

        ThirdPerson.Set(c, "command", !on);
        Chat.Tell(c, ThirdPerson.IsOn(c) ? "tp.on" : "tp.off");
    }
}
