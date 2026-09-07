using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SurfTimer.Replays;

namespace SurfTimer.Storage;

public sealed class PendingRunSaveException(Exception inner) : Exception("Run is safely queued locally for database retry.", inner);

public sealed partial class RecordRepository
{
    private readonly SemaphoreSlim _pendingGate = new(1, 1);
    public event Action<ulong, string>? PendingRunRecovered;
    public event Action<ulong, string>? RecordsChanged;
    private readonly object _enqueueLock = new();
    private Task _lastDurableWrite = Task.CompletedTask;
    private long _queueSequence;
    private DateTimeOffset _retryAfter;
    private int _retryFailures;
    private string QuarantineDirectory => Path.Combine(PendingDirectory, "quarantine");

    public string PendingQueueStatus()
    {
        try
        {
            var files = Directory.Exists(PendingDirectory) ? new DirectoryInfo(PendingDirectory).GetFiles("*.json") : [];
            var quarantine = Directory.Exists(QuarantineDirectory) ? Directory.GetFiles(QuarantineDirectory, "*.json").Length : 0;
            var oldest = files.Length == 0 ? 0 : Math.Max(0, (DateTime.UtcNow - files.Min(f => f.LastWriteTimeUtc)).TotalSeconds);
            return $"pending={files.Length} bytes={files.Sum(f => f.Length)} oldestSeconds={oldest:F0} quarantine={quarantine} retryAfter={_retryAfter:u}";
        }
        catch (Exception exception) { return $"queue diagnostics unavailable: {exception.Message}"; }
    }

