using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>One glowing outline that follows a player.</summary>
public sealed class GlowHandle
{
    internal CBaseModelEntity? Relay;
    internal CBaseModelEntity? Outline;

    public int OwnerSlot { get; internal set; }

    /// <summary>Who may see this outline; null = everyone.</summary>
    public Func<CCSPlayerController, bool>? VisibleTo { get; set; }

    public bool IsValid => Outline is { IsValid: true } && Relay is { IsValid: true };
}

/// <summary>
/// Player outlines through walls (replacement for the CS:GO prop_dynamic_override + m_bShouldGlow technique used by
/// UltJB X-ray). CS2 technique: an invisible prop_dynamic "relay" follows the pawn, a second prop_dynamic with the same
/// model follows the relay and carries CGlowProperty (type 3 = always visible through walls). Visibility per viewer is
/// enforced with CheckTransmit (entities are simply not sent to players who must not see them).
/// </summary>
public static class Glow
{
    private static readonly List<GlowHandle> Active = new();

    public static int Count => Active.Count;

    public static GlowHandle? Attach(CCSPlayerController target, Color color, Func<CCSPlayerController, bool>? visibleTo = null)
    {
        var pawn = target.PawnOrNull();
        if (pawn == null || !target.IsAliveP()) return null;
        try
        {
            var model = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
            if (string.IsNullOrEmpty(model)) return null;

            var relay = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            var outline = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (relay == null || outline == null)
            {
                Fx.SafeRemove(relay);
                Fx.SafeRemove(outline);
                return null;
            }

            relay.SetModel(model);
            relay.Spawnflags = 256; // start with collision disabled
            relay.RenderMode = RenderMode_t.kRenderNone;
            relay.DispatchSpawn();

            outline.SetModel(model);
            outline.Spawnflags = 256;
            outline.DispatchSpawn();
            outline.Glow.GlowColorOverride = color;
            outline.Glow.GlowRange = 5000;
            outline.Glow.GlowRangeMin = 0;
            outline.Glow.GlowTeam = -1;
            outline.Glow.GlowType = 3;

            relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
            outline.AcceptInput("FollowEntity", relay, outline, "!activator");

            var handle = new GlowHandle { Relay = relay, Outline = outline, OwnerSlot = target.Slot, VisibleTo = visibleTo };
            Active.Add(handle);
            return handle;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Failed to create glow");
            return null;
        }
    }

    public static void Remove(GlowHandle? handle)
    {
        if (handle == null) return;
        Active.Remove(handle);
        Fx.SafeRemove(handle.Outline);
        Fx.SafeRemove(handle.Relay);
        handle.Outline = null;
        handle.Relay = null;
    }

    public static void RemoveAll()
    {
        foreach (var h in Active.ToList()) Remove(h);
    }

    /// <summary>CheckTransmit listener (registered by the core): hide outlines from players who may not see them.</summary>
    internal static void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (Active.Count == 0) return;
        Active.RemoveAll(h => !h.IsValid);
        foreach (var (info, player) in infoList)
        {
            if (player == null || !player.IsValid) continue;
            foreach (var h in Active)
            {
                var hide = h.OwnerSlot == player.Slot || (h.VisibleTo != null && !h.VisibleTo(player));
                if (!hide) continue;
                info.TransmitEntities.Remove(h.Outline!);
                info.TransmitEntities.Remove(h.Relay!);
            }
        }
    }
}
