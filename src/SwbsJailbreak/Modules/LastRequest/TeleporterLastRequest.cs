using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>
/// Teleporter (Rebel): knife + Deagle and TeleporterHealth HP; pressing USE (E) teleports the prisoner to where they aim,
/// up to TeleporterRange units, every TeleporterCooldown seconds. The destination is a player-sized hull trace along the
/// view (CSS 1.0.376), so it is never inside a wall.
/// </summary>
public sealed class TeleporterLastRequest : JbModule
{
    private LrExtraConfig Cfg => Section<LrExtraConfig>("LastRequestsExtra");

    public override string Name => "LastRequests.Teleporter";
    public override int Order => 10;

    private readonly Dictionary<int, (float NextUse, bool UseHeld)> _users = new();

    private static readonly TraceOptions HullOptions = new() { InteractsWith = Masks.PlayerSolid };

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule || !Cfg.TeleporterEnabled) return;
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = "Teleporter", Category = "Rebel",
            Description = "Knife + Deagle; press E to teleport where you aim (cooldown).",
            Flags = LrFlags.Rebel | LrFlags.TempInvincible | LrFlags.NoRadar | LrFlags.RandomTeleportLocation,
            OnStart = p =>
            {
                Jb.LastRequests.SetHealth(p, Math.Max(1, Cfg.TeleporterHealth));
                Jb.LastRequests.StripWeapons(p, true);
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife_t");
                Jb.WeaponSelection.GiveWeapon(p, "weapon_deagle");
                _users[p.Slot] = (0f, true);
                Chat.Tell(p, "lr.teleporter.how", Cfg.TeleporterCooldown.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            },
            OnEnd = (p, _) =>
            {
                _users.Remove(p.Slot);
                Hud.Clear(p.Controller, "lr.teleporter");
                SimpleLastRequests.RestoreIfAlive(p);
            }
        });
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnPlayerDisconnect(JbPlayer player) => _users.Remove(player.Slot);

    private void OnTick()
    {
        if (_users.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var (slot, state) in _users.ToList())
        {
            var c = Players.Get(slot).Controller;
            if (c == null || !c.IsAliveP()) continue;
            var use = (c.Buttons & PlayerButtons.Use) != 0;
            var ready = now >= state.NextUse;
            if (!c.IsBot)
                Hud.Set(c, "lr.teleporter", ready ? Lang.T("lr.teleporter.ready") : Lang.T("lr.teleporter.cooldown", MathF.Ceiling(state.NextUse - now)), 20, 0.3f);
            if (use && !state.UseHeld && ready && Teleport(c))
            {
                _users[slot] = (now + Math.Max(0.5f, Cfg.TeleporterCooldown), true);
                continue;
            }

            _users[slot] = (state.NextUse, use);
        }
    }

    private bool Teleport(CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        if (pawn?.AbsOrigin == null) return false;
        var o = pawn.AbsOrigin;
        var fwd = VectorUtil.Forward(pawn.EyeAngles);
        var range = Math.Clamp(Cfg.TeleporterRange, 200f, 4000f);
        var start = new Vector(o.X, o.Y, o.Z + 2f);
        var end = new Vector(start.X + fwd.X * range, start.Y + fwd.Y * range, start.Z + fwd.Z * range);
        try
        {
            var t = Trace.TraceHullShape(start, end, new Vector(-16f, -16f, 0f), new Vector(16f, 16f, 72f), pawn, HullOptions);
            if (t.IsAllSolid || t.Fraction <= 0f) return false;
            var dest = t.EndPos;
            var dx = dest.X - o.X;
            var dy = dest.Y - o.Y;
            var dz = dest.Z - o.Z;
            if (dx * dx + dy * dy + dz * dz < 64f * 64f) return false;
            var from = VectorUtil.Copy(o);
            var to = new Vector(dest.X, dest.Y, dest.Z);
            pawn.Teleport(to, null, new Vector(0, 0, 0));
            Fx.PulseAt(from, Color.FromArgb(255, 80, 200, 255), 10f, 120f, 0.4f);
            Fx.PulseAt(to, Color.FromArgb(255, 80, 200, 255), 120f, 10f, 0.4f);
            if (Cfg.TeleporterSound.Length > 0) Sound.PlayAt(c, Cfg.TeleporterSound);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
