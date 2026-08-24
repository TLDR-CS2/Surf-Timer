namespace SurfTimer.Configuration;

public sealed class SurfTimerOptions
{
    public const string SectionName = "SurfTimer";

    public bool Enabled { get; init; } = true;

    public bool DebugLogging { get; init; }

    public string DatabaseConnection { get; init; } = "surftimer";

    public string ServerId { get; init; } = "change-me";

    public int HudRefreshRateHz { get; init; } = 64;

    public MapVotingOptions MapVoting { get; init; } = new();
}

public sealed class MapVotingOptions
{
    public bool Enabled { get; init; } = true;
    public bool RockTheVoteEnabled { get; init; } = true;
    public bool NominationEnabled { get; init; } = true;
    public bool EndOfMapVoteEnabled { get; init; } = false;
    public double RtvThreshold { get; init; } = 0.60;
    public int MinimumRtvVotes { get; init; } = 1;
    public int VoteDurationSeconds { get; init; } = 60;
    public int CandidateCount { get; init; } = 5;
    public int RecentMapExclusionCount { get; init; } = 3;
    public int ExtendMapMinutes { get; init; } = 10;
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
        new("surf_lt_omnific", "3660894345")
    ];
}

public sealed record MapPoolEntry(string Name, string WorkshopId);
