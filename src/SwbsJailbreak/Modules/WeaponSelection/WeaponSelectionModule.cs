using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.WeaponSelection;

public sealed class WeaponSelectionConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_select_weapon_time (min 1): seconds a player has to pick before a random weapon is chosen.</summary>
    public int SelectWeaponTime { get; set; } = 15;

    /// <summary>
    /// CS2 replacement for the original m_iTeamNum loadout trick: after giving a weapon, compare its item definition
    /// index with the requested one and, on a mismatch (loadout substitution), retry once with the weapon's default team.
    /// </summary>
    public bool VerifyLoadoutSubstitution { get; set; } = true;

    /// <summary>Bots cannot use menus: pick a random allowed weapon for them immediately (useful for LR tests vs bots).</summary>
    public bool BotsPickRandom { get; set; } = true;
}

/// <summary>
/// Port of UltJB_API_WeaponSelection.sp (v1.10): the "Select your weapon" category/weapon menus with the original
/// WPN_FLAGS (as <see cref="WeaponFilter"/>), the selection timer with random fallback, the give helpers with
/// IsGettingItem (so pickup blockers let plugin-given weapons through) and LR-weapon marking.
/// </summary>
public sealed class WeaponSelectionModule : JbModule, IWeaponSelectionService
{
    private sealed class SelectionState
    {
        public required WeaponFilter Filter;
        public required Action<JbPlayer, string> Ok;
        public Action<JbPlayer>? Failed;
        public Timer? Timer;
        public JbMenu? CategoryMenu;
        public JbMenu? WeaponMenu;
    }

    private readonly SelectionState?[] _selections = new SelectionState?[Players.MaxSlots];
    private readonly bool[] _gettingItem = new bool[Players.MaxSlots];
    private readonly HashSet<uint> _lrWeapons = new();

    private WeaponSelectionConfig Cfg => Section<WeaponSelectionConfig>("WeaponSelection");

