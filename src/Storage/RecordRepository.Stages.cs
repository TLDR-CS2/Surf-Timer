using Microsoft.Extensions.Logging;

namespace SurfTimer.Storage;

public sealed partial class RecordRepository
{
    public Task<SaveRecordResult> SaveStageAsync(CompletedStageRun run) => SchedulePending(() => new(null, null,
        run.Replay is null ? null : Replays.ReplayCodec.Encode(run.Replay), run with { Replay = null }));

    private async Task<SaveRecordResult> SaveStageDirectAsync(CompletedStageRun run)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            var saved = await ReadReceiptAsync(connection, transaction, run.RunId).ConfigureAwait(false);
            if (saved is not null) return saved;
            if (!run.CompetitiveStartCertified)
                throw new RulesetRejectedException("Independent stage start was not certified; evidence requires review.");
            await RequireRulesetAsync(connection, transaction, run.MapName, run.RulesetFingerprint).ConfigureAwait(false);
            var main = new CompletedRun(run.SteamId, run.PlayerName, run.MapName, run.WorkshopId, 0,
                run.TimeMicroseconds, [], [], run.ServerId, run.Replay, run.Telemetry) { RunId = run.RunId, FinishedAtUtc = run.FinishedAtUtc, RulesetFingerprint = run.RulesetFingerprint };
            await UpsertPlayerSeenAsync(connection, transaction, main, _shutdown.Token).ConfigureAwait(false);
            await UpsertMapAsync(connection, transaction, run.MapName, run.WorkshopId, 0, _shutdown.Token).ConfigureAwait(false);
            var mapId = await GetMapIdAsync(connection, transaction, run.MapName, _shutdown.Token).ConfigureAwait(false);
            var stage = await UpsertStageRecordAsync(connection, transaction, mapId, main, run.Stage, run.TimeMicroseconds, run.Replay, _shutdown.Token).ConfigureAwait(false);
            var result = new SaveRecordResult(stage.IsPersonalBest, stage.PreviousBestMicroseconds, stage.BestMicroseconds, stage.Rank, [stage]);
            await SaveReceiptAsync(connection, transaction, run.RunId, result, run.RulesetFingerprint, run.SteamId, run.MapName, "stage", run.Stage).ConfigureAwait(false);
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            MarkSuccess();
            return result;
        }
        catch (Exception exception)
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (exception is not RulesetAwaitingApprovalException) MarkFailure();
            throw;
        }
    }
}




