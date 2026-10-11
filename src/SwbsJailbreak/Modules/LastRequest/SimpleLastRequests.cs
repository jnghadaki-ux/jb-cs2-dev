using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>
/// Ports of the small LastRequests/*.sp plugins: Freeday (Classic LR, Auto Bhop, Speed, Low Gravity), End Round (Draw)
/// and Rebel (AWP, Scout, Wild Cowboy, Coked Taser, Drug Dealer, Machine Gunner). Names, categories, flags, health and
/// weapons are the originals'.
/// </summary>
public sealed class SimpleLastRequests : JbModule
{
    public override string Name => "LastRequests.Simple";
    public override int Order => 10;

    private const LrFlags RebelBase = LrFlags.Rebel | LrFlags.TempInvincible | LrFlags.NoRadar | LrFlags.RandomTeleportLocation;

    private int _speedId = -1, _gravityId = -1, _bhopId = -1, _drawId = -1;
    private bool _drawRound;

    public override void OnLoad(bool hotReload)
    {
        // Enabled flags are evaluated before any module loads, so the LR core's presence is checked here.
        if (Jb.LastRequests is not LastRequestModule) return;
        var lr = Jb.LastRequests;

        // ---------------------------------------------------------- Freeday
        lr.Register(new LastRequestDefinition { Name = "Classic LR", Category = "Freeday", Flags = LrFlags.Freeday, OnStart = _ => { } });

        _bhopId = lr.Register(new LastRequestDefinition
        {
            Name = "Auto Bhop", Category = "Freeday", Flags = LrFlags.Freeday,
            OnStart = p => Movement.SetAutoBhop(p.Controller, "lr.autobhop", true),
            OnEnd = (p, _) => Movement.SetAutoBhop(p.Controller, "lr.autobhop", false)
        });

        _speedId = lr.Register(new LastRequestDefinition
        {
            Name = "Speed", Category = "Freeday", Flags = LrFlags.Freeday,
            OnStart = p => Movement.SetSpeed(p.Controller, "lr.freeday", 2.4f),
            OnEnd = (p, _) => Movement.ClearSpeed(p.Controller, "lr.freeday")
        });

        _gravityId = lr.Register(new LastRequestDefinition
        {
            Name = "Low Gravity", Category = "Freeday", Flags = LrFlags.Freeday,
            OnStart = p => Movement.SetGravity(p.Controller, "lr.freeday", 0.2f),
            OnEnd = (p, _) => Movement.ClearGravity(p.Controller, "lr.freeday")
        });

        // ---------------------------------------------------------- End Round
        _drawId = lr.Register(new LastRequestDefinition
        {
            Name = "Draw", Category = "End Round", Description = "This ends the round in a draw", Flags = LrFlags.LastPrisonerOnlyCanUse,
            OnStart = p =>
            {
                Round.End(RoundEndReason.RoundDraw, 3f);
                Chat.PrintAll(Lang.T("lr.draw.chosen", p.Name, "Draw"), false);
                _drawRound = true; // nobody takes damage until the next round (m_takedamage 0)
            }
        });

        // ---------------------------------------------------------- Rebel
        lr.Register(Rebel("AWP", RebelBase | LrFlags.LastPrisonerOnlyCanUse, _ => 125, "weapon_awp", armor: true));
        lr.Register(Rebel("Scout", RebelBase, _ => 200, "weapon_ssg08", armor: true));
        lr.Register(Rebel("Wild Cowboy", RebelBase, _ => 600, "weapon_elite"));
        lr.Register(Rebel("Machine Gunner",
            RebelBase | LrFlags.AllowWeaponPickups | LrFlags.AllowWeaponDrops | LrFlags.ShowAllGuardsOnRadar,
            _ => 175 + 25 * Players.AliveGuards().Count(), "weapon_m249"));

        lr.Register(new LastRequestDefinition
        {
            Name = "Coked Taser", Category = "Rebel", Flags = LrFlags.LastPrisonerOnlyCanUse | LrFlags.Rebel | LrFlags.TempInvincible,
            OnStart = p =>
            {
                Movement.SetSpeed(p.Controller, "lr.cokedtaser", 3f);
                Jb.LastRequests.SetHealth(p, 200);
                Cvars.SetTemp("mp_taser_recharge_time", "1");
                Jb.LastRequests.StripWeapons(p, true);
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife");
                Jb.WeaponSelection.GiveWeapon(p, "weapon_taser");
            },
            OnEnd = (p, _) =>
            {
                RestoreIfAlive(p);
                Cvars.Restore("mp_taser_recharge_time");
            }
        });

        lr.Register(new LastRequestDefinition
        {
            Name = "Drug Dealer", Category = "Rebel", Flags = RebelBase | LrFlags.ShowAllGuardsOnRadar,
            OnStart = p =>
            {
                Jb.LastRequests.SetHealth(p, 300);
                Jb.LastRequests.StripWeapons(p, true);
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife_t");
                Jb.WeaponSelection.GiveWeapon(p, "weapon_ak47");
                var drugs = Jb.Effects.GetEffectId("Drugs");
                var speed = Jb.Effects.GetEffectId("Speed");
                if (drugs > 0) Jb.Effects.StartEffect(p, drugs, 2f);
                if (speed > 0) Jb.Effects.StartEffect(p, speed, Jb.Effects.GetDefaultData(speed));
            },
            OnEnd = (p, _) =>
            {
                RestoreIfAlive(p);
                var drugs = Jb.Effects.GetEffectId("Drugs");
                var speed = Jb.Effects.GetEffectId("Speed");
                if (drugs > 0) Jb.Effects.StopEffect(p, drugs);
                if (speed > 0) Jb.Effects.StopEffect(p, speed);
            }
        });

        DamageService.Register(Name, 200, OnDamage);
        JbEvents.SettingsSpawnPost += OnSpawnPost;
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        JbEvents.SettingsSpawnPost -= OnSpawnPost;
    }

    public override void OnRoundStart() => _drawRound = false;

    public override void OnMapStart(string mapName) => _drawRound = false; // warmup has no round_start

    private HookResult OnDamage(DamageContext ctx) => _drawRound ? HookResult.Handled : HookResult.Continue;

    /// <summary>Freeday LRs span a respawn: re-apply their modifier on every spawn while active (original SpawnPost hooks).</summary>
    private void OnSpawnPost(JbPlayer p)
    {
        var active = Jb.LastRequests.GetActive(p);
        if (active == null) return;
        if (active.Id == _speedId) Movement.SetSpeed(p.Controller, "lr.freeday", 2.4f);
        else if (active.Id == _gravityId) Movement.SetGravity(p.Controller, "lr.freeday", 0.2f);
        else if (active.Id == _bhopId) Movement.SetAutoBhop(p.Controller, "lr.autobhop", true);
    }

    private static LastRequestDefinition Rebel(string name, LrFlags flags, Func<JbPlayer, int> health, string weapon, bool armor = false) => new()
    {
        Name = name, Category = "Rebel", Flags = flags,
        OnStart = p =>
        {
            Jb.LastRequests.SetHealth(p, health(p));
            Jb.LastRequests.StripWeapons(p, true);
            Jb.WeaponSelection.GiveWeapon(p, "weapon_knife_t");
            Jb.WeaponSelection.GiveWeapon(p, weapon);
            if (armor) p.Controller?.SetArmor(100, true);
        },
        OnEnd = (p, _) => RestoreIfAlive(p)
    };

    internal static void RestoreIfAlive(JbPlayer p)
    {
        if (p.Controller is { IsValid: true } c && c.IsAliveP()) Jb.LastRequests.RestoreWeapons(p);
    }
}
