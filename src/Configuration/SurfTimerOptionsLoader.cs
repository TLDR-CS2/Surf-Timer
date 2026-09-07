using System.Text.Json;
using System.Text.RegularExpressions;
using SwiftlyS2.Shared;

namespace SurfTimer.Configuration;

internal static class SurfTimerOptionsLoader
{
    public static SurfTimerOptions Load(ISwiftlyCore core)
    {
        var path = core.Configuration.GetConfigPath("config.jsonc");
        if (!File.Exists(path))
            throw new FileNotFoundException("SurfTimer config.jsonc was not created.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (!document.RootElement.TryGetProperty(SurfTimerOptions.SectionName, out var section))
            return new SurfTimerOptions();

        var options = section.Deserialize<SurfTimerOptions>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new SurfTimerOptions();
        Validate(options);
        return options;
    }

    private static void Validate(SurfTimerOptions options)
    {
        if (!options.Enabled) return;
        if (options.MaximumReplayMinutes is < 1 or > 120)
            throw new InvalidDataException("SurfTimer.MaximumReplayMinutes must be between 1 and 120.");
        if (!string.IsNullOrEmpty(options.CatalogAuthorityServerId) &&
            !Regex.IsMatch(options.CatalogAuthorityServerId, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("SurfTimer.CatalogAuthorityServerId must be empty or a valid server ID.");
        if (string.IsNullOrWhiteSpace(options.DatabaseConnection))
            throw new InvalidDataException("SurfTimer.DatabaseConnection must not be empty.");
        if (string.IsNullOrWhiteSpace(options.ServerId) ||
            options.ServerId.Equals("change-me", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(options.ServerId, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(
                "SurfTimer.ServerId must be a unique 1-64 character lowercase ID using letters, numbers, '.', '_' or '-'.");
        var voting = options.MapVoting;
        if (voting.RtvThreshold is <= 0 or > 1)
            throw new InvalidDataException("SurfTimer.MapVoting.RtvThreshold must be greater than 0 and at most 1.");
        if (voting.MinimumRtvVotes < 1 || voting.VoteDurationSeconds is < 5 or > 120 ||
            voting.CandidateCount is < 2 or > 5 || voting.RecentMapExclusionCount is < 0 or > 20 ||
            voting.ExtendMapMinutes is < 1 or > 120 || voting.MaximumMapExtensions is < 0 or > 20 ||
            voting.ForceVoteAfterMinutes is < 1 or > 240 || voting.AfkAfterSeconds is < 30 or > 3600 ||
            voting.CompatibilityFailuresBeforeQuarantine is < 1 or > 10)
            throw new InvalidDataException("SurfTimer.MapVoting contains an out-of-range numeric setting.");
        if (voting.MinimumTier is < 1 or > 7 || voting.MaximumTier is < 1 or > 7 ||
            voting.MinimumTier > voting.MaximumTier)
            throw new InvalidDataException("SurfTimer.MapVoting tier range must be between 1 and 7 with minimum <= maximum.");
        if (voting.Maps.Any(map => !Regex.IsMatch(map.Name, "^[a-zA-Z0-9_]+$") ||
                                   !Regex.IsMatch(map.WorkshopId, "^[0-9]{1,20}$")))
            throw new InvalidDataException("SurfTimer.MapVoting.Maps contains an unsafe map name or Workshop ID.");
        var duplicateName = voting.Maps.GroupBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
            throw new InvalidDataException($"SurfTimer.MapVoting.Maps contains duplicate map name '{duplicateName}'.");
        var duplicateWorkshop = voting.Maps.GroupBy(map => map.WorkshopId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateWorkshop is not null)
            throw new InvalidDataException($"SurfTimer.MapVoting.Maps contains duplicate Workshop ID '{duplicateWorkshop}'.");
        if (voting.Enabled && voting.Maps.Count < voting.CandidateCount)
            throw new InvalidDataException("SurfTimer.MapVoting.Maps must contain at least CandidateCount entries when map voting is enabled.");
        if (options.Titles.MaximumCustomLength is < 1 or > 10)
            throw new InvalidDataException("SurfTimer.Titles.MaximumCustomLength must be between 1 and 10.");
        var zones = options.ZoneVisibility;
        if (zones.Width is < 0.25f or > 16f || zones.Red is < 0 or > 255 || zones.Green is < 0 or > 255 ||
            zones.Blue is < 0 or > 255 || zones.Alpha is < 0 or > 255 ||
            zones.Maps.Any(map => !Regex.IsMatch(map, "^[a-zA-Z0-9_]+$")))
            throw new InvalidDataException("SurfTimer.ZoneVisibility contains an invalid colour, width, or map name.");
        if (options.SplitComparisons.HudDurationSeconds is < 1 or > 15 ||
            options.SplitComparisons.Reference is not ("PB" or "WR" or "Both" or "Off"))
            throw new InvalidDataException("SurfTimer.SplitComparisons requires Reference PB, WR, Both, or Off and duration 1-15 seconds.");
    }
}
