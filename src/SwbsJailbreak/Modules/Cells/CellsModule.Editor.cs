using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Cells;

internal sealed class CellEditSession
{
    public int Slot;
    public uint LookingAt;
    public JbMenu? Menu;
    public readonly List<CBeam> Box = new();
    public int CycleIndex = -1;
    public bool ConfirmClear;
}

/// <summary>
/// Commands (sm_celldoors_open / sm_cdo / sm_celldoors_edit) and the in-game cell door editor (spec 02 §1.2.10-11).
/// Look-at detection uses the real CSS ray trace on the net10.0 build; the net8.0 build (no trace API) picks the
/// door-type entity closest to the admin's crosshair direction from real entity bounds, and the console commands
/// css_celldoors_list / _add / _remove / _save work on both builds.
/// </summary>
public sealed partial class CellsModule
{
    private readonly Dictionary<int, CellEditSession> _sessions = new();
    private readonly Dictionary<uint, SavedGlow> _savedGlows = new();
    private int _editorTick;

    private readonly record struct SavedGlow(Color Color, int Type, int Range, int Team, bool Glowing);

    // ================================================================== commands

    [ConsoleCommand("css_celldoors_open", "sm_celldoors_open - force the cell doors open")]
    [ConsoleCommand("css_cdo", "sm_cdo - force the cell doors open")]
    public void CmdForceOpen(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "cells")) { Chat.Tell(caller, "core.no_access"); return; }
        if (!ForceOpen(caller))
        {
            Chat.ReplyT(caller, info, "cells.none_to_open");
            return;
        }

        Chat.PrintAll(T("cells.admin_forced", Chat.Name(caller)), false);
        Logger.LogInformation("\"{Admin}\" forced open cell doors", AdminLabel(caller));
    }

    [ConsoleCommand("css_celldoors_edit", "sm_celldoors_edit - in-game cell door editor")]
    public void CmdEdit(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return; // original: players only, console does nothing
        if (!Perms.Has(caller, "cells.edit")) { Chat.Tell(caller, "core.no_access"); return; }
        if (!_sessions.TryGetValue(caller.Slot, out var s))
        {
            s = new CellEditSession { Slot = caller.Slot };
            _sessions[caller.Slot] = s;
        }

        s.LookingAt = 0;
        s.ConfirmClear = false;
        RefreshGlows();
        if (!VectorUtil.HasRayTrace) Chat.Tell(caller, "cells.editor.no_trace");
        ShowEditor(caller, s);
    }

    [ConsoleCommand("css_celldoors_list", "sm_celldoors_list - list door-type entities on this map (console)")]
    public void CmdList(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "cells.edit")) { Chat.Tell(caller, "core.no_access"); return; }
        var list = DoorTypeEntities(caller?.EyePosition());
        Chat.PrintToConsole(caller, $"[Cells] {_map}: {list.Count} door-type / button entities (* = cell door, + = linked button)");
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var raw = e.EntityHandle.Raw;
            var mark = _doorHandles.Contains(raw) ? "*" : _buttonHandles.Contains(raw) ? "+" : " ";
            var hid = CellAutoDetect.SafeHammerId(e);
            var name = e.Entity?.Name ?? "";
            Chat.PrintToConsole(caller, $"{mark} #{i + 1,-3} {e.DesignerName,-22} {(name.Length > 0 ? name : "(unnamed)"),-28} hid:{(hid.Length > 0 ? hid : "?")}");
        }

        Chat.PrintToConsole(caller, "Use css_celldoors_add <#|name> / css_celldoors_remove <#|name>, then css_celldoors_save.");
        if (caller != null && info.CallingContext == CommandCallingContext.Chat) Chat.Tell(caller, "cells.see_console");
    }

    [ConsoleCommand("css_celldoors_add", "sm_celldoors_add <#index|targetname> - add a cell door (live, use _save to keep)")]
    public void CmdAdd(CCSPlayerController? caller, CommandInfo info) => AddRemoveByArg(caller, info, add: true);

    [ConsoleCommand("css_celldoors_remove", "sm_celldoors_remove <#index|targetname> - remove a cell door (live)")]
    public void CmdRemove(CCSPlayerController? caller, CommandInfo info) => AddRemoveByArg(caller, info, add: false);

    [ConsoleCommand("css_celldoors_save", "sm_celldoors_save - save the live cell door list for this map")]
    public void CmdSave(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "cells.edit")) { Chat.Tell(caller, "core.no_access"); return; }
        Save(caller, info);
    }

    private void AddRemoveByArg(CCSPlayerController? caller, CommandInfo info, bool add)
    {
        if (caller != null && !Perms.Has(caller, "cells.edit")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 2)
        {
            Chat.ReplyT(caller, info, "core.usage", (add ? "css_celldoors_add" : "css_celldoors_remove") + " <#index|targetname>");
            return;
        }

        var arg = info.GetArg(1).Trim();
        var list = DoorTypeEntities(caller?.EyePosition());
        CBaseEntity? target = null;
        if (arg.StartsWith('#') && int.TryParse(arg[1..], out var idx) || int.TryParse(arg, out idx))
        {
            if (idx >= 1 && idx <= list.Count) target = list[idx - 1];
        }
        else
        {
            target = list.FirstOrDefault(e => (e.Entity?.Name ?? "") == arg);
        }

        if (target == null)
        {
            Chat.ReplyT(caller, info, "cells.editor.not_found", arg);
            return;
        }

        if (DoorClasses.IsButton(target.DesignerName))
        {
            ToggleButton(caller, info, target);
            return;
        }

        if (add) AddDoor(caller, info, target);
        else RemoveDoor(caller, info, target);
    }

    /// <summary>Optional warden !open / !o (ultjb_cell_doors_warden_open; not in the original).</summary>
    private void OnWardenOpenCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        var isWarden = Jb.Warden.IsWarden(caller);
        if (!isWarden && !Perms.Has(caller, "cells")) { Chat.Tell(caller, "cells.warden_only"); return; }
        if (_haveOpened) { Chat.Tell(caller, "cells.already_open"); return; }
        if (!ForceOpen(caller)) { Chat.Tell(caller, "cells.none_to_open"); return; }
        Chat.TellAll(isWarden ? "cells.warden_opened" : "cells.admin_opened", caller.PlayerName);
        Logger.LogInformation("\"{Player}\" opened the cell doors with !open", AdminLabel(caller));
    }

    private static string AdminLabel(CCSPlayerController? c) =>
        c == null ? "Console" : $"{c.PlayerName}<{c.UserId}><{c.SteamID}>";

    // ================================================================== editor menu

    private void ShowEditor(CCSPlayerController c, CellEditSession s)
    {
        var look = s.LookingAt != 0 ? new CHandle<CBaseEntity>(s.LookingAt).Value : null;
        if (look != null && !look.IsValid) look = null;

        var m = new JbMenu(Lang.T("cells.editor.title")) { Timeout = -1, ExitButton = true };
        var isButton = look != null && DoorClasses.IsButton(look.DesignerName);
        if (look != null)
        {
            var name = look.Entity?.Name ?? "";
            var hid = CellAutoDetect.SafeHammerId(look);
            m.Subtitle = Hud.Escape(look.DesignerName) + "<br>" +
                         Hud.Escape(name.Length > 0 ? name : $"(unnamed) hid:{(hid.Length > 0 ? hid : "?")}");
        }

        // Page 1 keeps the original layout: 1 add/remove, 2 spacer, 3 save.
        if (look == null)
            m.Add(Lang.T("cells.editor.look"), null, false);
        else if (isButton)
            m.Add(Lang.T("cells.editor.look_door"), null, false);
        else if (FindEntry(look) != null)
            m.Add(Lang.T("cells.editor.remove"), p => { RemoveDoor(p, null, Current(s)); Reshow(p, s); });
        else
            m.Add(Lang.T("cells.editor.add"), p => { AddDoor(p, null, Current(s)); Reshow(p, s); });
        m.AddInfo(" ");
        m.Add(Lang.T("cells.editor.save"), p => { Save(p, null); Reshow(p, s); });

        // CS2 additions.
        var linked = isButton && FindButton(look!) != null;
        m.Add(Lang.T(linked ? "cells.editor.unlink_button" : "cells.editor.link_button"),
            p => { ToggleButton(p, null, Current(s)); Reshow(p, s); }, isButton);
        m.Add(Lang.T("cells.editor.autodetect"), p => { EditorAutoDetect(p); Reshow(p, s); });
        m.Add(Lang.T("cells.editor.test_open"), p =>
        {
            if (!ForceOpen(p)) Chat.Tell(p, "cells.none_to_open");
            Reshow(p, s);
        });
        m.Add(Lang.T("cells.editor.next"), p => { Cycle(p, s, +1); Reshow(p, s); });
        m.Add(Lang.T("cells.editor.prev"), p => { Cycle(p, s, -1); Reshow(p, s); });
        m.Add(Lang.T(s.ConfirmClear ? "cells.editor.clear_confirm" : "cells.editor.clear"), p =>
        {
            if (!s.ConfirmClear)
            {
                s.ConfirmClear = true;
            }
            else
            {
                s.ConfirmClear = false;
                _entries.Clear();
                _buttons.Clear();
                RestoreGlowsNotIn(null);
                ResolveDoors(roundStart: false, additive: false);
                Chat.Tell(p, "cells.editor.cleared");
                Logger.LogInformation("\"{Admin}\" removed all cell doors (not saved yet)", AdminLabel(p));
            }

            Reshow(p, s);
        });
        m.OnCancel = p => EndEditSession(p.Slot);
        s.Menu = m;
        m.Open(c);
    }

    private void Reshow(CCSPlayerController p, CellEditSession s)
    {
        if (!p.IsValid || !_sessions.ContainsKey(p.Slot)) return;
        ShowEditor(p, s);
    }

    private static CBaseEntity? Current(CellEditSession s)
    {
        if (s.LookingAt == 0) return null;
        var e = new CHandle<CBaseEntity>(s.LookingAt).Value;
        return e != null && e.IsValid ? e : null;
    }

    // ================================================================== editor actions

    private void AddDoor(CCSPlayerController? by, CommandInfo? info, CBaseEntity? ent)
    {
        if (ent == null || !ent.IsValid || !AddEntityAsDoor(ent))
        {
            Chat.ReplyT(by, info, "cells.editor.add_failed");
            return;
        }

        var desc = FindEntry(ent)?.Describe() ?? ent.DesignerName;
        Chat.ReplyT(by, info, "cells.editor.added", desc);
        Logger.LogInformation("\"{Admin}\" added cell door \"{Door}\"", AdminLabel(by), desc);
    }

    private void RemoveDoor(CCSPlayerController? by, CommandInfo? info, CBaseEntity? ent)
    {
        var desc = ent != null && ent.IsValid ? FindEntry(ent)?.Describe() : null;
        if (ent == null || desc == null || !RemoveEntityFromBeingDoor(ent))
        {
            Chat.ReplyT(by, info, "cells.editor.remove_failed");
            return;
        }

        Chat.ReplyT(by, info, "cells.editor.removed", desc);
        Logger.LogInformation("\"{Admin}\" removed cell door \"{Door}\"", AdminLabel(by), desc);
    }

    private void ToggleButton(CCSPlayerController? by, CommandInfo? info, CBaseEntity? ent)
    {
        if (ent == null || !ent.IsValid || !DoorClasses.IsButton(ent.DesignerName))
        {
            Chat.ReplyT(by, info, "cells.editor.add_failed");
            return;
        }

        var existing = FindButton(ent);
        if (existing != null)
        {
            _buttons.Remove(existing);
            if (_buttons.Count == 0 && _openMethod != OpenMethod.Doors) _openMethod = OpenMethod.Doors;
            ResolveDoors(roundStart: false, additive: false);
            Chat.ReplyT(by, info, "cells.editor.button_unlinked", existing.Describe());
            return;
        }

        var name = ent.Entity?.Name ?? "";
        var hid = CellAutoDetect.SafeHammerId(ent);
        var entry = name.Length > 0
            ? new CellButtonEntry { Name = name }
            : new CellButtonEntry
            {
                HammerId = hid.Length > 0 ? hid : null, Class = ent.DesignerName,
                Origin = ent.AbsOrigin is { } o ? CellDoorStore.ToArray(o) : null
            };
        _buttons.Add(entry);
        // Pressing the map's own button is the open method for script-driven doors; with doors also listed use both.
        _openMethod = _entries.Count > 0 ? OpenMethod.Both : OpenMethod.Buttons;
        ResolveDoors(roundStart: false, additive: true);
        Chat.ReplyT(by, info, "cells.editor.button_linked", entry.Describe(), MethodName(_openMethod));
    }

    private void Save(CCSPlayerController? by, CommandInfo? info)
    {
        var doc = new CellDoorFile
        {
            Map = _map,
            Source = "manual",
            OpenMethod = MethodName(_openMethod),
            AutoOpenSeconds = _autoOpenOverride,
            Doors = _entries.ToList(),
            Buttons = _buttons.ToList(),
            SavedBy = by?.SteamID.ToString() ?? "console",
            SavedAt = DateTime.UtcNow.ToString("o")
        };

        switch (CellDoorStore.SaveJson(doc, Logger))
        {
            case CellDoorStore.SaveError.Directory:
                Chat.ReplyT(by, info, "cells.editor.save_dir_error");
                return;
            case CellDoorStore.SaveError.File:
                Chat.ReplyT(by, info, "cells.editor.save_file_error");
                return;
        }

        _source = "manual";
        _sourceDetail = CellDoorStore.JsonPath(_map);
        _jsonState = "loaded";
        Chat.ReplyT(by, info, "cells.editor.saved");
        Logger.LogInformation("\"{Admin}\" saved cell doors ({Count} entries) to {Path}", AdminLabel(by), _entries.Count, _sourceDetail);
    }

    private void EditorAutoDetect(CCSPlayerController p)
    {
        RunAutoDetect();
        RestoreGlowsNotIn(null);
        ResolveDoors(roundStart: false, additive: false);
        _announceAuto = false;
        var res = _lastAuto;
        if (res == null || res.Doors.Count == 0)
        {
            Chat.Tell(p, "cells.editor.autodetect_none", res?.Candidates ?? 0);
            return;
        }

        Chat.Tell(p, "cells.editor.autodetect_done", _doorHandles.Count, res.Method);
        foreach (var line in res.Table.Take(8)) Chat.PrintToConsole(p, line);
    }

    /// <summary>Cycle through door-type entities by distance and move the admin to each one (noclip), like EntityHooker.</summary>
    private void Cycle(CCSPlayerController p, CellEditSession s, int dir)
    {
        var list = DoorTypeEntities(p.EyePosition());
        if (list.Count == 0) { Chat.Tell(p, "cells.editor.none_on_map"); return; }
        s.CycleIndex = ((s.CycleIndex + dir) % list.Count + list.Count) % list.Count;
        var ent = list[s.CycleIndex];
        SetLook(p, s, ent.EntityHandle.Raw, redisplay: false);
        var center = EntityBounds.Center(ent);
        if (center != null && p.IsAliveP())
        {
            p.SetNoclip(true);
            p.TeleportTo(new Vector(center.X, center.Y, center.Z - 32f));
        }

        Chat.Tell(p, "cells.editor.cycled", s.CycleIndex + 1, list.Count, ent.DesignerName);
    }

    /// <summary>Every door-type and button entity, nearest first when a reference point is given.</summary>
    private static List<CBaseEntity> DoorTypeEntities(Vector? from)
    {
        var list = new List<CBaseEntity>();
        foreach (var ent in Utilities.GetAllEntities())
        {
            var dn = ent?.DesignerName;
            if (dn == null || !(DoorClasses.IsEditorDoorType(dn) || DoorClasses.IsButton(dn))) continue;
            list.Add(ent!.As<CBaseEntity>());
        }

        if (from == null) return list.OrderBy(e => e.DesignerName).ThenBy(e => e.Entity?.Name ?? "").ToList();
        return list.Select(e => (e, d: EntityBounds.TryGet(e, out var mn, out var mx) ? EntityBounds.DistanceToBox(from, mn, mx) : float.MaxValue))
            .OrderBy(t => t.d).Select(t => t.e).ToList();
    }

    // ================================================================== tracker (every 6 ticks)

    private void OnEditorTick()
    {
        if (_sessions.Count == 0 || ++_editorTick % 6 != 0) return;
        foreach (var s in _sessions.Values.ToArray())
        {
            var p = Players.Get(s.Slot).Controller;
            if (p == null || !p.IsValid || s.Menu == null || !ReferenceEquals(MenuService.Current(p), s.Menu))
            {
                // Closed, replaced by another menu or disconnected.
                EndEditSession(s.Slot);
                continue;
            }

            var target = FindLookTarget(p);
            var raw = target?.EntityHandle.Raw ?? 0u;
            if (raw != s.LookingAt) SetLook(p, s, raw, redisplay: true);
            else if (raw != 0 && target != null) DrawBox(s, target); // moving doors: keep the box on the entity
        }
    }

    private void SetLook(CCSPlayerController p, CellEditSession s, uint raw, bool redisplay)
    {
        s.LookingAt = raw;
        s.ConfirmClear = false;
        var ent = raw != 0 ? new CHandle<CBaseEntity>(raw).Value : null;
        if (ent != null && ent.IsValid) DrawBox(s, ent);
        else RemoveBox(s);
        if (redisplay) ShowEditor(p, s);
    }

    private CBaseEntity? FindLookTarget(CCSPlayerController p)
    {
        var pawn = p.PawnOrNull();
        if (pawn == null || pawn.AbsOrigin == null) return null;
        var eye = p.EyePosition();
#if NET10_0_OR_GREATER
        try
        {
            const Contents baseMask = Contents.Solid | Contents.Window | Contents.PassBullets | Contents.Debris |
                                      Contents.PhysicsProp | Contents.CsgoMoveable | Contents.WorldGeometry | Contents.StaticLevel;
            var tr = Trace.TraceShape(eye, pawn.EyeAngles, pawn, new TraceOptions
            {
                InteractsAs = Contents.Player, InteractsWith = baseMask,
                InteractsExclude = Contents.Player | Contents.Hitbox | Contents.Trigger | Contents.PlayerClip
            });
            var hit = tr.DidHit() ? DoorFromHit(tr.HitEntity()) : null;
            if (hit != null) return hit;

            // Pass 2: stop at player clips too (finds clip func_brush doors).
            var tr2 = Trace.TraceShape(eye, pawn.EyeAngles, pawn, new TraceOptions
            {
                InteractsAs = Contents.Player, InteractsWith = baseMask | Contents.PlayerClip,
                InteractsExclude = Contents.Player | Contents.Hitbox | Contents.Trigger
            });
            hit = tr2.DidHit() ? DoorFromHit(tr2.HitEntity()) : null;
            if (hit != null) return hit;

            // Invisible / nodraw doors: nearest door-type entity whose bounds are within 64 units of the hit point.
            var end = tr.DidHit() ? tr.EndPos : null;
            if (end == null) return null;
            CBaseEntity? best = null;
            var bestDist = 64f;
            foreach (var e in DoorTypeEntities(null))
            {
                if (!EntityBounds.TryGet(e, out var mn, out var mx)) continue;
                var d = EntityBounds.DistanceToBox(end, mn, mx);
                if (d > bestDist) continue;
                bestDist = d;
                best = e;
            }

            return best;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Cells] editor trace failed");
            return null;
        }