    private void NotifySaved(PendingRunEnvelope pending, bool recovered)
    {
        try
        {
            RecordsChanged?.Invoke(pending.SteamId, pending.MapName);
            if (recovered) PendingRunRecovered?.Invoke(pending.SteamId, pending.MapName);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Run save notification failed."); }
    }
    private string PendingDirectory => Path.Combine(core.PluginDataDirectory, "pending-runs");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _acceptedWrites = new();
    public Task<SaveRecordResult> SaveRunAsync(CompletedRun run) => SchedulePending(() => new(run with { Replay = null }, null,
        run.Replay is null ? null : ReplayCodec.Encode(run.Replay)));
    public Task<SaveRecordResult> SaveBonusAsync(CompletedBonusRun run) => SchedulePending(() => new(null, run with { Replay = null },
        run.Replay is null ? null : ReplayCodec.Encode(run.Replay)));

    private Task<SaveRecordResult> SchedulePending(Func<PendingRunEnvelope> create)
    {
        lock (_enqueueLock)
        {
            var writeId = Guid.NewGuid();
            var durable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = _lastDurableWrite;
            _lastDurableWrite = durable.Task;
            var sequence = ++_queueSequence;
            _acceptedWrites[writeId] = durable.Task;
            return Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    return await EnqueueAsync(create(), sequence, () => durable.TrySetResult()).ConfigureAwait(false);
                }
                finally { durable.TrySetResult(); _acceptedWrites.TryRemove(writeId, out _); }
            });
        }
    }
    private async Task<SaveRecordResult> EnqueueAsync(PendingRunEnvelope pending, long sequence, Action written)
    {
        Directory.CreateDirectory(PendingDirectory);
        var path = Path.Combine(PendingDirectory, $"{pending.FinishedAtUtc.UtcTicks:D19}-{sequence:D19}-{pending.Id:N}.json");
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, pending, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                stream.Flush(true);
            }
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        }
        written();
        // Disk durability precedes waiting for the database writer, including during an outage.
        try
        {
            await _pendingGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (DateTimeOffset.UtcNow < _retryAfter) throw new IOException("Database retry is backing off.");
                // Drain older durable submissions before a new finish can overtake them.
                var older = OrderedPendingFiles().TakeWhile(file => file != path).Take(33).ToArray();
                if (older.Length > 32) throw new IOException("Older queued runs are awaiting the next bounded recovery batch.");
                foreach (var file in older) await RecoverFileAsync(file).ConfigureAwait(false);
                SaveRecordResult result;
                try { result = await PersistPendingAsync(pending).ConfigureAwait(false); }
                catch (RulesetRejectedException exception)
                {
                    Quarantine(path, exception);
                    throw new RunQuarantinedException(exception);
                }
                File.Delete(path);
                _retryFailures = 0;
                NotifySaved(pending, false);
                return result;
            }
            finally { _pendingGate.Release(); }
        }
        catch (RunQuarantinedException) { throw; }
        catch (RulesetAwaitingApprovalException exception) { throw new PendingRunSaveException(exception); }
        catch (Exception exception)
        {
            if (DateTimeOffset.UtcNow >= _retryAfter)
                _retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(++_retryFailures - 1, 5))));
            throw new PendingRunSaveException(exception);
        }
    }
    private async Task<SaveRecordResult> PersistPendingAsync(PendingRunEnvelope pending)
    {
        var replay = pending.Replay is null ? null : ReplayCodec.Decode(pending.Replay);
        if (pending.Stage is { } stage) return await SaveStageDirectAsync(stage with { Replay = replay }).ConfigureAwait(false);
        if (pending.Main is { } main) return await SaveRunDirectAsync(main with { Replay = replay }).ConfigureAwait(false);
        await ReadyAsync().ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            try { var result = await SaveBonusAttemptAsync(pending.Bonus! with { Replay = replay }).ConfigureAwait(false); MarkSuccess(); return result; }
            catch (Exception exception) when (attempt < 4 && IsTransientWriteFailure(exception))
            { MarkFailure(); await Task.Delay(25 * (int)Math.Pow(3, attempt - 1), _shutdown.Token).ConfigureAwait(false); }
        }
    }

    private IEnumerable<string> OrderedPendingFiles() => Directory.Exists(PendingDirectory)
        ? Directory.EnumerateFiles(PendingDirectory, "*.json").OrderBy(path =>
            long.TryParse(Path.GetFileName(path).Split('-')[0], out var ticks) ? ticks : File.GetLastWriteTimeUtc(path).Ticks)
            .ThenBy(path => path, StringComparer.Ordinal)
        : [];

    private void Quarantine(string path, Exception exception)
    {
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(QuarantineDirectory);
        var target = Path.Combine(QuarantineDirectory, Path.GetFileName(path));
        File.Move(path, target, overwrite: false);
        logger.LogError(exception, "Run evidence quarantined at {File}; administrator review required.", target);
    }

    private async Task RecoverFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path, _shutdown.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var pending = JsonSerializer.Deserialize<PendingRunEnvelope>(json) ?? throw new InvalidDataException("Empty pending run.");
            if ((pending.Main is null ? 0 : 1) + (pending.Bonus is null ? 0 : 1) + (pending.Stage is null ? 0 : 1) != 1)
                throw new InvalidDataException("Pending run must contain exactly one route.");
            // Old queue envelopes did not capture finish time. Preserve their file timestamp as an approximation.
            var route = document.RootElement.GetProperty(pending.Main is not null ? "Main" : pending.Bonus is not null ? "Bonus" : "Stage");
            if (!route.TryGetProperty("FinishedAtUtc", out _))
            {
                var finished = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                pending = pending with {
                    Main = pending.Main is { } main ? main with { FinishedAtUtc = finished } : null,
                    Bonus = pending.Bonus is { } bonus ? bonus with { FinishedAtUtc = finished } : null,
                    Stage = pending.Stage is { } stage ? stage with { FinishedAtUtc = finished } : null
                };
            }
            await PersistPendingAsync(pending).ConfigureAwait(false);
            File.Delete(path);
            NotifySaved(pending, true);
        }
        // Approval is a map-specific dependency, not a database outage. Keep checking without
        // quarantining the run or preventing other maps from recovering in this batch.
        catch (RulesetAwaitingApprovalException) { }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or RulesetRejectedException)
        { Quarantine(path, exception); }
    }

    private async Task RecoverPendingRunsAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _shutdown.Token).ConfigureAwait(false);
                await _pendingGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    if (DateTimeOffset.UtcNow < _retryAfter) continue;
                    foreach (var path in OrderedPendingFiles().Take(32).ToArray())
                        await RecoverFileAsync(path).ConfigureAwait(false);
                    _retryFailures = 0;
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    _retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(++_retryFailures - 1, 5))));
                    logger.LogWarning(exception, "Pending recovery paused until {RetryAfter}; evidence remains queued.", _retryAfter);
                }
                finally { _pendingGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<SaveRecordResult?> ReadReceiptAsync(DbConnection c, DbTransaction t, Guid id)
    {
        // Insert first to serialize even concurrent deliveries of the same run from different processes.
        await using (var claim = c.CreateCommand())
        {
            claim.Transaction = t;
            claim.CommandText = "INSERT INTO st_run_receipts(run_id,result_json,created_at) VALUES(@id,NULL,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE run_id=run_id";
            claim.AddParameter("@id", id.ToString("N"));
            await claim.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT result_json FROM st_run_receipts WHERE run_id=@id FOR UPDATE";
        command.AddParameter("@id", id.ToString("N"));
        var value = await command.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false);
        return value is null or DBNull ? null : JsonSerializer.Deserialize<SaveRecordResult>(Convert.ToString(value)!);
    }

    private async Task<bool> IsCatalogAuthorityAsync(DbConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(options.CatalogAuthorityServerId) && options.CatalogAuthorityServerId != options.ServerId) return false;
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO st_catalog_authority(id,server_id) VALUES(1,@server) ON DUPLICATE KEY UPDATE id=id; SELECT server_id FROM st_catalog_authority WHERE id=1";
        command.AddParameter("@server", options.ServerId);
        return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false)), options.ServerId, StringComparison.Ordinal);
    }

    private async Task SaveReceiptAsync(DbConnection c, DbTransaction t, Guid id, SaveRecordResult result, string fingerprint, ulong steamId, string mapName, string route, int routeIndex)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "UPDATE st_run_receipts SET result_json=@result,ruleset_fingerprint=@fingerprint,player_steam_id=@steam,map_name=@map,route_type=@route,route_index=@index WHERE run_id=@id";
        command.AddParameter("@id", id.ToString("N")); command.AddParameter("@result", JsonSerializer.Serialize(result));
        command.AddParameter("@fingerprint", fingerprint);
        command.AddParameter("@steam", steamId); command.AddParameter("@map", mapName);
        command.AddParameter("@route", route); command.AddParameter("@index", routeIndex);
        await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
    }

    private static async Task DeleteReplayAsync(DbConnection c, DbTransaction t, long id, bool stage, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = stage ? "DELETE FROM st_stage_replays WHERE stage_record_id=@id" : "DELETE FROM st_replays WHERE record_id=@id";
        command.AddParameter("@id", id);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}





