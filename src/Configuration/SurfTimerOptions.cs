namespace SurfTimer.Configuration;

public sealed class SurfTimerOptions
{
    public const string SectionName = "SurfTimer";

    public bool Enabled { get; init; } = true;

    public bool DebugLogging { get; init; }

    public string DatabaseConnection { get; init; } = "surftimer";

    public string ServerId { get; init; } = "change-me";

    public string CatalogAuthorityServerId { get; init; } = "";

    public int MaximumReplayMinutes { get; init; } = 30;

    public int HudRefreshRateHz { get; init; } = 64;

    public MapVotingOptions MapVoting { get; init; } = new();

    public TitleOptions Titles { get; init; } = new();

    public ZoneVisibilityOptions ZoneVisibility { get; init; } = new();
    public SplitComparisonOptions SplitComparisons { get; init; } = new();
    public AnnouncementOptions Announcements { get; init; } = new();
}

public sealed class SplitComparisonOptions
{
    public bool Enabled { get; init; } = true;
    public string Reference { get; init; } = "Both";
    public int HudDurationSeconds { get; init; } = 4;
}

public sealed class AnnouncementOptions
{
    public bool Enabled { get; init; } = true;
    public bool WorldRecords { get; init; } = true;
    public bool TopTen { get; init; } = true;
    public bool FirstCompletions { get; init; } = true;
}

public sealed class TitleOptions
{
    public bool Enabled { get; init; } = true;
    public int MaximumCustomLength { get; init; } = 10;
}

public sealed class ZoneVisibilityOptions
{
    public bool Enabled { get; init; } = true;
    public bool RenderAllMaps { get; init; } = true;
    public IReadOnlyList<string> Maps { get; init; } =
    [
        "surf_boreas",
        "surf_kitsune",
        "surf_mesa_revo",
        "surf_mom"
    ];
    public int Red { get; init; } = 64;
    public int Green { get; init; } = 255;
    public int Blue { get; init; } = 112;
    public int Alpha { get; init; } = 210;
    public float Width { get; init; } = 1.5f;
}

public sealed class MapVotingOptions
{
    public bool Enabled { get; init; } = true;
    public bool RockTheVoteEnabled { get; init; } = true;
    public bool NominationEnabled { get; init; } = true;
    public bool EndOfMapVoteEnabled { get; init; } = true;
    public int ForceVoteAfterMinutes { get; init; } = 15;
    public double RtvThreshold { get; init; } = 0.60;
    public int MinimumRtvVotes { get; init; } = 1;
    public int VoteDurationSeconds { get; init; } = 60;
    public int CandidateCount { get; init; } = 5;
    public int RecentMapExclusionCount { get; init; } = 3;
    public int ExtendMapMinutes { get; init; } = 10;
    public int MaximumMapExtensions { get; init; } = 3;
    public bool ExcludeAfkPlayers { get; init; } = true;
    public int AfkAfterSeconds { get; init; } = 180;
    public bool AutomaticQuarantineEnabled { get; init; } = true;
    public int CompatibilityFailuresBeforeQuarantine { get; init; } = 2;
    public int MinimumTier { get; init; } = 1;
    public int MaximumTier { get; init; } = 2;
    public IReadOnlyList<MapPoolEntry> Maps { get; init; } =
    [
        new("surf_boreas", "3133346713"),
        new("surf_kitsune", "3076153623"),
        new("surf_mesa_revo", "3076980482"),
        new("surf_mom", "3282137145"),
        new("surf_prisma", "3319154265"),
        new("surf_cyka_ksf", "3263197243"),
        new("surf_elysium", "3147764666"),
        new("surf_goliath", "3448505317"),
        new("surf_mesa_aether", "3125360522"),
        new("surf_aquaflow", "3255589335"),
        new("surf_newbie", "3263974751"),
        new("surf_zeitgeist", "3265329080"),
        new("surf_jive", "3318285030"),
        new("surf_cannonball", "3152119098"),
        new("surf_sippysip", "3246776437"),
        new("surf_lt_omnific", "3660894345"),
        new("surf_summit", "3650611306"),
        new("surf_nyx", "3129698096"),
        new("surf_ace", "3088413071"),
        new("surf_me", "3248211716"),
        new("surf_beginner", "3070321829"),
        new("surf_whiteout", "3296258256"),
        new("surf_lux", "3280013431"),
        new("surf_utopia_njv", "3073875025"),
        new("surf_atrium", "3130141240"),
        new("surf_lullaby", "3271149992"),
        new("surf_rookie", "3082548297"),
        new("surf_benevolent", "3098972556"),
        new("surf_race", "3259650654"),
        new("surf_cyberwave", "3167714768"),
        new("surf_rookie2", "3617159050"),
        new("surf_minecraft_2020", "3420606769"),
        new("surf_slob", "3212207205"),
        new("surf_sandtrap2", "3303121302"),
        new("surf_dojo", "3287151811"),
        new("surf_paradise", "3391598488"),
        new("surf_longreach", "3105967738"),
        new("surf_6", "3306251447"),
        new("surf_1day", "3331512455"),
        new("surf_oompa_loompa", "3335810135"),
        new("surf_scarlet", "3423231772"),
        new("surf_quantum_njv", "3410482882"),
        new("surf_forbidden_tomb", "3332027866"),
        new("surf_overgrowth", "3333270287"),
        new("surf_ambient_njv", "3402014847"),
        new("surf_beginner_hell", "3430172550"),
        new("surf_tundra_v2", "3412847937"),
        new("surf_conserve", "3487164265"),
        new("surf_tycho_fix", "3425903417"),
        new("surf_innokia", "3435482464"),
        new("surf_castlewalls", "3437384087"),
        new("surf_nyze", "3534202802"),
        new("surf_syria_again", "3440106952"),
        new("surf_bugs", "3445707890"),
        new("surf_sinsane_ksf", "3446916582"),
        new("surf_shade", "3448016341"),
        new("surf_glow", "3454592243"),
        new("surf_diamond_beta1", "3460875224"),
        new("surf_grid", "3473073810"),
        new("surf_selenka", "3476698795"),
        new("surf_pyrism", "3573822509")
    ];
}

public sealed record MapPoolEntry(string Name, string WorkshopId);