#else
        // net8.0 build: no ray-trace API. Pick the door-type entity nearest to the crosshair direction (within ~6
        // degrees and 2048 units) from the entities' real bounds.
        var fwd = VectorUtil.Forward(pawn.EyeAngles);
        CBaseEntity? best = null;
        var bestCos = 0.9945f;
        foreach (var e in DoorTypeEntities(null))
        {
            var c = EntityBounds.Center(e);
            if (c == null) continue;
            var dx = c.X - eye.X;
            var dy = c.Y - eye.Y;
            var dz = c.Z - eye.Z;
            var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1f || len > 2048f) continue;
            var cos = (dx * fwd.X + dy * fwd.Y + dz * fwd.Z) / len;
            if (cos <= bestCos) continue;
            bestCos = cos;
            best = e;
        }

        return best;
#endif
    }

    /// <summary>The hit entity if it is a door type / button, else its first door-type ancestor (up to 8 levels).</summary>
    private static CBaseEntity? DoorFromHit(CEntityInstance? hit)
    {
        if (hit == null || !hit.IsValid) return null;
        var ent = hit.As<CBaseEntity>();
        for (var depth = 0; depth < 8 && ent != null && ent.IsValid; depth++)
        {
            var dn = ent.DesignerName;
            if (DoorClasses.IsEditorDoorType(dn) || DoorClasses.IsButton(dn)) return ent;
            var owner = ent.CBodyComponent?.SceneNode?.PParent?.Owner;
            ent = owner != null && owner.IsValid ? owner.As<CBaseEntity>() : null;
        }

        return null;
    }

    // ================================================================== visuals (real beams + glow)

    private static void DrawBox(CellEditSession s, CBaseEntity ent)
    {
        if (!EntityBounds.TryGet(ent, out var mn, out var mx)) { RemoveBox(s); return; }
        var c = new[]
        {
            new Vector(mn.X, mn.Y, mn.Z), new Vector(mx.X, mn.Y, mn.Z), new Vector(mx.X, mx.Y, mn.Z), new Vector(mn.X, mx.Y, mn.Z),
            new Vector(mn.X, mn.Y, mx.Z), new Vector(mx.X, mn.Y, mx.Z), new Vector(mx.X, mx.Y, mx.Z), new Vector(mn.X, mx.Y, mx.Z)
        };
        var edges = new (int a, int b)[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)
        };
        var reuse = s.Box.Count == edges.Length && s.Box.All(b => b.IsValid);
        if (!reuse) RemoveBox(s);
        for (var i = 0; i < edges.Length; i++)
        {
            var (a, b) = edges[i];
            if (reuse)
            {
                Fx.MoveBeam(s.Box[i], c[a], c[b]);
            }
            else
            {
                var beam = Fx.Beam(c[a], c[b], Color.Yellow, 1.5f);
                if (beam != null) s.Box.Add(beam);
            }
        }
    }

    private static void RemoveBox(CellEditSession s)
    {
        foreach (var b in s.Box) Fx.SafeRemove(b);
        s.Box.Clear();
    }

    /// <summary>While anyone edits, resolved cell doors glow green; doors no longer in the set get their glow back.</summary>
    private void RefreshGlows()
    {
        if (!Cfg.EditorGlow || _sessions.Count == 0)
        {
            RestoreGlowsNotIn(null);
            return;
        }

        RestoreGlowsNotIn(_doorHandles);
        foreach (var raw in _doorHandles)
        {
            if (_savedGlows.ContainsKey(raw)) continue;
            var ent = new CHandle<CBaseModelEntity>(raw).Value;
            if (ent == null || !ent.IsValid) continue;
            try
            {
                var g = ent.Glow;
                _savedGlows[raw] = new SavedGlow(g.GlowColorOverride, g.GlowType, g.GlowRange, g.GlowTeam, g.Glowing);
                g.GlowColorOverride = Color.Lime;
                g.GlowType = 3;
                g.GlowRange = 4096;
                g.GlowTeam = -1;
                g.Glowing = true;
                Utilities.SetStateChanged(ent, "CBaseModelEntity", "m_Glow");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[Cells] glow failed on {Class}", ent.DesignerName);
            }
        }
    }

    private void RestoreGlowsNotIn(IReadOnlyCollection<uint>? keep)
    {
        foreach (var (raw, saved) in _savedGlows.ToArray())
        {
            if (keep != null && keep.Contains(raw)) continue;
            _savedGlows.Remove(raw);
            var ent = new CHandle<CBaseModelEntity>(raw).Value;
            if (ent == null || !ent.IsValid) continue;
            try
            {
                var g = ent.Glow;
                g.GlowColorOverride = saved.Color;
                g.GlowType = saved.Type;
                g.GlowRange = saved.Range;
                g.GlowTeam = saved.Team;
                g.Glowing = saved.Glowing;
                Utilities.SetStateChanged(ent, "CBaseModelEntity", "m_Glow");
            }
            catch
            {
                // entity going away
            }
        }
    }

    // ================================================================== session cleanup

    private void EndEditSession(int slot)
    {
        if (!_sessions.Remove(slot, out var s)) return;
        RemoveBox(s);
        var p = Players.Get(slot).Controller;
        if (p != null && p.IsValid && s.Menu != null && ReferenceEquals(MenuService.Current(p), s.Menu)) MenuService.Close(p, false);
        if (_sessions.Count == 0) RestoreGlowsNotIn(null);
    }

    private void EndAllEditSessions()
    {
        foreach (var slot in _sessions.Keys.ToArray()) EndEditSession(slot);
        RestoreGlowsNotIn(null);
    }

    public override void OnPlayerDisconnect(JbPlayer player) => EndEditSession(player.Slot);
}
