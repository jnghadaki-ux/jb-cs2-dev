using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Zones;

/// <summary>ADDITION: tells guards (or everyone / admins) when a prisoner (or guard / anyone) enters, e.g. the armory or vents.</summary>
internal sealed class NotifyZone : ZoneType
{
    private readonly ZonesModule _m;
    private readonly Dictionary<(int Zone, int Slot), float> _next = new();

    public NotifyZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.Notify;
    public override string DisplayName => "Notify (area alert)";
    public override string Description => "Announces when a player enters this area. No punishment, information only.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Area name", ZoneFieldKind.Text, 1, "(shown in the alert, e.g. Armory)"),
        new ZoneField("Triggered by", ZoneFieldKind.Choice, 1) { Options = new[] { ("Prisoners", 0), ("Guards", 1), ("Everyone", 2) } },
        new ZoneField("Alert goes to", ZoneFieldKind.Choice, 2) { Options = new[] { ("Guards", 0), ("Everyone", 1), ("Admins", 2) } }
    };

    public override void OnRoundStart() => _next.Clear();

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        var who = zone.GetInt(1);
        if (who == 0 && player.Team != CsTeam.Terrorist) return;
        if (who == 1 && player.Team != CsTeam.CounterTerrorist) return;
        var now = Server.CurrentTime;
        if (_next.TryGetValue((zone.Id, player.Slot), out var t) && now < t) return;
        _next[(zone.Id, player.Slot)] = now + Math.Max(0f, _m.Cfg.NotifyCooldown);
        var area = zone.Name.Length > 0 ? zone.Name : zone.Title;
        var msg = Lang.T("zones.notify.entered", Chat.Name(player), area);
        foreach (var c in Players.Humans())
        {
            var send = zone.GetInt(2) switch
            {
                1 => true,
                2 => Perms.IsAdmin(c),
                _ => c.Team == CsTeam.CounterTerrorist
            };
            if (send) Chat.Print(c, msg);
        }
    }

    public override string Summary(Zone zone) => (zone.Name.Length > 0 ? zone.Name : "unnamed") + " alert";
}

/// <summary>ADDITION: marks a cell block (data string 1 = block name). Crews claim cell blocks as turf.</summary>
internal sealed class CellBlockZone : ZoneType
{
    public override string Key => ZoneTypeKeys.CellBlock;
    public override string DisplayName => "Cell Block";
    public override string Description => "A block of cells. Crews claim cell blocks as turf.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[] { new ZoneField("Block name", ZoneFieldKind.Text, 1, "(e.g. A, B, Upper)") };
    public override string Summary(Zone zone) => zone.Name.Length > 0 ? "block " + zone.Name : "unnamed block";
}
