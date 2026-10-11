using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Cells;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Zones;

internal enum ZoneEditMode { None, Position, Size }

/// <summary>One admin's zone manager state (g_iSelectedZoneID, g_bInZoneMenu, g_bIsEditingPosition/Size, ...).</summary>
internal sealed class ZoneEditor
{
    public int Slot;
    public int SelectedId;
    public ZoneEditMode Mode;
    public float PositionDistance;
    public bool PositionHold; // after move-to-feet/ping: stay put until attack or attack2
    public bool ShowAll;
    public bool Active;
    public JbMenu? Menu;
    public float NextMove, NextDraw, NextNearby;
    public Vector3? CornerA;
    public readonly Dictionary<int, List<CBeam>> Boxes = new();
    public readonly Dictionary<int, (int Version, bool Selected)> Drawn = new();
    public readonly HashSet<int> Nearby = new();
}

public sealed partial class ZonesModule
{
    private static readonly Color EditColor = Color.FromArgb(255, 0, 255, 0);        // ZONE_EDIT_COLOR
    private static readonly Color NearbyColor = Color.FromArgb(220, 255, 0, 0);      // ZONE_EDIT_COLOR_RADIUS
    private const float EditBeamWidth = 2.5f;                                         // BOX_BEAM_WIDTH
    private const float NearbyBeamWidth = 1.2f;                                       // BOX_BEAM_WIDTH_RADIUS
    private const float MoveStep = 4f;                                                // +/-4 units per update
    private const float UpdateInterval = 0.05f;                                       // UPDATE_CHECK_TIME_*
    private const float DrawInterval = 0.1f;                                          // DISPLAY_BOX_DELAY
    private const float PingMaxAge = 20f;

    private readonly Dictionary<int, ZoneEditor> _editors = new();
    private readonly HashSet<int> _triggerNameWatchers = new();
    private readonly Dictionary<string, float> _triggerNameShown = new();
    private List<(string Name, Vector3 Mins, Vector3 Maxs)>? _triggerCache;

    // ====================================================================== commands

