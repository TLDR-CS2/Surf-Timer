using SurfTimer.Replays;

namespace SurfTimer.Storage;

internal sealed record PendingRunEnvelope(CompletedRun? Main, CompletedBonusRun? Bonus, EncodedReplay? Replay, CompletedStageRun? Stage = null)
{
    public Guid Id => Main?.RunId ?? Bonus?.RunId ?? Stage!.RunId;
    public DateTimeOffset FinishedAtUtc => Main?.FinishedAtUtc ?? Bonus?.FinishedAtUtc ?? Stage!.FinishedAtUtc;
    public ulong SteamId => Main?.SteamId ?? Bonus?.SteamId ?? Stage!.SteamId;
    public string MapName => Main?.MapName ?? Bonus?.MapName ?? Stage!.MapName;
}
