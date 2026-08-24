namespace SurfTimer.Storage;

public sealed record MapPointsBreakdown(string? Group, double Percentile, long Points);

public static class SurfPointsPolicy
{
    public const int BonusRoutePoints = 10;
    public const int PortfolioMapsPerTier = 20;

    public static MapPointsBreakdown ForMainMap(int tier, int rank, int totalRecords)
    {
        var percentile = totalRecords <= 0 ? 1d : (double)(rank - 1) / totalRecords;
        var group = percentile switch
        {
            < .01 => "Group 1", < .05 => "Group 2", < .10 => "Group 3",
            < .25 => "Group 4", < .50 => "Group 5", _ => null
        };
        var basePoints = 25L << Math.Clamp(tier - 1, 0, 6);
        var placementMultiplier = PlacementMultiplier(rank);
        return new MapPointsBreakdown(group, percentile,
            basePoints + (long)Math.Round(basePoints * 10d * placementMultiplier, MidpointRounding.AwayFromZero));
    }

    public static double PlacementMultiplier(int rank) => rank switch
    {
        <= 0 => 0d,
        1 => 1d,
        <= 10 => .85d - .05d * (rank - 1),
        <= 100 => 4d / rank,
        _ => 0d
    };
}