    [ConsoleCommand("css_zonemanager", "sm_zonemanager - opens the zone manager")]
    [ConsoleCommand("css_zm", "sm_zm - opens the zone manager")]
    public void CmdZoneManager(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) { info.ReplyToCommand("[Zones] in-game only; use css_zones_list from the console."); return; }
        if (!Perms.Has(caller, "zones")) { Chat.Tell(caller, "core.no_access"); return; }
        ShowMain(caller);
    }

    [ConsoleCommand("css_showtriggernames", "sm_showtriggernames - show trigger names in console as you touch them")]
    public void CmdShowTriggerNames(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        if (!Perms.Has(caller, "zones")) { Chat.Tell(caller, "core.no_access"); return; }
        if (_triggerNameWatchers.Remove(caller.Slot))
        {
            Chat.ReplyT(caller, info, "zones.triggernames.off");
            return;
        }

        _triggerNameWatchers.Add(caller.Slot);
        _triggerCache = null;
        Chat.ReplyT(caller, info, "zones.triggernames.on");
    }

    [ConsoleCommand("css_zones_list", "List this map's zones (id, type, name, position)")]
    public void CmdList(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "zones")) { Chat.Tell(caller, "core.no_access"); return; }
        Chat.PrintToConsole(caller, $"---- Zones on {_map} ({_zones.Count}) ----");
        foreach (var z in _zones.OrderBy(z => z.Id))
        {
            var c = z.Box.Center;
            Chat.PrintToConsole(caller,
                $"#{z.Id,-3} {z.Type?.DisplayName ?? z.TypeKey + " (not loaded)",-22} {Quote(z.Title),-20} {(z.Type?.Summary(z) ?? ""),-18} at {c.X:0} {c.Y:0} {c.Z:0}{(z.Record.Imported ? "  imported:" + z.Record.ImportedName : "")}");
        }

        if (caller != null && info.CallingContext == CommandCallingContext.Chat) Chat.Tell(caller, "zones.see_console");
    }

    // ====================================================================== menus

    private ZoneEditor Editor(CCSPlayerController c)
    {
        if (!_editors.TryGetValue(c.Slot, out var ed))
        {
            ed = new ZoneEditor { Slot = c.Slot };
            _editors[c.Slot] = ed;
        }

        return ed;
    }

    /// <summary>
    /// Zone menus never use JbMenu.Parent: every screen is rebuilt by its Show* method (with explicit Back / Done items),
    /// so the editor mode always matches the menu on screen.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ValidMapName = new(@"^[a-z0-9_.\-]{1,64}$");

    private JbMenu NewMenu(CCSPlayerController c, string title)
    {
        var ed = Editor(c);
        ChatInput.Forget(c.Slot);
        var m = new JbMenu(title) { Timeout = -1, OnCancel = p => OnMenuCancelled(p) };
        ed.Menu = m;
        ed.Active = true;
        return m;
    }

    private void Open(CCSPlayerController c, JbMenu m)
    {
        Editor(c).Menu = m;
        m.Open(c);
    }

    private void OnMenuCancelled(CCSPlayerController c)
    {
        if (!_editors.TryGetValue(c.Slot, out var ed)) return;
        if (ed.Mode == ZoneEditMode.Size && Find(ed.SelectedId) is { } z) LogChange(c, "size", z);
        DeactivateEditor(ed);
        if (_dirty) Save();
    }

    internal void ShowMain(CCSPlayerController c)
    {
        var ed = Editor(c);
        ed.Mode = ZoneEditMode.None;
        var m = NewMenu(c, T("zones.menu.main"));
        m.Subtitle = Hud.Escape(T("zones.menu.main_sub", _map, _zones.Count, Math.Max(1, Cfg.MaxZones)));
        m.Add(T("zones.menu.add"), AddZoneHere);
        m.Add(T("zones.menu.select"), p => SelectNext(p, 0), _zones.Count > 0);
        m.Add(T("zones.menu.select_here"), SelectZoneHere, _zones.Count > 0);
        m.Add(T("zones.menu.import_trigger"), AskImportTrigger);
        m.Add(T("zones.menu.import_map"), AskImportMap, Cfg.CanImportFromAnotherMap);
        m.Add(T(ed.ShowAll ? "zones.menu.show_all_on" : "zones.menu.show_all_off"), p =>
        {
            Editor(p).ShowAll = !Editor(p).ShowAll;
            Editor(p).NextNearby = 0;
            ShowMain(p);
        });
        m.Add(T(c.IsNoclip() ? "zones.menu.noclip_off" : "zones.menu.noclip_on"), p =>
        {
            if (p.IsAliveP()) p.SetNoclip(!p.IsNoclip());
            ShowMain(p);
        });
        m.Add(T(_dirty ? "zones.menu.save_dirty" : "zones.menu.save"), p =>
        {
            Save();
            Chat.Tell(p, "zones.saved", _zones.Count);
            ShowMain(p);
        });
        Open(c, m);
    }

    private void ShowSelect(CCSPlayerController c)
    {
        var ed = Editor(c);
        ed.Mode = ZoneEditMode.None;
        var z = Find(ed.SelectedId);
        if (z == null)
        {
            Chat.Tell(c, "zones.none_to_select");
            ShowMain(c);
            return;
        }

        if (z.Type == null) Chat.Tell(c, "zones.type_not_loaded", z.TypeKey);
        var m = NewMenu(c, T("zones.menu.select_title"));
        m.Subtitle = Describe(z);
        m.Add(T("zones.menu.next"), p => SelectNext(p, +1));
        m.Add(T("zones.menu.previous"), p => SelectNext(p, -1));
        m.Add(T("zones.menu.edit"), ShowEdit);
        m.Add(T("zones.menu.teleport_to"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && p.IsAliveP())
            {
                var pos = sel.Box.BottomCenter;
                p.TeleportTo(new CssVector(pos.X, pos.Y, pos.Z + 2f), null, new CssVector(0, 0, 0));
            }

            ShowSelect(p);
        });
        m.Add(T("zones.menu.duplicate"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel)
            {
                var copy = Duplicate(sel, new Vector3(0, 0, sel.Box.Size.Z + 8f));
                if (copy == null) Chat.Tell(p, "zones.max_zones");
                else
                {
                    Editor(p).SelectedId = copy.Id;
                    LogChange(p, "duplicate", copy);
                }
            }

            ShowSelect(p);
        });
        m.Add(T("zones.menu.delete"), ShowDelete);
        m.Add(T("zones.menu.back_to_main"), ShowMain);
        Open(c, m);
    }

    private void ShowDelete(CCSPlayerController c)
    {
        var z = Find(Editor(c).SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.delete_confirm"));
        m.Subtitle = Describe(z);
        m.AddInfo(" ");
        m.Add(T("zones.menu.no"), ShowSelect);
        m.AddInfo(" ");
        m.Add(T("zones.menu.yes"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && Remove(sel))
            {
                LogChange(p, "delete", sel);
                RemoveBox(Editor(p), sel.Id);
                Chat.Tell(p, "zones.removed");
            }

            SelectNext(p, +1);
        });
        m.ExitButton = false;
        Open(c, m);
    }

    private void ShowEdit(CCSPlayerController c)
    {
        var ed = Editor(c);
        ed.Mode = ZoneEditMode.None;
        var z = Find(ed.SelectedId);
        if (z == null) { Chat.Tell(c, "zones.selected_invalid"); ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.edit_title"));
        m.Subtitle = Describe(z);
        var editable = !z.Record.Imported;
        m.Add(T("zones.menu.edit_position"), p => ShowPosition(p), editable);
        m.Add(T("zones.menu.edit_size"), ShowSize, editable);
        m.Add(T("zones.menu.edit_corners"), ShowCorners, editable);
        m.Add(T("zones.menu.edit_angles"), ShowAngles);
        m.Add(T("zones.menu.edit_type"), ShowType);
        m.Add(T("zones.menu.edit_data"), ShowData, z.Type is { Fields.Count: > 0 });
        m.Add(T("zones.menu.edit_label"), AskLabel);
        m.Add(T("zones.menu.back_to_selection"), ShowSelect);
        Open(c, m);
    }

    private void ShowPosition(CCSPlayerController c, bool hold = false)
    {
        var ed = Editor(c);
        var z = Find(ed.SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.position_title"));
        m.Subtitle = Describe(z);
        m.AddInfo(T("zones.menu.position_look"));
        m.AddInfo(T("zones.menu.position_attack"));
        m.AddInfo(T("zones.menu.position_attack2"));
        m.Add(T("zones.menu.move_to_feet"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && Feet(p) is { } f)
            {
                sel.Record.Origin = Zone.A(f - new Vector3((sel.Box.Mins.X + sel.Box.Maxs.X) * 0.5f, (sel.Box.Mins.Y + sel.Box.Maxs.Y) * 0.5f, sel.Box.Mins.Z));
                MarkDirty(sel);
            }

            ShowPosition(p, hold: true);
        });
        m.Add(T("zones.menu.move_to_ping"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && Aim.TryGetPing(p, PingMaxAge, out var ping, out _))
            {
                var target = ToV3(ping);
                sel.Record.Origin = Zone.A(target - new Vector3((sel.Box.Mins.X + sel.Box.Maxs.X) * 0.5f, (sel.Box.Mins.Y + sel.Box.Maxs.Y) * 0.5f, sel.Box.Mins.Z));
                MarkDirty(sel);
            }
            else Chat.Tell(p, "zones.no_ping");

            ShowPosition(p, hold: true);
        });
        m.Add(T("zones.menu.done"), p => { LogChange(p, "position", Find(Editor(p).SelectedId)); ShowEdit(p); });
        Open(c, m);

        ed.Mode = ZoneEditMode.Position;
        ed.PositionHold = hold;
        if (hold) return; // just placed: no view turn and no follow until the admin presses attack / attack2

        // Face the zone and keep the current distance (DisplayMenu_EditPosition).
        var eye = ToV3(c.EyePosition());
        var center = z.Box.Center;
        ed.PositionDistance = Math.Max(32f, Vector3.Distance(eye, center));
        var dir = center - eye;
        var yaw = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
        var pitch = -MathF.Atan2(dir.Z, MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y)) * 180f / MathF.PI;
        c.PawnOrNull()?.Teleport(null, new QAngle(pitch, yaw, 0), null);
    }

    private void ShowSize(CCSPlayerController c)
    {
        var ed = Editor(c);
        var z = Find(ed.SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.size_title"));
        m.Subtitle = Describe(z);
        m.AddInfo(T("zones.menu.size_look"));
        m.AddInfo(T("zones.menu.size_attack"));
        m.AddInfo(T("zones.menu.size_attack2"));
        m.Add(T("zones.menu.expand_to_ping"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && Aim.TryGetPing(p, PingMaxAge, out var ping, out _) && p.PawnOrNull() is { } pawn)
            {
                var mins = sel.Box.Mins;
                var maxs = sel.Box.Maxs;
                if (ZoneEdit.MoveFaceTo(pawn.EyeAngles.X, pawn.EyeAngles.Y, sel.Box.Origin, ref mins, ref maxs, ToV3(ping)))
                {
                    sel.Record.Mins = Zone.A(mins);
                    sel.Record.Maxs = Zone.A(maxs);
                    MarkDirty(sel);
                }
                else Chat.Tell(p, "zones.look_at_face");
            }
            else Chat.Tell(p, "zones.no_ping");

            ShowSize(p);
        });
        m.Add(T("zones.menu.done"), p => { LogChange(p, "size", Find(Editor(p).SelectedId)); ShowEdit(p); });
        Open(c, m);
        ed.Mode = ZoneEditMode.Size;
    }

    /// <summary>ADDITION: size the box from two corner points (feet or ping) instead of growing it face by face.</summary>
    private void ShowCorners(CCSPlayerController c)
    {
        var ed = Editor(c);
        var z = Find(ed.SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.corners_title"));
        m.Subtitle = Describe(z) + (ed.CornerA is { } a ? $"<br>{Hud.Escape(T("zones.menu.corner_a_set", a.X.ToString("0"), a.Y.ToString("0"), a.Z.ToString("0")))}" : "");
        m.Add(T("zones.menu.corner_a_feet"), p => SetCorner(p, true, false));
        m.Add(T("zones.menu.corner_a_ping"), p => SetCorner(p, true, true));
        m.Add(T("zones.menu.corner_b_feet"), p => SetCorner(p, false, false), ed.CornerA != null);
        m.Add(T("zones.menu.corner_b_ping"), p => SetCorner(p, false, true), ed.CornerA != null);
        m.Add(T("zones.menu.done"), ShowEdit);
        Open(c, m);
    }

    private void SetCorner(CCSPlayerController c, bool first, bool usePing)
    {
        var ed = Editor(c);
        Vector3? point = null;
        if (usePing)
        {
            if (Aim.TryGetPing(c, PingMaxAge, out var ping, out _)) point = ToV3(ping);
            else Chat.Tell(c, "zones.no_ping");
        }
        else point = Feet(c);

        if (point == null) { ShowCorners(c); return; }
        if (first)
        {
            ed.CornerA = point;
            ShowCorners(c);
            return;
        }

        if (ed.CornerA is { } a && Find(ed.SelectedId) is { } z)
        {
            var box = ZoneBox.FromCorners(a, point.Value);
            z.Record.Origin = Zone.A(box.Origin);
            z.Record.Mins = Zone.A(box.Mins);
            z.Record.Maxs = Zone.A(box.Maxs);
            MarkDirty(z);
            ed.CornerA = null;
            LogChange(c, "corners", z);
        }

        ShowEdit(c);
    }

    private void ShowAngles(CCSPlayerController c)
    {
        var z = Find(Editor(c).SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.angles_title"));
        m.Subtitle = Describe(z);
        m.AddInfo(T("zones.menu.angles_look"));
        m.Add(T("zones.menu.angles_set"), p =>
        {
            if (Find(Editor(p).SelectedId) is { } sel && p.PawnOrNull() is { } pawn)
            {
                var a = pawn.EyeAngles;
                sel.Record.Angles = new[] { a.X, a.Y, a.Z };
                MarkDirty(sel);
                LogChange(p, "angles", sel);
            }

            ShowAngles(p);
        });
        m.Add(T("zones.menu.done"), ShowEdit);
        m.ExitButton = false;
        Open(c, m);
    }

    private void ShowType(CCSPlayerController c)
    {
        var z = Find(Editor(c).SelectedId);
        if (z == null) { ShowMain(c); return; }
        var m = NewMenu(c, T("zones.menu.type_title"));
        m.Subtitle = Describe(z);
        foreach (var t in _typeList)
        {
            var type = t;
            var mark = ReferenceEquals(z.Type, t) ? "&#9658; " : "";
            m.Add(mark + Hud.Escape(t.DisplayName), p =>
            {
                if (Find(Editor(p).SelectedId) is { } sel)
                {
                    SetType(sel, type);
                    LogChange(p, "type", sel);
                }

                ShowEdit(p);
            });
        }

        m.Add(T("zones.menu.done"), ShowEdit);
        Open(c, m);
    }

    // ---------------------------------------------------------------------- type data (generic replacement for each type's OnEditData menu)

    private void ShowData(CCSPlayerController c)
    {
        var z = Find(Editor(c).SelectedId);
        if (z?.Type == null) { ShowEdit(c); return; }
        var m = NewMenu(c, T("zones.menu.data_title", Hud.Escape(z.Type.DisplayName)));
        m.Subtitle = Hud.Escape(z.Type.Description);
        foreach (var f in z.Type.Fields)
        {
            var field = f;
            switch (f.Kind)
            {
                case ZoneFieldKind.Text:
                    var s = z.GetString(f.Index);
                    m.Add(Hud.Escape($"{f.Label}: {(s.Length > 0 ? s : T("zones.data.not_set"))}"), p => AskText(p, field));
                    break;
                case ZoneFieldKind.Seconds:
                    var sec = z.GetInt(f.Index);
                    m.Add(Hud.Escape($"{f.Label}: {(sec != 0 ? T("zones.data.seconds", sec) : T("zones.data.not_set"))}"), p => ShowNumber(p, field));
                    break;
                case ZoneFieldKind.Number:
                    m.Add(Hud.Escape($"{f.Label}: {z.GetInt(f.Index)}"), p => ShowNumber(p, field));
                    break;
                case ZoneFieldKind.Flags:
                    var bits = z.GetInt(f.Index);
                    foreach (var (name, value) in f.Options)
                    {
                        var bit = value;
                        m.Add(Hud.Escape($"[{((bits & bit) != 0 ? "x" : " ")}] {name}"), p =>
                        {
                            if (Find(Editor(p).SelectedId) is { } sel)
                            {
                                sel.SetInt(field.Index, sel.GetInt(field.Index) ^ bit);
                                RetypeData(sel);
                            }

                            ShowData(p);
                        });
                    }

                    break;
                case ZoneFieldKind.Choice:
                    var cur = z.GetInt(f.Index);
                    var label = f.Options.FirstOrDefault(o => o.Value == cur).Name ?? cur.ToString();
                    m.Add(Hud.Escape($"{f.Label}: {label}"), p =>
                    {
                        if (Find(Editor(p).SelectedId) is { } sel && field.Options.Length > 0)
                        {
                            var idx = Array.FindIndex(field.Options, o => o.Value == sel.GetInt(field.Index));
                            sel.SetInt(field.Index, field.Options[(idx + 1) % field.Options.Length].Value);
                            RetypeData(sel);
                        }

                        ShowData(p);
                    });
                    break;
            }
        }

        m.Add(T("zones.menu.done"), ShowEdit);
        Open(c, m);
    }

    private void ShowNumber(CCSPlayerController c, ZoneField f)
    {
        var z = Find(Editor(c).SelectedId);
        if (z == null) { ShowEdit(c); return; }
        var m = NewMenu(c, Hud.Escape(f.Label));
        var v = z.GetInt(f.Index);
        m.Subtitle = Hud.Escape((f.Kind == ZoneFieldKind.Seconds ? (v != 0 ? T("zones.data.seconds", v) : T("zones.data.not_set")) : v.ToString())
                                + (f.Help.Length > 0 ? " — " + f.Help : ""));
        foreach (var step in new[] { 1, 5, 30, -1, -5, -30 })
        {
            var d = step;
            m.Add((d > 0 ? "+" : "") + d, p => AdjustInt(p, f, d), keepOpen: false);
        }

        m.Add(T("zones.data.type_value"), p => AskNumber(p, f));
        m.Add(T(f.Kind == ZoneFieldKind.Seconds ? "zones.data.clear_timer" : "zones.data.reset"), p => AdjustInt(p, f, int.MinValue));
        m.Add(T("zones.menu.done"), ShowData);
        Open(c, m);
    }

    private void AdjustInt(CCSPlayerController c, ZoneField f, int delta)
    {
        if (Find(Editor(c).SelectedId) is { } z)
        {
            var v = delta == int.MinValue ? 0 : Math.Clamp(z.GetInt(f.Index) + delta, f.Min, f.Max);
            z.SetInt(f.Index, v);
            RetypeData(z);
        }

        ShowNumber(c, f);
    }

    private void AskNumber(CCSPlayerController c, ZoneField f)
    {
        ChatInput.Ask(c, T("zones.data.ask_number", f.Label, f.Min, f.Max), (p, text) =>
        {
            if (int.TryParse(text, out var v) && Find(Editor(p).SelectedId) is { } z)
            {
                z.SetInt(f.Index, Math.Clamp(v, f.Min, f.Max));
                RetypeData(z);
            }
            else Chat.Tell(p, "zones.data.bad_number");

            ShowNumber(p, f);
        }, p => ShowNumber(p, f));
    }

    private void AskText(CCSPlayerController c, ZoneField f)
    {
        ChatInput.Ask(c, T("zones.data.ask_text", f.Label) + (f.Help.Length > 0 ? " " + f.Help : ""), (p, text) =>
        {
            if (Find(Editor(p).SelectedId) is { } z)
            {
                z.SetString(f.Index, text.Equals("-", StringComparison.Ordinal) ? "" : text);
                RetypeData(z);
            }

            ShowData(p);
        }, ShowData);
    }

    /// <summary>Data changed: re-run the type's assignment so cached state follows (the original RecreateZone).</summary>
    private void RetypeData(Zone z)
    {
        if (z.Type != null)
        {
            var t = z.Type;
            SafeCall(() => t.OnUnassigned(z));
            SafeCall(() => t.OnAssigned(z));
        }

        MarkDirty(z);
    }

    private void AskLabel(CCSPlayerController c)
    {
        ChatInput.Ask(c, T("zones.ask_label"), (p, text) =>
        {
            if (Find(Editor(p).SelectedId) is { } z)
            {
                z.Record.Label = text.Equals("-", StringComparison.Ordinal) ? "" : text.Length > 48 ? text[..48] : text;
                MarkDirty(z);
            }

            ShowEdit(p);
        }, ShowEdit);
    }

    // ---------------------------------------------------------------------- add / select / import

    private void AddZoneHere(CCSPlayerController c)
    {
        var feet = Feet(c);
        var pawn = c.PawnOrNull();
        if (feet == null || pawn == null)
        {
            Chat.Tell(c, "zones.must_be_alive");
            ShowMain(c);
            return;
        }

        var a = pawn.EyeAngles;
        var z = Create(feet.Value, ZoneBox.DefaultMins, ZoneBox.DefaultMaxs, new Vector3(a.X, a.Y, a.Z));
        if (z == null)
        {
            Chat.Tell(c, "zones.max_zones");
            ShowMain(c);
            return;
        }

        LogChange(c, "create", z);
        Editor(c).SelectedId = z.Id;
        ShowEdit(c);
    }

    private void SelectZoneHere(CCSPlayerController c)
    {
        var feet = Feet(c);
        Zone? z = null;
        if (feet != null) z = _zones.Where(x => x.Box.OverlapsHull(feet.Value)).OrderBy(x => x.Box.Size.LengthSquared()).FirstOrDefault();
        if (z == null && Aim.TryGetPing(c, PingMaxAge, out var ping, out _))
            z = _zones.Where(x => x.Box.Contains(ToV3(ping))).OrderBy(x => x.Box.Size.LengthSquared()).FirstOrDefault();
        if (z == null)
        {
            Chat.Tell(c, "zones.not_in_zone");
            ShowMain(c);
            return;
        }

        Editor(c).SelectedId = z.Id;
        ShowSelect(c);
    }

    /// <summary>SelectZone_Next / SelectZone_Previous: walk the zones in id order, wrapping around.</summary>
    private void SelectNext(CCSPlayerController c, int direction)
    {
        var ed = Editor(c);
        if (_zones.Count == 0)
        {
            ed.SelectedId = 0;
            Chat.Tell(c, "zones.none_to_select");
            ShowMain(c);
            return;
        }

        var ordered = _zones.OrderBy(z => z.Id).ToList();
        var idx = ordered.FindIndex(z => z.Id == ed.SelectedId);
        idx = idx < 0 ? 0 : (idx + direction + ordered.Count) % ordered.Count;
        ed.SelectedId = ordered[idx].Id;
        ShowSelect(c);
    }

    private void AskImportTrigger(CCSPlayerController c)
    {
        ChatInput.Ask(c, T("zones.import.ask"), (p, name) =>
        {
            ImportTrigger(p, name);
            ShowMain(p);
        }, ShowMain);
    }

    private bool ImportTrigger(CCSPlayerController c, string name)
    {
        var found = false;
        foreach (var e in FindNamedEntities(name).ToList())
        {
            if (!EntityBounds.TryGet(e, out var mn, out var mx) || e.AbsOrigin == null) continue;
            var origin = ToV3(e.AbsOrigin);
            var mins = ToV3(mn) - origin;
            var maxs = ToV3(mx) - origin;
            if (_zones.Any(z => z.Record.Imported && string.Equals(z.Record.ImportedName, name, StringComparison.OrdinalIgnoreCase)
                                && Vector3.Distance(z.Box.Origin, origin) < 1f && Vector3.Distance(z.Box.Mins, mins) < 1f && Vector3.Distance(z.Box.Maxs, maxs) < 1f))
            {
                Chat.Tell(c, "zones.import.already", name);
                continue;
            }

            var rot = e.AbsRotation;
            var z2 = Create(origin, mins, maxs, rot == null ? Vector3.Zero : new Vector3(rot.X, rot.Y, rot.Z), true, name);
            if (z2 == null)
            {
                Chat.Tell(c, "zones.max_zones");
                break;
            }

            Chat.Tell(c, "zones.import.added", name);
            LogChange(c, "import", z2);
            found = true;
        }

        if (!found) Chat.Tell(c, "zones.import.none", name);
        return found;
    }

    private void AskImportMap(CCSPlayerController c)
    {
        if (!Cfg.CanImportFromAnotherMap) { ShowMain(c); return; }
        ChatInput.Ask(c, T("zones.import_map.ask"), (p, map) =>
        {
            var other = map.Trim().ToLowerInvariant();
            if (!ValidMapName.IsMatch(other) || other.Contains(".."))
            {
                Chat.Tell(p, "zones.import_map.none", other.Replace('{', '(').Replace('}', ')'));
                ShowMain(p);
                return;
            }

            if (!Storage.MapDocExists("zones", other))
            {
                Chat.Tell(p, "zones.import_map.none", other);
                ShowMain(p);
                return;
            }

            var doc = Storage.LoadMapDoc<ZoneMapDoc>("zones", other);
            var added = 0;
            foreach (var r in doc.Zones ?? new List<ZoneRecord>())
            {
                if (r == null) continue;
                r.Normalize();
                var z = Create(Zone.V(r.Origin), Zone.V(r.Mins), Zone.V(r.Maxs), Zone.V(r.Angles), r.Imported, r.ImportedName);
                if (z == null) break;
                z.Record.Ints = (int[])r.Ints.Clone();
                z.Record.Strings = (string[])r.Strings.Clone();
                z.Record.Label = r.Label;
                if (_types.TryGetValue(r.Type, out var t)) SetType(z, t);
                else SetUnloadedType(z, r.Type);
                added++;
            }

            Logger.LogInformation("[Zones] \"{Admin}\" imported {Count} zones from {From} into {Map}", Chat.Name(p), added, other, _map);
            Chat.Tell(p, "zones.import_map.done", added, other);
            ShowMain(p);
        }, ShowMain);
    }

    // ====================================================================== editor tick (OnPreThinkPost)

    private void EditorTick()
    {
        if (_triggerNameWatchers.Count > 0 && _tick % 16 == 0) TriggerNamesTick();
        if (_editors.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var ed in _editors.Values.ToList())
        {
            if (!ed.Active) continue;
            var c = Players.Get(ed.Slot)?.Controller;
            if (c == null || !c.IsValid || !ReferenceEquals(MenuService.Current(c), ed.Menu))
            {
                // Left the zone menus (exit, another plugin's menu, disconnect).
                DeactivateEditor(ed);
                continue;
            }

            if (now >= ed.NextMove)
            {
                ed.NextMove = now + UpdateInterval;
                if (ed.Mode == ZoneEditMode.Position) UpdatePosition(c, ed);
                else if (ed.Mode == ZoneEditMode.Size) UpdateSize(c, ed);
            }

            if (now >= ed.NextDraw)
            {
                ed.NextDraw = now + DrawInterval;
                DrawEditor(c, ed, now);
            }
        }
    }

    /// <summary>EditZone_Position_TryUpdate: hold attack to push the zone away, attack2 to pull it closer.</summary>
    private void UpdatePosition(CCSPlayerController c, ZoneEditor ed)
    {
        var z = Find(ed.SelectedId);
        var pawn = c.PawnOrNull();
        if (z == null || pawn == null || z.Record.Imported) return;
        var buttons = c.Buttons;
        var eye = ToV3(c.EyePosition());
        if (ed.PositionHold)
        {
            if ((buttons & (PlayerButtons.Attack | PlayerButtons.Attack2)) == 0) return;
            ed.PositionHold = false;
            ed.PositionDistance = Math.Max(32f, Vector3.Distance(eye, z.Box.Center));
        }

        if ((buttons & PlayerButtons.Attack) != 0) ed.PositionDistance += MoveStep;
        else if ((buttons & PlayerButtons.Attack2) != 0) ed.PositionDistance = Math.Max(32f, ed.PositionDistance - MoveStep);
        var fwd = ZoneEdit.Forward(pawn.EyeAngles.X, pawn.EyeAngles.Y);
        var target = eye + fwd * ed.PositionDistance - (z.Box.Mins + z.Box.Maxs) * 0.5f;
        if (Vector3.DistanceSquared(target, z.Box.Origin) < 0.01f) return;
        z.Record.Origin = Zone.A(target);
        MarkDirty(z);
    }

    /// <summary>EditZone_Size_TryUpdate: attack grows, attack2 shrinks the face you look at by 4 units.</summary>
    private void UpdateSize(CCSPlayerController c, ZoneEditor ed)
    {
        var z = Find(ed.SelectedId);
        var pawn = c.PawnOrNull();
        if (z == null || pawn == null || z.Record.Imported) return;
        var buttons = c.Buttons;
        float amount;
        if ((buttons & PlayerButtons.Attack) != 0) amount = MoveStep;
        else if ((buttons & PlayerButtons.Attack2) != 0) amount = -MoveStep;
        else return;
        var mins = z.Box.Mins;
        var maxs = z.Box.Maxs;
        ZoneEdit.ResizeByLook(pawn.EyeAngles.X, pawn.EyeAngles.Y, ref mins, ref maxs, amount);
        z.Record.Mins = Zone.A(mins);
        z.Record.Maxs = Zone.A(maxs);
        MarkDirty(z);
    }

    /// <summary>TryUpdateZoneBox: the selected zone in green, the closest others in red, only to this admin.</summary>
    private void DrawEditor(CCSPlayerController c, ZoneEditor ed, float now)
    {
        if (now >= ed.NextNearby)
        {
            ed.NextNearby = now + 1f;
            ed.Nearby.Clear();
            var feet = Feet(c) ?? ToV3(c.EyePosition());
            var count = ed.ShowAll ? 40 : Math.Max(0, Cfg.EditorNearbyZones);
            var range = ed.ShowAll ? 4000f : float.MaxValue;
            foreach (var z in _zones.Where(z => z.Id != ed.SelectedId)
                         .Select(z => (z, d: z.Box.DistanceTo(feet)))
                         .Where(t => t.d <= range)
                         .OrderBy(t => t.d)
                         .Take(count))
                ed.Nearby.Add(z.z.Id);
        }

        var wanted = new HashSet<int>(ed.Nearby);
        if (Find(ed.SelectedId) != null) wanted.Add(ed.SelectedId);
        foreach (var id in ed.Boxes.Keys.Where(id => !wanted.Contains(id)).ToList()) RemoveBox(ed, id);
        foreach (var id in wanted)
        {
            var z = Find(id);
            if (z == null) continue;
            DrawBox(ed, z, id == ed.SelectedId);
        }
    }

    private void DrawBox(ZoneEditor ed, Zone z, bool selected)
    {
        if (ed.Drawn.TryGetValue(z.Id, out var d) && d.Selected != selected) RemoveBox(ed, z.Id);
        if (!ed.Boxes.TryGetValue(z.Id, out var beams))
        {
            beams = new List<CBeam>();
            ed.Boxes[z.Id] = beams;
        }

        if (ed.Drawn.TryGetValue(z.Id, out d) && d.Version == z.Version && beams.Count == 13 && beams.All(b => b.IsValid)) return;

        var color = selected ? EditColor : NearbyColor;
        var width = selected ? EditBeamWidth : NearbyBeamWidth;
        var corners = z.Box.Corners();
        var segments = new List<(Vector3 A, Vector3 B)>(13);
        foreach (var (a, b) in ZoneBox.Edges) segments.Add((corners[a], corners[b]));
        var center = z.Box.Center;
        segments.Add((center, center + ZoneEdit.Forward(z.Angles.X, z.Angles.Y) * 64f)); // forward indicator

        for (var i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            if (i < beams.Count && beams[i].IsValid)
            {
                Fx.MoveBeam(beams[i], ToCss(a), ToCss(b));
                continue;
            }

            var beam = Fx.Beam(ToCss(a), ToCss(b), color, width);
            if (beam == null) continue;
            Visibility.OnlyFor(beam, ed.Slot);
            if (i < beams.Count) beams[i] = beam;
            else beams.Add(beam);
        }

        ed.Drawn[z.Id] = (z.Version, selected);
    }

    private static void RemoveBox(ZoneEditor ed, int id)
    {
        if (ed.Boxes.Remove(id, out var beams))
            foreach (var b in beams)
            {
                Visibility.Release(b);
                Fx.SafeRemove(b);
            }

        ed.Drawn.Remove(id);
    }

    private void DeactivateEditor(ZoneEditor ed)
    {
        ed.Active = false;
        ed.Mode = ZoneEditMode.None;
        ed.Menu = null;
        foreach (var id in ed.Boxes.Keys.ToList()) RemoveBox(ed, id);
    }

    private void EndEditor(int slot)
    {
        _triggerNameWatchers.Remove(slot);
        if (_editors.Remove(slot, out var ed)) DeactivateEditor(ed);
        if (_dirty && !_editors.Values.Any(s => s.Active)) Save();
    }

    private void EndAllEditors()
    {
        foreach (var ed in _editors.Values) DeactivateEditor(ed);
        _editors.Clear();
        _triggerNameWatchers.Clear();
        _triggerCache = null;
        _triggerNameShown.Clear();
    }

    // ---------------------------------------------------------------------- trigger names (OnClientTouchPost)

    private void TriggerNamesTick()
    {
        _triggerCache ??= BuildTriggerCache();
        var now = Server.CurrentTime;
        foreach (var slot in _triggerNameWatchers.ToList())
        {
            var c = Players.Get(slot)?.Controller;
            if (c == null || !c.IsValid) { _triggerNameWatchers.Remove(slot); continue; }
            var feet = Feet(c);
            if (feet == null) continue;
            foreach (var (name, mn, mx) in _triggerCache)
            {
                var box = new ZoneBox(Vector3.Zero, mn, mx);
                if (!box.OverlapsHull(feet.Value)) continue;
                var key = slot + "-" + name;
                if (_triggerNameShown.TryGetValue(key, out var last) && now < last + 2f) continue;
                _triggerNameShown[key] = now;
                Chat.PrintToConsole(c, "Trigger name: " + name);
            }
        }
    }

    private List<(string, Vector3, Vector3)> BuildTriggerCache()
    {
        var list = new List<(string, Vector3, Vector3)>();
        foreach (var e in Utilities.GetAllEntities())
        {
            if (!e.IsValid || !e.DesignerName.StartsWith("trigger_", StringComparison.Ordinal)) continue;
            string? n;
            try { n = e.Entity?.Name; }
            catch { continue; }
            if (string.IsNullOrEmpty(n)) continue;
            var b = e.As<CBaseEntity>();
            if (!EntityBounds.TryGet(b, out var mn, out var mx)) continue;
            list.Add((n, ToV3(mn), ToV3(mx)));
        }

        return list;
    }

    // ---------------------------------------------------------------------- text helpers

    private string Describe(Zone z)
    {
        var editing = _editors.Values.Count(e => e.Active && e.SelectedId == z.Id);
        var source = z.Record.Imported ? T("zones.info.imported", z.Record.ImportedName) : T("zones.info.plugin");
        var type = z.Type?.DisplayName ?? T("zones.info.type_missing", z.TypeKey);
        var label = z.Record.Label.Length > 0 ? " · " + z.Record.Label : "";
        return Hud.Escape($"#{z.Id}{label} · {type} · {z.Type?.Summary(z) ?? ""}") + "<br>" +
               Hud.Escape(source + (editing > 1 ? " · " + T("zones.info.editors", editing) : ""));
    }

    private void LogChange(CCSPlayerController c, string what, Zone? z)
    {
        if (z == null) return;
        Logger.LogInformation("[Zones] \"{Admin}\" {What} zone #{Id} ({Type}) on {Map}", Chat.Name(c), what, z.Id, z.TypeKey, _map);
    }

    private static string Quote(string s) => "\"" + s + "\"";

    private static string T(string key, params object?[] args) => Lang.T(key, args);
}
