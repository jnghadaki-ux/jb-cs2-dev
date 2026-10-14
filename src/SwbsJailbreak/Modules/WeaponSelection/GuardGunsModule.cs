using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.WeaponSelection;

/// <summary>Section "GuardGuns": SWBS CS2 addition, the !guns loadout menu for guards.</summary>
public sealed class GuardGunsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds after spawning (or the end of freeze time) a guard may still use !guns. 0 = whole round.</summary>
    public float WindowSeconds { get; set; } = 30f;

    /// <summary>Loadouts per guard per round.</summary>
    public int UsesPerRound { get; set; } = 1;

    /// <summary>Weapons never offered (designer names, e.g. "weapon_awp").</summary>
    public List<string> DisabledWeapons { get; set; } = new() { "weapon_awp", "weapon_g3sg1", "weapon_scar20", "weapon_negev", "weapon_m249" };

    /// <summary>Also offer a pistol after the primary.</summary>
    public bool OfferPistol { get; set; } = true;
}

/// <summary>
/// !guns: an alive guard picks a primary (then a pistol) from the weapon-selection menus. Replaces the weapon in that
/// slot. Not during days or last requests; limited per round and to a window after spawning.
/// </summary>
public sealed class GuardGunsModule : JbModule
{
    private GuardGunsConfig Cfg => Section<GuardGunsConfig>("GuardGuns");

    public override string Name => "GuardGuns";
    public override int Order => 83;
    public override bool Enabled => Cfg.Enabled;

    private readonly int[] _uses = new int[Players.MaxSlots];
    private readonly float[] _spawnTime = new float[Players.MaxSlots];

    public override void OnRoundStart() => Array.Clear(_uses);

    public override void OnPlayerSpawn(JbPlayer player) => _spawnTime[player.Slot] = Server.CurrentTime;

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _uses[player.Slot] = 0;
        _spawnTime[player.Slot] = 0;
    }

    [ConsoleCommand("css_guns", "sm_guns - (guard) choose your weapons")]
    [ConsoleCommand("css_gun", "sm_gun - (guard) choose your weapons")]
    public void CmdGuns(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        var p = caller.Jb();
        if (p == null) return;
        if (caller.Team != CsTeam.CounterTerrorist || !caller.IsAliveP()) { Chat.Tell(caller, "guns.only_guards"); return; }
        if (Jb.Days.IsInProgress) { Chat.Tell(caller, "guns.day"); return; }
        if (Jb.LastRequests.IsInLastRequest(p) || Jb.LastRequests.NumInitialized > 0) { Chat.Tell(caller, "guns.lr"); return; }
        if (_uses[caller.Slot] >= Math.Max(1, Cfg.UsesPerRound)) { Chat.Tell(caller, "guns.used"); return; }

        if (Cfg.WindowSeconds > 0)
        {
            // The window starts at the later of spawning and the end of freeze time.
            var start = Math.Max(_spawnTime[caller.Slot], Round.FreezeEndTime);
            if (Round.IsActive && Server.CurrentTime - start > Cfg.WindowSeconds) { Chat.Tell(caller, "guns.too_late", (int)Cfg.WindowSeconds); return; }
        }

        var primary = BuildFilter(pistols: false);
        if (!Jb.WeaponSelection.DisplaySelectionMenu(p, (pl, weapon) => OnPicked(pl, weapon, askPistol: Cfg.OfferPistol), null, primary))
            return;
        _uses[caller.Slot]++;
    }

    private WeaponFilter BuildFilter(bool pistols)
    {
        var f = new WeaponFilter();
        f.DisabledCategories.Add(WeaponCategory.Knife);
        f.DisabledCategories.Add(WeaponCategory.Grenades);
        if (pistols)
        {
            f.DisabledCategories.Add(WeaponCategory.Heavy);
            f.DisabledCategories.Add(WeaponCategory.Smgs);
            f.DisabledCategories.Add(WeaponCategory.Rifles);
        }
        else
        {
            f.DisabledCategories.Add(WeaponCategory.Pistols);
        }

        foreach (var w in Cfg.DisabledWeapons.Where(w => !string.IsNullOrWhiteSpace(w))) f.Disabled.Add(w.Trim());
        return f;
    }

    private void OnPicked(JbPlayer player, string weapon, bool askPistol)
    {
        var c = player.Controller;
        if (c == null || !c.IsAliveP() || c.Team != CsTeam.CounterTerrorist || Jb.Days.IsInProgress) return;

        var pistol = Weapons.IsPistol(weapon);
        // Free the slot first so the new weapon is equipped instead of dropped on the floor.
        foreach (var w in c.Weapons().ToList())
        {
            var name = w.DesignerName;
            if (pistol ? Weapons.IsPistol(name) : Weapons.IsPrimary(name)) Jb.Settings.StripWeaponFromOwner(w);
        }

        Jb.WeaponSelection.GiveWeapon(player, weapon);

        if (askPistol && !pistol)
            Server.NextFrame(() => Jb.WeaponSelection.DisplaySelectionMenu(player, (pl, w) => OnPicked(pl, w, askPistol: false), null, BuildFilter(pistols: true)));
    }
}