    public override int Order => 8;
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        Jb.WeaponSelection = this;
        Plugin.RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
    }

    public override void OnUnload(bool hotReload)
    {
        for (var i = 0; i < Players.MaxSlots; i++) DropState(i, closeMenu: true);
        _lrWeapons.Clear();
    }

    public override void OnMapStart(string mapName)
    {
        _lrWeapons.Clear();
        for (var i = 0; i < Players.MaxSlots; i++) DropState(i, closeMenu: false);
    }

    public override void OnPlayerDisconnect(JbPlayer player) => DropState(player.Slot, closeMenu: false);

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_lrWeapons.Count == 0) return;
        try
        {
            _lrWeapons.Remove(entity.EntityHandle.Raw);
        }
        catch
        {
            // entity already gone - nothing to clean
        }
    }

    // ================================================================== IWeaponSelectionService

    public bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, string> onSelected, Action<JbPlayer>? onFailed, WeaponFilter filter)
    {
        // Original: client 0 or invalid callbacks -> false.
        if (onSelected == null || filter == null) return false;
        var c = player.Controller;
        if (c == null || !player.IsValid) return false;

        DropState(player.Slot, closeMenu: true); // replaces any previous request for this client
        var state = new SelectionState { Filter = filter, Ok = onSelected, Failed = onFailed };

        var menu = BuildCategoryMenu(player, state);
        if (menu == null)
        {
            Chat.Tell(player, "weaponselect.no_categories");
            InvokeFailed(state, player);
            return false;
        }

        _selections[player.Slot] = state;

        if (c.IsBot)
        {
            if (Cfg.BotsPickRandom)
            {
                var slot = player.Slot;
                Server.NextFrame(() => OnSelectTimeout(slot, state));
                return true;
            }
        }
        else
        {
            state.CategoryMenu = menu;
            menu.Open(c);
        }

        var time = Math.Max(1, Cfg.SelectWeaponTime);
        var s = player.Slot;
        state.Timer = Plugin.AddTimer(time, () => OnSelectTimeout(s, state), TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    public bool CancelSelection(JbPlayer player)
    {
        if (!player.Connected) return false;
        DropState(player.Slot, closeMenu: true);
        return true;
    }

    public string? GetRandomWeapon(WeaponFilter filter) => WeaponTable.GetRandom(filter)?.Classname;

    public WeaponCategory? GetCategory(string weapon) => WeaponTable.GetCategory(weapon);

    /// <summary>UltJB_Weapons_GivePlayerWeapon: give + loadout check + LR-weapon mark when the player started an LR.</summary>
    public CBasePlayerWeapon? GiveWeapon(JbPlayer player, string weapon)
    {
        var info = WeaponTable.Find(weapon);
        if (info is { Giveable: false })
        {
            Logger.LogWarning("[WeaponSelection] {Weapon} does not exist in CS2 and cannot be given", info.Classname);
            return null;
        }

        var classname = info?.GiveName ?? NormalizeClassname(weapon);
        var w = GiveItemInternal(player, classname);

        if (w != null && info != null && Cfg.VerifyLoadoutSubstitution && info.Id is not (WeaponId.Knife or WeaponId.KnifeT or WeaponId.KnifeGG))
            w = VerifyLoadout(player, w, info);

        if (w != null && Jb.LastRequests.HasStartedLastRequest(player))
            _lrWeapons.Add(w.EntityHandle.Raw);
        return w;
    }

    public bool IsWeaponFromLastRequest(CBasePlayerWeapon weapon) =>
        weapon is { IsValid: true } && _lrWeapons.Contains(weapon.EntityHandle.Raw);

    public void MarkWeaponFromLastRequest(CBasePlayerWeapon weapon)
    {
        if (weapon is { IsValid: true }) _lrWeapons.Add(weapon.EntityHandle.Raw);
    }

    public bool IsGettingItem(JbPlayer player) => player.Slot is >= 0 and < Players.MaxSlots && _gettingItem[player.Slot];

    // ================================================================== extra API (not in Core/Api.cs yet)

    /// <summary>UltJB_Weapons_GivePlayerItem: give without the team trick and without LR marking.</summary>
    public CBasePlayerWeapon? GivePlayerItem(JbPlayer player, string classname) => GiveItemInternal(player, NormalizeClassname(classname));

    /// <summary>UltJB_Weapons_GivePlayerWeapon by id.</summary>
    public CBasePlayerWeapon? GivePlayerWeapon(JbPlayer player, WeaponId id) =>
        WeaponTable.Get(id) is { } info ? GiveWeapon(player, info.Classname) : null;

    // ================================================================== giving

    private static string NormalizeClassname(string weapon)
    {
        var s = weapon.Trim();
        if (!s.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
            s = "weapon_" + s;
        return s.ToLowerInvariant();
    }

    private CBasePlayerWeapon? GiveItemInternal(JbPlayer player, string classname)
    {
        var c = player.Controller;
        if (c == null || !player.IsAlive) return null;
        var slot = player.Slot;
        _gettingItem[slot] = true;
        try
        {
            var w = c.GiveNamedItem<CBasePlayerWeapon>(classname);
            return w is { IsValid: true } ? w : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[WeaponSelection] GiveNamedItem({Item}) failed", classname);
            return null;
        }
        finally
        {
            _gettingItem[slot] = false;
        }
    }

    private CBasePlayerWeapon? VerifyLoadout(JbPlayer player, CBasePlayerWeapon w, WeaponInfo info)
    {
        ushort got;
        try { got = w.AttributeManager.Item.ItemDefinitionIndex; }
        catch { return w; }
        if (got == info.DefIndex) return w;

        var pawn = player.Pawn;
        if (pawn == null || info.DefaultTeam == WeaponTable.TeamAny)
        {
            Logger.LogWarning("[WeaponSelection] Asked for {Want} (def {WantDef}) but CS2 gave def {Got}", info.Classname, info.DefIndex, got);
            return w;
        }

        // Retry once with the weapon's default team (the CS:GO m_iTeamNum trick), restored within the same call.
        try
        {
            pawn.RemovePlayerItem(w);
            if (w.IsValid) w.Remove();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[WeaponSelection] Could not remove substituted {Weapon}", info.Classname);
            return w.IsValid ? w : null;
        }

        var oldTeam = pawn.TeamNum;
        CBasePlayerWeapon? retry;
        try
        {
            pawn.TeamNum = (byte)info.DefaultTeam;
            retry = GiveItemInternal(player, info.GiveName);
        }
        finally
        {
            var p2 = player.Pawn;
            if (p2 != null) p2.TeamNum = oldTeam;
        }

        if (retry != null)
        {
            try
            {
                if (retry.AttributeManager.Item.ItemDefinitionIndex != info.DefIndex)
                    Logger.LogWarning("[WeaponSelection] {Weapon} still substituted by the loadout after the team retry", info.Classname);
            }
            catch
            {
                // ignore
            }
        }

        return retry;
    }

    // ================================================================== menus

    private JbMenu? BuildCategoryMenu(JbPlayer player, SelectionState state)
    {
        var menu = new JbMenu(Lang.T("weaponselect.title")) { ExitButton = false, Timeout = -1 };
        foreach (var (cat, text) in WeaponTable.MenuCategories)
        {
            if (state.Filter.DisabledCategories.Contains(cat)) continue;
            if (!WeaponTable.MenuItems(cat).Any(w => WeaponTable.Allows(state.Filter, w))) continue;
            var category = cat;
            var key = "weaponselect.cat." + cat.ToString().ToLowerInvariant();
            menu.Add(Lang.Has(key) ? Lang.T(key) : text, _ => OnCategorySelected(player, state, category));
        }

        return menu.Items.Count == 0 ? null : menu;
    }

    private void OnCategorySelected(JbPlayer player, SelectionState state, WeaponCategory category)
    {
        if (!ReferenceEquals(_selections[player.Slot], state)) return;
        var c = player.Controller;
        if (c == null) return;

        var menu = new JbMenu(Lang.T("weaponselect.title")) { ExitButton = false, Timeout = -1, Parent = state.CategoryMenu };
        foreach (var w in WeaponTable.MenuItems(category))
        {
            if (!WeaponTable.Allows(state.Filter, w)) continue;
            var pick = w.Id == WeaponId.Knife ? WeaponTable.RandomKnife() : w; // re-rolled each time the menu is built
            menu.Add(w.MenuText, _ => OnWeaponSelected(player, state, pick.Classname));
        }

        if (menu.Items.Count == 0)
        {
            Chat.Tell(player, "weaponselect.no_weapons");
            if (state.CategoryMenu != null && state.CategoryMenu.Items.Count > 0)
            {
                state.CategoryMenu.Open(c);
                return;
            }

            DropState(player.Slot, closeMenu: false);
            InvokeFailed(state, player);
            return;
        }

        state.WeaponMenu = menu;
        menu.Open(c);
    }

    private void OnWeaponSelected(JbPlayer player, SelectionState state, string classname)
    {
        if (!ReferenceEquals(_selections[player.Slot], state)) return;
        DropState(player.Slot, closeMenu: false);
        InvokeOk(state, player, classname);
    }

    private void OnSelectTimeout(int slot, SelectionState state)
    {
        if (!ReferenceEquals(_selections[slot], state)) return;
        state.Timer = null; // the timer fired (or the bot shortcut) - do not kill it again
        DropState(slot, closeMenu: true);
        var player = Players.Get(slot);
        if (!player.Connected) return;

        if (player.Controller is { IsBot: false }) Chat.Tell(player, "weaponselect.random");
        var w = WeaponTable.GetRandom(state.Filter);
        if (w == null)
        {
            Chat.Tell(player, "weaponselect.random_error");
            InvokeFailed(state, player);
            return;
        }

        InvokeOk(state, player, w.Classname);
    }

    private void DropState(int slot, bool closeMenu)
    {
        if (slot is < 0 or >= Players.MaxSlots) return;
        var state = _selections[slot];
        if (state == null) return;
        _selections[slot] = null;
        try { state.Timer?.Kill(); }
        catch { /* already dead */ }
        state.Timer = null;

        if (!closeMenu) return;
        var c = Players.Get(slot).Controller;
        if (c == null) return;
        var current = MenuService.Current(c);
        if (current != null && (ReferenceEquals(current, state.CategoryMenu) || ReferenceEquals(current, state.WeaponMenu)))
            MenuService.Close(c, false); // cancel without callbacks, like CancelMenu on the original private menu
    }

    private void InvokeOk(SelectionState state, JbPlayer player, string classname)
    {
        try
        {
            state.Ok(player, classname);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error calling weapon selection success.");
        }
    }

    private void InvokeFailed(SelectionState state, JbPlayer player)
    {
        if (state.Failed == null) return;
        try
        {
            state.Failed(player);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error calling weapon selection failed.");
        }
    }
}
