namespace Swbs.Common;

/// <summary>How the next round's variation is chosen.</summary>
public enum RoundSchedule
{
    /// <summary>Players vote between a few variations near the end of each round (the default).</summary>
    Vote,

    /// <summary>A weighted random variation, never the same twice in a row.</summary>
    Random,

    /// <summary>Every enabled variation in turn.</summary>
    Rotation
}

/// <summary>Per-variation switches (key = the variation's key, e.g. "rpg").</summary>
public sealed class VariationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Relative chance in Random mode and of being offered in a vote (0 = never offered, admins can still set it).</summary>
    public int Weight { get; set; } = 10;

    /// <summary>Round length for this variation in minutes (0 = Rounds.RoundMinutes).</summary>
    public double Minutes { get; set; }

    /// <summary>At least this many players in game (0 = the variation's own minimum).</summary>
    public int MinPlayers { get; set; }
}

/// <summary>Section "Rounds": the round variations system (inspired by SWBS Jailbreak's days).</summary>
public sealed class RoundsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Length of a round in minutes. The game mode's round never ends by itself; the rounds system ends it.</summary>
    public double RoundMinutes { get; set; } = 5;

    public RoundSchedule Schedule { get; set; } = RoundSchedule.Vote;

    /// <summary>Normal rounds (the mode as it is) between two variation rounds. 0 = every round is a variation.</summary>
    public int NormalRoundsBetween { get; set; } = 1;

    /// <summary>Variations offered in a vote.</summary>
    public int VoteOptions { get; set; } = 3;

    /// <summary>The vote opens this many seconds before the round ends.</summary>
    public int VoteAtSecondsLeft { get; set; } = 40;

    /// <summary>Seconds the vote stays open.</summary>
    public int VoteSeconds { get; set; } = 20;

    /// <summary>The vote also offers "Normal round".</summary>
    public bool VoteIncludesNormal { get; set; } = true;

    /// <summary>The same variation is not offered again until this many other rounds were played.</summary>
    public int RepeatCooldownRounds { get; set; } = 2;

    /// <summary>Seconds the intro banner of a variation round stays up.</summary>
    public float IntroSeconds { get; set; } = 6f;

    /// <summary>Seconds of scoreboard between the end of a round and the next one.</summary>
    public float RoundEndDelay { get; set; } = 6f;

    /// <summary>Points for the round's 1st, 2nd and 3rd place (variation rounds only).</summary>
    public List<int> PlacePoints { get; set; } = new() { 25, 15, 10 };

    /// <summary>Sounds (CS2 sound events; empty = none).</summary>
    public string IntroSound { get; set; } = "UIPanorama.popup_accept_match_beep";
    public string VoteSound { get; set; } = "UIPanorama.generic_button_press";
    public string MvpSound { get; set; } = "UIPanorama.round_report_round_won";

    /// <summary>Per variation: on/off, weight, length, minimum players. Filled with every variation on first run.</summary>
    public Dictionary<string, VariationSettings> Variations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Section "RoundTuning": the knobs of the individual variations.</summary>
public sealed class RoundTuning
{
    // ---- RPG Arena (SMPlugins CustomWeapon_RPG wpn_rpg_* cvars)
    public float RpgBaseDamage { get; set; } = 30f;
    public float RpgRocketSpeed { get; set; } = 1000f;
    public float RpgPushForceBase { get; set; } = 102f;
    public float RpgPushForceMultiplier { get; set; } = 6f;
    public float RpgPushFalloffPercent { get; set; } = 0.55f;
    public float RpgPushMinDistanceClamp { get; set; } = 32f;
    public float RpgPushMaxDistance { get; set; } = 115f;
    public float RpgAttackDelayDeploy { get; set; } = 0.5f;
    public float RpgAttackDelayAttack { get; set; } = 0.8f;
    public float RpgReloadDelay { get; set; } = 1.25f;
    public int RpgClip { get; set; } = 4;
    public bool RpgAllowDamageSelf { get; set; } = true;
    public bool RpgKnockbackSelf { get; set; } = true;
    public bool RpgKnockbackEnemies { get; set; } = true;
    public string RpgExplosionParticle { get; set; } = "particles/explosions_fx/explosion_basic.vpcf";
    public string RpgExplosionSound { get; set; } = "BaseGrenade.Explode";
    public string RpgShootSound { get; set; } = "Weapon_Nova.Single";
    public string RpgAirshotSound { get; set; } = "UIPanorama.round_report_round_won";

    /// <summary>Rocket Rain: reload and fire delay multiplier, clip size.</summary>
    public float RocketRainSpeed { get; set; } = 0.4f;
    public int RocketRainClip { get; set; } = 8;

    // ---- Throwing Knives (SMPlugins ThrowingKnife)
    public string KnifeModel { get; set; } = "weapons/models/knife/knife_default_ct/weapon_knife_default_ct.vmdl";
    public float KnifeSpeed { get; set; } = 2250f;
    public float KnifeGravity { get; set; } = 0.4f;
    public float KnifeDamage { get; set; } = 100f;
    public float KnifeDelay { get; set; } = 1f;
    public string KnifeHitPlayerSound { get; set; } = "Weapon_Knife.Hit";
    public string KnifeHitWorldSound { get; set; } = "Weapon_Knife.HitWall";

    // ---- Dodgeball / HE Party
    public float DodgeballRefillSeconds { get; set; } = 1f;
    public float DodgeballGravity { get; set; } = 0.6f;
    public float HeRefillSeconds { get; set; } = 1.5f;
    public float HeGravity { get; set; } = 0.5f;

    // ---- Gun Game (SWBS Jailbreak Gun Game day)
    public List<string> GunGameWeapons { get; set; } = new()
    {
        "weapon_glock", "weapon_p250", "weapon_deagle", "weapon_mp9", "weapon_p90", "weapon_ump45", "weapon_nova",
        "weapon_xm1014", "weapon_famas", "weapon_ak47", "weapon_m4a1_silencer", "weapon_ssg08", "weapon_awp"
    };
    public int GunGameKnifeDemote { get; set; } = 1;

    // ---- mutators
    public float LowGravity { get; set; } = 0.4f;
    public float SpeedMultiplier { get; set; } = 1.5f;
    /// <summary>Vampire: share of the damage dealt that heals the attacker, and the health cap.</summary>
    public float VampireShare { get; set; } = 0.5f;
    public int VampireMaxHealth { get; set; } = 150;
    public int GlassCannonHealth { get; set; } = 35;

    // ---- objectives
    /// <summary>Bounty Hunter: score for killing the bounty is this plus one per kill they made while being the bounty.</summary>
    public int BountyBase { get; set; } = 3;
    public int JuggernautBaseHealth { get; set; } = 400;
    public int JuggernautHealthPerPlayer { get; set; } = 60;
    public float JuggernautSpeed { get; set; } = 0.85f;
    public string JuggernautWeapon { get; set; } = "weapon_negev";
    public int JuggernautKillScore { get; set; } = 5;
    public int LastManStandingLives { get; set; } = 3;
}
