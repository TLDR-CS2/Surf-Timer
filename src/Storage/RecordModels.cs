using SurfTimer.Replays;
using SurfTimer.Timing;

namespace SurfTimer.Storage;

public sealed record CompletedRun(
    ulong SteamId,
    string PlayerName,
    string MapName,
    string? WorkshopId,
    int CheckpointCount,
    long TimeMicroseconds,
    IReadOnlyList<long> CheckpointSplits,
    IReadOnlyList<long> StageTimes,
    string ServerId,
    ReplayCapture? Replay,
    RunTelemetry Telemetry);

public sealed record CompletedBonusRun(
    ulong SteamId,
    string PlayerName,
    string MapName,
    string? WorkshopId,
    int Bonus,
    long TimeMicroseconds,
    string ServerId,
    ReplayCapture? Replay,
    RunTelemetry Telemetry);

public sealed record SaveRecordResult(
    bool IsPersonalBest,
    long? PreviousBestMicroseconds,
    long BestMicroseconds,
    int Rank,
    IReadOnlyList<StageRecordResult> Stages);

public sealed record LeaderboardEntry(
    int Rank,
    ulong SteamId,
    string PlayerName,
    long TimeMicroseconds,
    int Completions);

public sealed record PersonalBest(long TimeMicroseconds, int Rank, int Completions);

public sealed record StageRecordResult(
    int Stage,
    bool IsPersonalBest,
    long? PreviousBestMicroseconds,
    long BestMicroseconds,
    int Rank);

public sealed record StagePersonalBest(long TimeMicroseconds, int Rank, int TotalRecords, int Completions);

public sealed record RecordSplit(int Checkpoint, long TimeMicroseconds);

public sealed record PersonalBestDetails(
    long TimeMicroseconds,
    int Rank,
    int TotalRecords,
    int Completions,
    IReadOnlyList<RecordSplit> Splits);

public sealed record DatabaseHealth(
    bool IsHealthy,
    long LatencyMilliseconds,
    string ServerId,
    string ConnectionName,
    string Message);

public sealed record PlayerIdentity(ulong SteamId, string PlayerName);

public sealed record MapRecordSummary(int RecordCount, LeaderboardEntry? WorldRecord);

public sealed record AdminPlayerDetails(
    ulong SteamId,
    string PlayerName,
    DateTime FirstSeen,
    DateTime LastSeen,
    uint Connections,
    int Records);

public sealed record DeletedPersonalBest(
    ulong SteamId,
    string PlayerName,
    string MapName,
    long TimeMicroseconds,
    int Completions);

public sealed record ReplayAdminDetails(
    int Rank,
    ulong SteamId,
    string PlayerName,
    string MapName,
    long TimeMicroseconds,
    int FormatVersion,
    int SampleRateHz,
    int FrameCount,
    long DurationMicroseconds,
    int CompressedBytes);

public sealed record RecordValidationDetails(
    int Rank,
    string PlayerName,
    long TimeMicroseconds,
    int ValidationVersion,
    double MaximumSpeed,
    int OverspeedSamples,
    double MaximumFrameDistance,
    int PositionJumpCount,
    string Flags,
    DateTime AnalyzedAt);

public sealed record RecentPersonalBest(string MapName, string RouteType, int RouteIndex, long TimeMicroseconds, DateTime AchievedAt);

public sealed record GlobalPlayerProfile(
    ulong SteamId, string PlayerName, DateTime FirstSeen, DateTime LastSeen, uint Connections,
    long Completions, int UniqueMaps, int MainRecords, int BonusRecords, int StageRecords,
    int Replays, int WorldRecords, string? MostPlayedMap, long TrackedCompletions,
    long TrackedTimeMicroseconds, DateTime? TrackingStartedAt, IReadOnlyList<RecentPersonalBest> RecentPersonalBests);

public sealed record GlobalMapStatistics(
    string MapName, long Completions, int UniquePlayers, long? WorldRecordMicroseconds,
    string? WorldRecordPlayer, long? AveragePersonalBestMicroseconds, long? MedianPersonalBestMicroseconds);

public sealed record OverallRanking(
    int Rank, ulong SteamId, string PlayerName, long Points, int CompletedMaps,
    int Group1, int Group2, int Group3, int Group4, int Group5,
    long MapPoints, long StagePoints, long BonusPoints, string Title);
