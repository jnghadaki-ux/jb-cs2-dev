namespace SwbsJailbreak.Modules.Warden;

/// <summary>Config section "Warden" (UltJB_Warden.sp v1.42). Defaults = original cvars / constants.</summary>
public sealed class WardenConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_warden_select_time: countdown before selecting a replacement warden (min 1).</summary>
    public int SelectTime { get; set; } = 3;

    /// <summary>ultjb_warden_select_time_initial: countdown before the first warden of the round (min 1).</summary>
    public int SelectTimeInitial { get; set; } = 10;

    /// <summary>
    /// ADDITION: auto warden. When nobody queued with !w (or the queue holds nobody eligible) the countdown still ends
    /// with a guard being picked automatically. false = only players who queued can become warden.
    /// </summary>
    public bool AutoWarden { get; set; } = true;

    /// <summary>"roundrobin" (original: the guard who has waited longest) or "random".</summary>
    public string AutoWardenMode { get; set; } = "roundrobin";

    /// <summary>
    /// ADDITION: warden bonus while guards are few. With at most BonusMaxGuards guards alive the warden gets one "step" per
    /// guard below the limit + 1 (2 max guards: 2 alive = 1 step, 1 alive = 2 steps), so the bonus stacks as guards die.
    /// With more guards alive the warden has no bonus at all.
    /// </summary>
    public bool BonusEnabled { get; set; } = true;
    public int BonusMaxGuards { get; set; } = 2;
    public int BonusHpPerStep { get; set; } = 25;
    public int BonusArmorPerStep { get; set; } = 25;

    /// <summary>Speed multiplier added per step (0 = off), e.g. 0.05 = +5 % per step.</summary>
    public float BonusSpeedPerStep { get; set; } = 0f;

    /// <summary>Warden model (original ctm_heavy.mdl). Empty = do not change the model. Must be a CS2 .vmdl.</summary>
    public string WardenModel { get; set; } = "";

    /// <summary>ADDITION (not in the original): optional render tint on the warden, "R,G,B". Empty = off.</summary>
    public string WardenColor { get; set; } = "";

    /// <summary>ADDITION (not in the original): show "Warden: name" as a low-priority centre HUD layer to everyone.</summary>
    public bool ShowWardenHud { get; set; } = false;

    /// <summary>Guard clan tag handling (guards show no tag, the warden shows WardenClanTag).</summary>
    public bool GuardClanTags { get; set; } = true;

    public string WardenClanTag { get; set; } = "Warden";

    /// <summary>Global !kiss cooldown in seconds (shared by every warden, consumed even on a miss).</summary>
    public float KissCooldown { get; set; } = 5.0f;

    /// <summary>!kiss: how far away (units) the kissed player may be.</summary>
    public float KissRange { get; set; } = 1500f;

    /// <summary>Original swoobles kiss.mp3. ".vsnd" path (played to players within KissSoundRadius) or sound event. Empty = silent.</summary>
    public string KissSound { get; set; } = "";

    public float KissSoundRadius { get; set; } = 1000f;

    /// <summary>Original music/revenge.wav, played to everyone. ".vsnd" path or sound event. Empty = silent.</summary>
    public string DeathSound { get; set; } = "";

    /// <summary>Debug only: allow bots to be selected as warden (the original excluded bots).</summary>
    public bool AllowBotWarden { get; set; } = false;

    // ---- pointer laser (double-tap USE) ----
    public bool PointerEnabled { get; set; } = true;
    public float PointerExpire { get; set; } = 0.5f;
    public int[] PointerColor { get; set; } = { 240, 50, 15, 255 };
    public float PointerWidth { get; set; } = 0.4f;
    public float PointerEndWidth { get; set; } = 1.0f;
    public float PulseRingRadius { get; set; } = 40f;
    public float PulseRingLife { get; set; } = 0.3f;
    public float PulseRingWidth { get; set; } = 3f;

    /// <summary>
    /// CounterStrikeSharp 1.0.368 (.NET 8) has no ray trace, so the warden aims with the stock CS2 ping key (player_ping,
    /// middle mouse by default): a warden ping shows the pointer laser at the pinged spot for this many seconds
    /// (0 = pings do not draw the pointer).
    /// </summary>
    public float PingPointerTime { get; set; } = 3f;

    /// <summary>Maximum age (seconds) of a warden ping used as the position for double-tap USE, rings and the healing area.</summary>
    public float PingMaxAge { get; set; } = 15f;

    /// <summary>Optional: print the cl_use_opens_buy_menu tip to a new warden (the original had this as a TODO).</summary>
    public bool ShowPointerTip { get; set; } = false;

    // ---- bullet line (triple-tap walk) / gesture ring (triple-tap duck) ----
    public bool GesturesEnabled { get; set; } = true;
    public float GestureTapWindow { get; set; } = 0.5f;
    public int[] LineColor { get; set; } = { 255, 0, 136, 255 };
    public float LineWidth { get; set; } = 5f;

    // ---- warden paintballs ----
    /// <summary>
    /// "beam" = on (crossed beam splats, the CS2 substitute for TE World Decal); "off" = no warden paintballs.
    /// </summary>
    public string PaintballMode { get; set; } = "beam";

    public float PaintballDelay { get; set; } = 0.02f;
    public int PaintballMaxSplats { get; set; } = 48;
    public float PaintballSize { get; set; } = 8f;
    public float PaintballWidth { get; set; } = 2f;
}

/// <summary>Config section "WardenMenu" (UltJB_WardenMenu.sp v1.11).</summary>
public sealed class WardenMenuConfig
{
    public bool Enabled { get; set; } = true;

    public float RingRadius { get; set; } = 50f;
    public int RingSizeMin { get; set; } = 1;
    public int RingSizeMax { get; set; } = 5;
    public int RingSegments { get; set; } = 32;
    public float RingWidth { get; set; } = 5f;

    public float HealingRadius { get; set; } = 33f;
    public float HealingInterval { get; set; } = 0.5f;
    public int HealingAmount { get; set; } = 5;
    public int HealingMaxHealth { get; set; } = 100;
    public int HealingRingSegments { get; set; } = 16;
    public float HealingRingWidth { get; set; } = 2f;

    /// <summary>Optional .vpcf replacing the swoobles "healing_area" particle (tinted (255,r,r)). Empty = beam ring only.</summary>
    public string HealingParticle { get; set; } = "";

    /// <summary>Original items/medshot4.wav when someone is healed. ".vsnd" path or sound event. Empty = silent.</summary>
    public string HealSound { get; set; } = "";

    public float HealSoundRadius { get; set; } = 1000f;

    /// <summary>ADDITION: "Days menu" entry (same as the warden typing !day; the Days module applies its own rules).</summary>
    public bool ShowDaysItem { get; set; } = true;

    /// <summary>ADDITION (gameplay change, off by default): "Open cell doors" entry calling the Cells module.</summary>
    public bool ShowOpenCellsItem { get; set; } = false;

    /// <summary>Addition: "Warden Games" item (opens !games) when the Games module is loaded.</summary>
    public bool ShowGamesItem { get; set; } = true;

    /// <summary>Addition: "Crew shakedown" item (opens !shakedown) when Crews and shakedowns are enabled.</summary>
    public bool ShowShakedownItem { get; set; } = true;
}
