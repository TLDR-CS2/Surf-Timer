using System.Data.Common;
using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SwiftlyS2.Shared;
using SurfTimer.Replays;
using SurfTimer.Timing;

namespace SurfTimer.Storage;

public sealed class RecordRepository(
    ISwiftlyCore core,
    SurfTimerOptions options,
    MigrationRunner migrations,
    ILogger<RecordRepository> logger) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _initializationSync = new();
    private Task? _initialization;
    private const string OverallRankingCte = """
        WITH map_rankings AS (
          SELECT r.player_steam_id,r.map_id,m.tier,
            RANK() OVER(PARTITION BY r.map_id ORDER BY r.best_time_us) rank_no,
            COUNT(*) OVER(PARTITION BY r.map_id) total_records
          FROM st_records r JOIN st_maps m ON m.id=r.map_id
          WHERE r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf' AND m.enabled=1
        ), main_scored AS (
          SELECT *,ROUND((25*POW(2,tier-1))*(1+10*CASE
            WHEN rank_no=1 THEN 1 WHEN rank_no<=10 THEN .85-.05*(rank_no-1)
            WHEN rank_no<=100 THEN 4/rank_no ELSE 0 END)) route_points
          FROM map_rankings
        ), portfolio_ranked AS (
          SELECT *,ROW_NUMBER() OVER(PARTITION BY player_steam_id,tier ORDER BY route_points DESC,map_id) portfolio_position
          FROM main_scored
        ), main_scores AS (
          SELECT player_steam_id,
            SUM(CASE WHEN portfolio_position<=20 THEN route_points ELSE 0 END) map_points,
            COUNT(*) completed_maps,
            SUM((rank_no-1)/total_records < 0.01) group1,
            SUM((rank_no-1)/total_records >= 0.01 AND (rank_no-1)/total_records < 0.05) group2,
            SUM((rank_no-1)/total_records >= 0.05 AND (rank_no-1)/total_records < 0.10) group3,
            SUM((rank_no-1)/total_records >= 0.10 AND (rank_no-1)/total_records < 0.25) group4,
            SUM((rank_no-1)/total_records >= 0.25 AND (rank_no-1)/total_records < 0.50) group5
          FROM portfolio_ranked GROUP BY player_steam_id
        ), stage_rankings AS (
          SELECT sr.player_steam_id,sr.map_id,m.tier,GREATEST(m.stage_count,1) stage_count,
            RANK() OVER(PARTITION BY sr.map_id,sr.stage ORDER BY sr.best_time_us) rank_no
          FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id
          WHERE m.enabled=1
        ), stage_scores AS (
          SELECT player_steam_id,map_id,ROUND(SUM(((25*POW(2,tier-1))*10*.35/stage_count)*CASE
            WHEN rank_no=1 THEN 1 WHEN rank_no<=10 THEN .85-.05*(rank_no-1)
            WHEN rank_no<=100 THEN 4/rank_no ELSE 0 END)) stage_points
          FROM stage_rankings GROUP BY player_steam_id,map_id
        ), counted_stage_scores AS (
          SELECT ss.player_steam_id,SUM(ss.stage_points) stage_points FROM stage_scores ss
          JOIN portfolio_ranked p ON p.player_steam_id=ss.player_steam_id AND p.map_id=ss.map_id AND p.portfolio_position<=20
          GROUP BY ss.player_steam_id
        ), bonus_scores AS (
          SELECT r.player_steam_id,COUNT(*)*10 bonus_points
          FROM st_records r JOIN st_maps m ON m.id=r.map_id
          WHERE r.route_type='bonus' AND r.style=0 AND r.mode='surf' AND m.enabled=1
          GROUP BY r.player_steam_id
        ), score_players AS (
          SELECT player_steam_id FROM main_scores UNION SELECT player_steam_id FROM counted_stage_scores
          UNION SELECT player_steam_id FROM bonus_scores
        ), scores AS (
          SELECT p.player_steam_id,
            COALESCE(m.map_points,0)+COALESCE(st.stage_points,0)+COALESCE(b.bonus_points,0) points,
            COALESCE(m.completed_maps,0) completed_maps,
            COALESCE(m.group1,0) group1,COALESCE(m.group2,0) group2,COALESCE(m.group3,0) group3,
            COALESCE(m.group4,0) group4,COALESCE(m.group5,0) group5,
            COALESCE(m.map_points,0) map_points,COALESCE(st.stage_points,0) stage_points,COALESCE(b.bonus_points,0) bonus_points
          FROM score_players p LEFT JOIN main_scores m ON m.player_steam_id=p.player_steam_id
          LEFT JOIN counted_stage_scores st ON st.player_steam_id=p.player_steam_id
          LEFT JOIN bonus_scores b ON b.player_steam_id=p.player_steam_id
        ), ranked AS (
          SELECT s.*,RANK() OVER(ORDER BY s.points DESC) overall_rank,COUNT(*) OVER() ranked_players FROM scores s
        ), overall AS (
          SELECT r.*,CASE WHEN completed_maps<5 THEN 'Provisional'
            WHEN overall_rank=1 AND completed_maps>=25 THEN 'God'
            WHEN (overall_rank-1)/ranked_players<.005 THEN 'Legend'
            WHEN (overall_rank-1)/ranked_players<.01 THEN 'Master'
            WHEN (overall_rank-1)/ranked_players<.05 THEN 'Elite'
            WHEN (overall_rank-1)/ranked_players<.10 THEN 'Expert'
            WHEN (overall_rank-1)/ranked_players<.25 THEN 'Veteran'
            WHEN (overall_rank-1)/ranked_players<.50 THEN 'Skilled' ELSE 'Surfer' END title
          FROM ranked r
        )
        """;
    public string Status { get; private set; } = "not-started";
    public DateTimeOffset? LastSuccessfulOperationAt { get; private set; }
    public DateTimeOffset? LastFailedOperationAt { get; private set; }
    public int ConsecutiveFailures { get; private set; }

    public void Start()
    {
        lock (_initializationSync)
        {
            if (_initialization is not null) return;
            Status = "migrating";
            _initialization = Task.Run(async () =>
            {
                try
                {
                    await migrations.ApplyAsync(_shutdown.Token).ConfigureAwait(false);
                    MarkSuccess();
                    logger.LogInformation("Record repository ready on connection {ConnectionName} for server {ServerId}.",
                        options.DatabaseConnection, options.ServerId);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    Status = "faulted";
                    MarkFailure();
                    logger.LogError(exception, "SurfTimer database initialization failed.");
                    throw;
                }
            });
        }
    }

    public async Task UpsertPlayerConnectionAsync(ulong steamId, string name)
    {
        try
        {
            await ReadyAsync().ConfigureAwait(false);
            await using var connection = await OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO st_players
                    (steam_id, last_name, first_seen_at, last_seen_at, first_server_id, last_server_id, total_connections)
                VALUES (@steam, @name, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), @server, @server, 1)
                ON DUPLICATE KEY UPDATE
                    last_name = VALUES(last_name), last_seen_at = UTC_TIMESTAMP(6),
                    last_server_id = VALUES(last_server_id), total_connections = total_connections + 1
                """;
            command.AddParameter("@steam", steamId);
            command.AddParameter("@name", name);
            command.AddParameter("@server", options.ServerId);
            await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to persist player {SteamId}.", steamId);
        }
    }

    public async Task UpsertMapAsync(string name, string? workshopId, int checkpointCount)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await UpsertMapAsync(connection, null, name, workshopId, checkpointCount, _shutdown.Token).ConfigureAwait(false);
    }

    public async Task TrackMapAsync(string name, string? workshopId, int checkpointCount)
    {
        try
        {
            await UpsertMapAsync(name, workshopId, checkpointCount).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to persist map {MapName}.", name);
        }
    }

    public async Task TrackMapMetadataAsync(
        string name, string? workshopId, int checkpointCount, int stageCount, int bonusCount, int tier, bool enabled)
    {
        try
        {
            await ReadyAsync().ConfigureAwait(false);
            await using var connection = await OpenAsync().ConfigureAwait(false);
            await UpsertMapAsync(connection, null, name, workshopId, checkpointCount, _shutdown.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE st_maps SET tier=@tier,enabled=@enabled,checkpoint_count=@checkpoints,stage_count=@stages,bonus_count=@bonuses,updated_at=UTC_TIMESTAMP(6) WHERE name=@name";
            command.AddParameter("@tier", Math.Clamp(tier, 1, 7));
            command.AddParameter("@enabled", enabled ? 1 : 0);
            command.AddParameter("@checkpoints", checkpointCount);
            command.AddParameter("@stages", stageCount);
            command.AddParameter("@bonuses", bonusCount);
            command.AddParameter("@name", name);
            await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to persist metadata for map {MapName}.", name);
        }
    }

    public async Task<SaveRecordResult> SaveRunAsync(CompletedRun run)
    {
        await ReadyAsync().ConfigureAwait(false);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var result = await SaveRunAttemptAsync(run).ConfigureAwait(false);
                MarkSuccess();
                return result;
            }
            catch (Exception exception) when (IsTransientWriteFailure(exception) && attempt < 4)
            {
                lastFailure = exception;
                MarkFailure();
                var delay = TimeSpan.FromMilliseconds(25 * Math.Pow(3, attempt - 1));
                logger.LogWarning(exception,
                    "Transient global PB write failure for {SteamId} on {Map}; retry {NextAttempt}/4 in {DelayMs}ms.",
                    run.SteamId, run.MapName, attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
            }
            catch
            {
                MarkFailure();
                throw;
            }
        }
        throw lastFailure ?? new InvalidOperationException("Global PB write failed without an exception.");
    }

    public async Task<SaveRecordResult> SaveBonusAsync(CompletedBonusRun run)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await using (var player = connection.CreateCommand())
            {
                player.Transaction = transaction;
                player.CommandText = """
                    INSERT INTO st_players (steam_id,last_name,first_seen_at,last_seen_at,first_server_id,last_server_id,total_connections)
                    VALUES (@steam,@name,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@server,@server,1)
                    ON DUPLICATE KEY UPDATE last_name=VALUES(last_name),last_seen_at=UTC_TIMESTAMP(6),last_server_id=VALUES(last_server_id)
                    """;
                player.AddParameter("@steam", run.SteamId); player.AddParameter("@name", run.PlayerName);
                player.AddParameter("@server", run.ServerId);
                await player.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
            }
            await UpsertMapAsync(connection, transaction, run.MapName, run.WorkshopId, 0, _shutdown.Token).ConfigureAwait(false);
            var mapId = await GetMapIdAsync(connection, transaction, run.MapName, _shutdown.Token).ConfigureAwait(false);
            long? id = null; long? previous = null;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id,best_time_us FROM st_records WHERE map_id=@map AND player_steam_id=@steam AND route_type='bonus' AND route_index=@bonus AND style=0 AND mode='surf' FOR UPDATE";
                select.AddParameter("@map", mapId); select.AddParameter("@steam", run.SteamId); select.AddParameter("@bonus", run.Bonus);
                await using var reader = await select.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
                if (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                { id = Convert.ToInt64(reader.GetValue(0)); previous = Convert.ToInt64(reader.GetValue(1)); }
            }
            var isPb = previous is null || run.TimeMicroseconds < previous;
            if (id is null)
            {
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO st_records (map_id,player_steam_id,route_type,route_index,style,mode,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id)
                    VALUES (@map,@steam,'bonus',@bonus,0,'surf',@time,1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@server);
                    SELECT LAST_INSERT_ID()
                    """;
                insert.AddParameter("@map", mapId); insert.AddParameter("@steam", run.SteamId); insert.AddParameter("@bonus", run.Bonus);
                insert.AddParameter("@time", run.TimeMicroseconds); insert.AddParameter("@server", run.ServerId);
                id = Convert.ToInt64(await insert.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false));
            }
            else
            {
                await using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE st_records SET completions=completions+1,last_completed_at=UTC_TIMESTAMP(6),last_server_id=@server,best_time_us=IF(@pb=1,@time,best_time_us),pb_updated_at=IF(@pb=1,UTC_TIMESTAMP(6),pb_updated_at) WHERE id=@id";
                update.AddParameter("@server", run.ServerId); update.AddParameter("@pb", isPb ? 1 : 0);
                update.AddParameter("@time", run.TimeMicroseconds); update.AddParameter("@id", id.Value);
                await update.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
            }
            if (isPb && run.Replay is not null)
                await ReplaceReplayAsync(connection, transaction, id.Value, ReplayCodec.Encode(run.Replay), _shutdown.Token).ConfigureAwait(false);
            if (isPb)
                await ReplaceValidationAsync(connection, transaction, id.Value, run.Telemetry, _shutdown.Token).ConfigureAwait(false);
            await TrackCompletedRunAsync(connection, transaction, run.SteamId, run.TimeMicroseconds, _shutdown.Token).ConfigureAwait(false);
            if (isPb)
                await AppendPbHistoryAsync(connection, transaction, id.Value, mapId, run.SteamId, "bonus", run.Bonus,
                    previous, run.TimeMicroseconds, _shutdown.Token).ConfigureAwait(false);
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            var best = isPb ? run.TimeMicroseconds : previous!.Value;
            await using var rank = connection.CreateCommand();
            rank.CommandText = "SELECT 1+COUNT(*) FROM st_records WHERE map_id=@map AND route_type='bonus' AND route_index=@bonus AND style=0 AND mode='surf' AND best_time_us<@best";
            rank.AddParameter("@map", mapId); rank.AddParameter("@bonus", run.Bonus); rank.AddParameter("@best", best);
            MarkSuccess();
            return new SaveRecordResult(isPb, previous, best,
                Convert.ToInt32(await rank.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false)), []);
        }
        catch { await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false); MarkFailure(); throw; }
    }

    public async Task<StagePersonalBest?> GetBonusPersonalBestAsync(ulong steamId, string mapName, int bonus)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.best_time_us,r.completions,
              1+(SELECT COUNT(*) FROM st_records f WHERE f.map_id=r.map_id AND f.route_type='bonus' AND f.route_index=r.route_index AND f.style=0 AND f.mode='surf' AND f.best_time_us<r.best_time_us),
              (SELECT COUNT(*) FROM st_records t WHERE t.map_id=r.map_id AND t.route_type='bonus' AND t.route_index=r.route_index AND t.style=0 AND t.mode='surf')
            FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.player_steam_id=@steam AND r.route_type='bonus' AND r.route_index=@bonus AND r.style=0 AND r.mode='surf'
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@steam", steamId); command.AddParameter("@bonus", bonus);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new StagePersonalBest(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(2)),
            Convert.ToInt32(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(1)));
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetBonusTopAsync(string mapName, int bonus, int limit = 10)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.player_steam_id,p.last_name,r.best_time_us,r.completions
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            WHERE m.name=@map AND r.route_type='bonus' AND r.route_index=@bonus AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT @limit
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@bonus", bonus); command.AddParameter("@limit", Math.Clamp(limit, 1, 100));
        var entries = new List<LeaderboardEntry>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            entries.Add(new LeaderboardEntry(entries.Count + 1, Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        return entries;
    }

    private async Task<SaveRecordResult> SaveRunAttemptAsync(CompletedRun run)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await UpsertPlayerSeenAsync(connection, transaction, run, _shutdown.Token).ConfigureAwait(false);
            await UpsertMapAsync(connection, transaction, run.MapName, run.WorkshopId, run.CheckpointCount, _shutdown.Token).ConfigureAwait(false);
            var mapId = await GetMapIdAsync(connection, transaction, run.MapName, _shutdown.Token).ConfigureAwait(false);

            var existing = await GetRecordForUpdateAsync(connection, transaction, mapId, run.SteamId, _shutdown.Token).ConfigureAwait(false);
            var isPb = existing is null || run.TimeMicroseconds < existing.Value.Best;
            long recordId;
            long best;

            if (existing is null)
            {
                recordId = await InsertRecordAsync(connection, transaction, mapId, run, _shutdown.Token).ConfigureAwait(false);
                best = run.TimeMicroseconds;
            }
            else
            {
                recordId = existing.Value.Id;
                best = isPb ? run.TimeMicroseconds : existing.Value.Best;
                await UpdateRecordAsync(connection, transaction, recordId, run, isPb, _shutdown.Token).ConfigureAwait(false);
            }

            if (isPb)
            {
                await ReplaceSplitsAsync(connection, transaction, recordId, run.CheckpointSplits, _shutdown.Token).ConfigureAwait(false);
                if (run.Replay is not null)
                    await ReplaceReplayAsync(connection, transaction, recordId, ReplayCodec.Encode(run.Replay), _shutdown.Token).ConfigureAwait(false);
                await ReplaceValidationAsync(connection, transaction, recordId, run.Telemetry, _shutdown.Token).ConfigureAwait(false);
            }
            await TrackCompletedRunAsync(connection, transaction, run.SteamId, run.TimeMicroseconds, _shutdown.Token).ConfigureAwait(false);
            if (isPb)
                await AppendPbHistoryAsync(connection, transaction, recordId, mapId, run.SteamId, "main", 0,
                    existing?.Best, run.TimeMicroseconds, _shutdown.Token).ConfigureAwait(false);
            var stageResults = new List<StageRecordResult>(run.StageTimes.Count);
            long stageStart = 0;
            for (var index = 0; index < run.StageTimes.Count; index++)
            {
                var stageTime = run.StageTimes[index];
                var stageReplay = SliceReplay(run.Replay, stageStart, stageStart + stageTime);
                stageResults.Add(await UpsertStageRecordAsync(connection, transaction, mapId, run, index + 1,
                    stageTime, stageReplay, _shutdown.Token).ConfigureAwait(false));
                stageStart += stageTime;
            }
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            var rank = await GetRankAsync(connection, mapId, best, _shutdown.Token).ConfigureAwait(false);
            return new SaveRecordResult(isPb, existing?.Best, best, rank, stageResults);
        }
        catch
        {
            await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DatabaseHealth> CheckHealthAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ReadyAsync().ConfigureAwait(false);
            await using var connection = await OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            var result = await command.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false);
            if (Convert.ToInt32(result) != 1) throw new InvalidDataException("Database health query returned an unexpected value.");
            MarkSuccess();
            return new DatabaseHealth(true, started.ElapsedMilliseconds, options.ServerId,
                options.DatabaseConnection, "ok");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkFailure();
            logger.LogError(exception, "SurfTimer database health check failed.");
            return new DatabaseHealth(false, started.ElapsedMilliseconds, options.ServerId,
                options.DatabaseConnection, exception.GetType().Name);
        }
    }

    public async Task<StagePersonalBest?> GetStagePersonalBestAsync(ulong steamId, string mapName, int stage)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sr.best_time_us,sr.completions,
                   1+(SELECT COUNT(*) FROM st_stage_records faster WHERE faster.map_id=sr.map_id AND faster.stage=sr.stage AND faster.best_time_us<sr.best_time_us),
                   (SELECT COUNT(*) FROM st_stage_records total WHERE total.map_id=sr.map_id AND total.stage=sr.stage)
            FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id
            WHERE m.name=@map AND sr.player_steam_id=@steam AND sr.stage=@stage
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@steam", steamId); command.AddParameter("@stage", stage);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new StagePersonalBest(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(2)),
            Convert.ToInt32(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(1)));
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetStageTopAsync(string mapName, int stage, int limit = 10)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sr.player_steam_id,p.last_name,sr.best_time_us,sr.completions
            FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id JOIN st_players p ON p.steam_id=sr.player_steam_id
            WHERE m.name=@map AND sr.stage=@stage
            ORDER BY sr.best_time_us,sr.pb_updated_at,sr.player_steam_id LIMIT @limit
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@stage", stage); command.AddParameter("@limit", Math.Clamp(limit, 1, 100));
        var entries = new List<LeaderboardEntry>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            entries.Add(new LeaderboardEntry(entries.Count + 1, Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        return entries;
    }

    public async Task<PersonalBest?> GetPersonalBestAsync(ulong steamId, string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.best_time_us, r.completions,
                   1 + (SELECT COUNT(*) FROM st_records faster
                        WHERE faster.map_id = r.map_id AND faster.route_type = 'main' AND faster.route_index = 0
                          AND faster.style = 0 AND faster.mode = 'surf' AND faster.best_time_us < r.best_time_us) AS rank_no
            FROM st_records r JOIN st_maps m ON m.id = r.map_id
            WHERE m.name = @map AND r.player_steam_id = @steam
              AND r.route_type = 'main' AND r.route_index = 0 AND r.style = 0 AND r.mode = 'surf'
            """;
        command.AddParameter("@map", mapName);
        command.AddParameter("@steam", steamId);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        return await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)
            ? new PersonalBest(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(1)))
            : null;
    }

    public async Task<PersonalBestDetails?> GetPersonalBestDetailsAsync(ulong steamId, string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.best_time_us, r.completions,
                   1 + (SELECT COUNT(*) FROM st_records faster
                        WHERE faster.map_id=r.map_id AND faster.route_type='main' AND faster.route_index=0
                          AND faster.style=0 AND faster.mode='surf' AND faster.best_time_us<r.best_time_us) AS rank_no,
                   (SELECT COUNT(*) FROM st_records total
                        WHERE total.map_id=r.map_id AND total.route_type='main' AND total.route_index=0
                          AND total.style=0 AND total.mode='surf') AS total_records
            FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.player_steam_id=@steam
              AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            """;
        command.AddParameter("@map", mapName);
        command.AddParameter("@steam", steamId);
        long recordId;
        long time;
        int completions;
        int rank;
        int total;
        await using (var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
            recordId = Convert.ToInt64(reader.GetValue(0));
            time = Convert.ToInt64(reader.GetValue(1));
            completions = Convert.ToInt32(reader.GetValue(2));
            rank = Convert.ToInt32(reader.GetValue(3));
            total = Convert.ToInt32(reader.GetValue(4));
        }

        await using var splitCommand = connection.CreateCommand();
        splitCommand.CommandText = "SELECT checkpoint,split_time_us FROM st_record_splits WHERE record_id=@id ORDER BY checkpoint";
        splitCommand.AddParameter("@id", recordId);
        var splits = new List<RecordSplit>();
        await using var splitReader = await splitCommand.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await splitReader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            splits.Add(new RecordSplit(Convert.ToInt32(splitReader.GetValue(0)), Convert.ToInt64(splitReader.GetValue(1))));
        return new PersonalBestDetails(time, rank, total, completions, splits);
    }

    public async Task<MapRunComparison> GetMapRunComparisonAsync(ulong steamId, string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.player_steam_id,r.best_time_us,r.completions
            FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id
            """;
        command.AddParameter("@map", mapName);
        var rows = new List<(long Id, ulong SteamId, long Time, int Completions)>();
        await using (var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                rows.Add((Convert.ToInt64(reader.GetValue(0)), Convert.ToUInt64(reader.GetValue(1)),
                    Convert.ToInt64(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        }

        async Task<PersonalBestDetails?> BuildDetailsAsync(int index)
        {
            if (index < 0) return null;
            var row = rows[index];
            await using var splitCommand = connection.CreateCommand();
            splitCommand.CommandText = "SELECT checkpoint,split_time_us FROM st_record_splits WHERE record_id=@id ORDER BY checkpoint";
            splitCommand.AddParameter("@id", row.Id);
            var splits = new List<RecordSplit>();
            await using var splitReader = await splitCommand.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
            while (await splitReader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                splits.Add(new RecordSplit(Convert.ToInt32(splitReader.GetValue(0)), Convert.ToInt64(splitReader.GetValue(1))));
            var rank = 1 + rows.Count(value => value.Time < row.Time);
            return new PersonalBestDetails(row.Time, rank, rows.Count, row.Completions, splits);
        }

        var personalIndex = rows.FindIndex(value => value.SteamId == steamId);
        var personal = await BuildDetailsAsync(personalIndex).ConfigureAwait(false);
        var worldRecord = await BuildDetailsAsync(rows.Count == 0 ? -1 : 0).ConfigureAwait(false);
        return new MapRunComparison(personal, worldRecord, rows.Select(value => value.Time).ToArray());
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetTopAsync(string mapName, int limit = 10)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
              SELECT r.player_steam_id,p.last_name,r.best_time_us,r.completions,r.pb_updated_at,
                     RANK() OVER (ORDER BY r.best_time_us) rank_no
              FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
              WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            )
            SELECT rank_no,player_steam_id,last_name,best_time_us,completions
            FROM ranked ORDER BY best_time_us,pb_updated_at,player_steam_id LIMIT @limit
            """;
        command.AddParameter("@map", mapName);
        command.AddParameter("@limit", Math.Clamp(limit, 1, 100));
        var entries = new List<LeaderboardEntry>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            entries.Add(new LeaderboardEntry(Convert.ToInt32(reader.GetValue(0)), Convert.ToUInt64(reader.GetValue(1)), reader.GetString(2),
                Convert.ToInt64(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4))));
        return entries;
    }

    public async Task<IReadOnlyList<long>> GetRankedTimesAsync(string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.best_time_us
            FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us
            """;
        command.AddParameter("@map", mapName);
        var times = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            times.Add(Convert.ToInt64(reader.GetValue(0)));
        return times;
    }

    public async Task<IReadOnlyList<PlayerIdentity>> FindPlayersAsync(string query, int limit = 5)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT steam_id,last_name FROM st_players
            WHERE steam_id=@query OR LOCATE(LOWER(@query),LOWER(last_name))>0
            ORDER BY (LOWER(last_name)=LOWER(@query)) DESC,last_seen_at DESC,steam_id
            LIMIT @limit
            """;
        command.AddParameter("@query", query.Trim());
        command.AddParameter("@limit", Math.Clamp(limit, 1, 20));
        var matches = new List<PlayerIdentity>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
            matches.Add(new PlayerIdentity(Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1)));
        return matches;
    }

    public async Task<IReadOnlyList<OverallRanking>> GetOverallRankingsAsync(int limit=10)
    {
        await ReadyAsync().ConfigureAwait(false);await using var connection=await OpenAsync().ConfigureAwait(false);await using var command=connection.CreateCommand();
        command.CommandText=OverallRankingCte+" SELECT o.overall_rank,o.player_steam_id,p.last_name,o.points,o.completed_maps,o.group1,o.group2,o.group3,o.group4,o.group5,o.map_points,o.stage_points,o.bonus_points,o.title FROM overall o JOIN st_players p ON p.steam_id=o.player_steam_id ORDER BY o.points DESC,o.group1 DESC,o.group2 DESC,o.completed_maps DESC,p.last_name LIMIT @limit";
        command.AddParameter("@limit",Math.Clamp(limit,1,100));var rows=new List<OverallRanking>();await using var reader=await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while(await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))rows.Add(ReadOverallRanking(reader));return rows;
    }

    public async Task<OverallRanking?> GetPlayerOverallRankingAsync(ulong steamId)
    {
        await ReadyAsync().ConfigureAwait(false);await using var connection=await OpenAsync().ConfigureAwait(false);await using var command=connection.CreateCommand();
        command.CommandText=OverallRankingCte+" SELECT o.overall_rank,o.player_steam_id,p.last_name,o.points,o.completed_maps,o.group1,o.group2,o.group3,o.group4,o.group5,o.map_points,o.stage_points,o.bonus_points,o.title FROM overall o JOIN st_players p ON p.steam_id=o.player_steam_id WHERE o.player_steam_id=@steam";
        command.AddParameter("@steam",steamId);await using var reader=await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);return await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)?ReadOverallRanking(reader):null;
    }

    private static OverallRanking ReadOverallRanking(DbDataReader reader)=>new(Convert.ToInt32(reader.GetValue(0)),Convert.ToUInt64(reader.GetValue(1)),reader.GetString(2),Convert.ToInt64(reader.GetValue(3)),Convert.ToInt32(reader.GetValue(4)),Convert.ToInt32(reader.GetValue(5)),Convert.ToInt32(reader.GetValue(6)),Convert.ToInt32(reader.GetValue(7)),Convert.ToInt32(reader.GetValue(8)),Convert.ToInt32(reader.GetValue(9)),Convert.ToInt64(reader.GetValue(10)),Convert.ToInt64(reader.GetValue(11)),Convert.ToInt64(reader.GetValue(12)),reader.GetString(13));

    public async Task<GlobalPlayerProfile?> GetGlobalPlayerProfileAsync(ulong steamId)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.steam_id,p.last_name,p.first_seen_at,p.last_seen_at,p.total_connections,
              COALESCE((SELECT SUM(r.completions) FROM st_records r WHERE r.player_steam_id=p.steam_id),0),
              COALESCE((SELECT COUNT(DISTINCT r.map_id) FROM st_records r WHERE r.player_steam_id=p.steam_id),0),
              COALESCE((SELECT SUM(r.route_type='main') FROM st_records r WHERE r.player_steam_id=p.steam_id),0),
              COALESCE((SELECT SUM(r.route_type='bonus') FROM st_records r WHERE r.player_steam_id=p.steam_id),0),
              COALESCE((SELECT COUNT(*) FROM st_stage_records sr WHERE sr.player_steam_id=p.steam_id),0),
              COALESCE((SELECT COUNT(*) FROM st_replays rp JOIN st_records r ON r.id=rp.record_id WHERE r.player_steam_id=p.steam_id),0),
              COALESCE((SELECT COUNT(*) FROM st_records r WHERE r.player_steam_id=p.steam_id AND NOT EXISTS
                (SELECT 1 FROM st_records f WHERE f.map_id=r.map_id AND f.route_type=r.route_type AND f.route_index=r.route_index
                 AND f.style=r.style AND f.mode=r.mode AND f.best_time_us<r.best_time_us)),0),
              s.tracked_completions,s.tracked_time_us,s.tracking_started_at
            FROM st_players p LEFT JOIN st_player_run_stats s ON s.player_steam_id=p.steam_id
            WHERE p.steam_id=@steam
            """;
        command.AddParameter("@steam", steamId);
        ulong id; string name; DateTime first; DateTime last; uint connections; long completions;
        int maps; int main; int bonus; int stages; int replays; int wrs; long trackedRuns; long trackedTime; DateTime? trackingStarted;
        await using (var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
            id=Convert.ToUInt64(reader.GetValue(0)); name=reader.GetString(1); first=reader.GetDateTime(2); last=reader.GetDateTime(3);
            connections=Convert.ToUInt32(reader.GetValue(4)); completions=Convert.ToInt64(reader.GetValue(5)); maps=Convert.ToInt32(reader.GetValue(6));
            main=Convert.ToInt32(reader.GetValue(7)); bonus=Convert.ToInt32(reader.GetValue(8)); stages=Convert.ToInt32(reader.GetValue(9));
            replays=Convert.ToInt32(reader.GetValue(10)); wrs=Convert.ToInt32(reader.GetValue(11));
            trackedRuns=reader.IsDBNull(12)?0:Convert.ToInt64(reader.GetValue(12)); trackedTime=reader.IsDBNull(13)?0:Convert.ToInt64(reader.GetValue(13));
            trackingStarted=reader.IsDBNull(14)?null:reader.GetDateTime(14);
        }
        string? mostPlayed = null;
        await using (var most = connection.CreateCommand())
        {
            most.CommandText = "SELECT m.name FROM st_records r JOIN st_maps m ON m.id=r.map_id WHERE r.player_steam_id=@steam GROUP BY m.id,m.name ORDER BY SUM(r.completions) DESC,m.name LIMIT 1";
            most.AddParameter("@steam", steamId); mostPlayed = (await most.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false)) as string;
        }
        var recent = new List<RecentPersonalBest>();
        await using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT m.name,h.route_type,h.route_index,h.new_time_us,h.achieved_at FROM st_pb_history h JOIN st_maps m ON m.id=h.map_id WHERE h.player_steam_id=@steam ORDER BY h.achieved_at DESC,h.id DESC LIMIT 3";
            history.AddParameter("@steam", steamId);
            await using var reader = await history.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                recent.Add(new(reader.GetString(0),reader.GetString(1),Convert.ToInt32(reader.GetValue(2)),Convert.ToInt64(reader.GetValue(3)),reader.GetDateTime(4)));
        }
        return new(id,name,first,last,connections,completions,maps,main,bonus,stages,replays,wrs,mostPlayed,trackedRuns,trackedTime,trackingStarted,recent);
    }

    public async Task<GlobalMapStatistics?> GetGlobalMapStatisticsAsync(string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.last_name,r.best_time_us,r.completions FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf' ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id";
        command.AddParameter("@map", mapName);
        var rows = new List<(string Name,long Time,long Completions)>();
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) rows.Add((reader.GetString(0),Convert.ToInt64(reader.GetValue(1)),Convert.ToInt64(reader.GetValue(2))));
        if (rows.Count==0) return new(mapName,0,0,null,null,null,null);
        var median = rows.Count%2==1 ? rows[rows.Count/2].Time : (rows[rows.Count/2-1].Time+rows[rows.Count/2].Time)/2;
        return new(mapName,rows.Sum(x=>x.Completions),rows.Count,rows[0].Time,rows[0].Name,(long)rows.Average(x=>x.Time),median);
    }

    public async Task<MapRecordSummary> GetMapSummaryAsync(string mapName)
    {
        var top = await GetTopAsync(mapName, 1).ConfigureAwait(false);
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            """;
        command.AddParameter("@map", mapName);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false));
        return new MapRecordSummary(count, top.FirstOrDefault());
    }

    public async Task<AdminPlayerDetails?> GetAdminPlayerDetailsAsync(ulong steamId)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.steam_id,p.last_name,p.first_seen_at,p.last_seen_at,p.total_connections,COUNT(r.id)
            FROM st_players p LEFT JOIN st_records r ON r.player_steam_id=p.steam_id
            WHERE p.steam_id=@steam
            GROUP BY p.steam_id,p.last_name,p.first_seen_at,p.last_seen_at,p.total_connections
            """;
        command.AddParameter("@steam", steamId);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new AdminPlayerDetails(Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1),
            reader.GetDateTime(2), reader.GetDateTime(3), Convert.ToUInt32(reader.GetValue(4)),
            Convert.ToInt32(reader.GetValue(5)));
    }

    public async Task<DeletedPersonalBest?> DeletePersonalBestAsync(ulong steamId, string mapName)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            long recordId;
            string playerName;
            long time;
            int completions;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT r.id,p.last_name,r.best_time_us,r.completions
                    FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
                    WHERE m.name=@map AND r.player_steam_id=@steam AND r.route_type='main' AND r.route_index=0
                      AND r.style=0 AND r.mode='surf' FOR UPDATE
                    """;
                select.AddParameter("@map", mapName);
                select.AddParameter("@steam", steamId);
                await using var reader = await select.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);
                    return null;
                }
                recordId = Convert.ToInt64(reader.GetValue(0));
                playerName = reader.GetString(1);
                time = Convert.ToInt64(reader.GetValue(2));
                completions = Convert.ToInt32(reader.GetValue(3));
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM st_records WHERE id=@id";
            delete.AddParameter("@id", recordId);
            await delete.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            return new DeletedPersonalBest(steamId, playerName, mapName, time, completions);
        }
        catch
        {
            await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AppendAdminAuditAsync(
        ulong actorSteamId, string actorName, string action, string target, string details)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO st_admin_audit
                (actor_steam_id,actor_name,server_id,action_name,target_value,details,created_at)
            VALUES (@steam,@name,@server,@action,@target,@details,UTC_TIMESTAMP(6))
            """;
        command.AddParameter("@steam", actorSteamId);
        command.AddParameter("@name", actorName);
        command.AddParameter("@server", options.ServerId);
        command.AddParameter("@action", action);
        command.AddParameter("@target", target);
        command.AddParameter("@details", details);
        await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
    }

    public async Task<StoredReplay?> GetReplayAsync(string mapName, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,rp.frame_count,rp.duration_us,rp.compressed_frames
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            JOIN st_replays rp ON rp.record_id=r.id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@offset", Math.Clamp(rank, 1, 10) - 1);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new StoredReplay(rank, reader.GetString(0), Convert.ToInt64(reader.GetValue(1)),
            new EncodedReplay(Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5)), (byte[])reader.GetValue(6)));
    }

    public async Task<StoredReplay?> GetBonusReplayAsync(string mapName, int bonus, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,rp.frame_count,rp.duration_us,rp.compressed_frames
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            JOIN st_replays rp ON rp.record_id=r.id
            WHERE m.name=@map AND r.route_type='bonus' AND r.route_index=@bonus AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@bonus", bonus);
        command.AddParameter("@offset", Math.Clamp(rank, 1, 10) - 1);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new StoredReplay(rank, reader.GetString(0), Convert.ToInt64(reader.GetValue(1)),
            new EncodedReplay(Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5)), (byte[])reader.GetValue(6)));
    }

    public async Task<StoredReplay?> GetStageReplayAsync(string mapName, int stage, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.last_name,sr.best_time_us,rp.format_version,rp.sample_rate_hz,rp.frame_count,rp.duration_us,rp.compressed_frames
            FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id JOIN st_players p ON p.steam_id=sr.player_steam_id
            JOIN st_stage_replays rp ON rp.stage_record_id=sr.id
            WHERE m.name=@map AND sr.stage=@stage
            ORDER BY sr.best_time_us,sr.pb_updated_at,sr.player_steam_id LIMIT 1 OFFSET @offset
            """;
        command.AddParameter("@map", mapName); command.AddParameter("@stage", stage);
        command.AddParameter("@offset", Math.Clamp(rank, 1, 10)-1);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new StoredReplay(rank,reader.GetString(0),Convert.ToInt64(reader.GetValue(1)),
            new EncodedReplay(Convert.ToInt32(reader.GetValue(2)),Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4)),Convert.ToInt64(reader.GetValue(5)),(byte[])reader.GetValue(6)));
    }

    public async Task<ReplayAdminDetails?> GetReplayAdminDetailsAsync(string mapName, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.player_steam_id,p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,
                   rp.frame_count,rp.duration_us,OCTET_LENGTH(rp.compressed_frames)
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            JOIN st_replays rp ON rp.record_id=r.id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset
            """;
        var safeRank = Math.Clamp(rank, 1, 10);
        command.AddParameter("@map", mapName); command.AddParameter("@offset", safeRank - 1);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new ReplayAdminDetails(safeRank, Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1), mapName,
            Convert.ToInt64(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4)),
            Convert.ToInt32(reader.GetValue(5)), Convert.ToInt64(reader.GetValue(6)), Convert.ToInt32(reader.GetValue(7)));
    }

    public async Task<ReplayAdminDetails?> GetStageReplayAdminDetailsAsync(string mapName,int stage,int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection=await OpenAsync().ConfigureAwait(false);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT sr.player_steam_id,p.last_name,sr.best_time_us,rp.format_version,rp.sample_rate_hz,rp.frame_count,rp.duration_us,OCTET_LENGTH(rp.compressed_frames)
            FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id JOIN st_players p ON p.steam_id=sr.player_steam_id
            JOIN st_stage_replays rp ON rp.stage_record_id=sr.id WHERE m.name=@map AND sr.stage=@stage
            ORDER BY sr.best_time_us,sr.pb_updated_at,sr.player_steam_id LIMIT 1 OFFSET @offset
            """;
        var safeRank=Math.Clamp(rank,1,10); command.AddParameter("@map",mapName); command.AddParameter("@stage",stage); command.AddParameter("@offset",safeRank-1);
        await using var reader=await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if(!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new(safeRank,Convert.ToUInt64(reader.GetValue(0)),reader.GetString(1),mapName,Convert.ToInt64(reader.GetValue(2)),
            Convert.ToInt32(reader.GetValue(3)),Convert.ToInt32(reader.GetValue(4)),Convert.ToInt32(reader.GetValue(5)),
            Convert.ToInt64(reader.GetValue(6)),Convert.ToInt32(reader.GetValue(7)));
    }

    public async Task<ReplayAdminDetails?> GetBonusReplayAdminDetailsAsync(string mapName,int bonus,int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection=await OpenAsync().ConfigureAwait(false);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT r.player_steam_id,p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,
                   rp.frame_count,rp.duration_us,OCTET_LENGTH(rp.compressed_frames)
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            JOIN st_replays rp ON rp.record_id=r.id
            WHERE m.name=@map AND r.route_type='bonus' AND r.route_index=@bonus AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset
            """;
        var safeRank=Math.Clamp(rank,1,10);
        command.AddParameter("@map",mapName);command.AddParameter("@bonus",bonus);command.AddParameter("@offset",safeRank-1);
        await using var reader=await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if(!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        return new(safeRank,Convert.ToUInt64(reader.GetValue(0)),reader.GetString(1),mapName,Convert.ToInt64(reader.GetValue(2)),
            Convert.ToInt32(reader.GetValue(3)),Convert.ToInt32(reader.GetValue(4)),Convert.ToInt32(reader.GetValue(5)),
            Convert.ToInt64(reader.GetValue(6)),Convert.ToInt32(reader.GetValue(7)));
    }

    public async Task<ReplayAdminDetails?> DeleteStageReplayAsync(string mapName,int stage,int rank)
    {
        var details=await GetStageReplayAdminDetailsAsync(mapName,stage,rank).ConfigureAwait(false);
        if(details is null) return null;
        await using var connection=await OpenAsync().ConfigureAwait(false);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            DELETE rp FROM st_stage_replays rp JOIN st_stage_records sr ON sr.id=rp.stage_record_id JOIN st_maps m ON m.id=sr.map_id
            WHERE m.name=@map AND sr.stage=@stage AND sr.player_steam_id=@steam
            """;
        command.AddParameter("@map",mapName); command.AddParameter("@stage",stage); command.AddParameter("@steam",details.SteamId);
        return await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false)==1?details:null;
    }

    public async Task<ReplayAdminDetails?> DeleteBonusReplayAsync(string mapName,int bonus,int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection=await OpenAsync().ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            var safeRank=Math.Clamp(rank,1,10);long recordId;ReplayAdminDetails details;
            await using(var select=connection.CreateCommand())
            {
                select.Transaction=transaction;
                select.CommandText="""
                    SELECT rp.record_id,r.player_steam_id,p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,
                           rp.frame_count,rp.duration_us,OCTET_LENGTH(rp.compressed_frames)
                    FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
                    JOIN st_replays rp ON rp.record_id=r.id
                    WHERE m.name=@map AND r.route_type='bonus' AND r.route_index=@bonus AND r.style=0 AND r.mode='surf'
                    ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset FOR UPDATE
                    """;
                select.AddParameter("@map",mapName);select.AddParameter("@bonus",bonus);select.AddParameter("@offset",safeRank-1);
                await using var reader=await select.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
                if(!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)){await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);return null;}
                recordId=Convert.ToInt64(reader.GetValue(0));
                details=new(safeRank,Convert.ToUInt64(reader.GetValue(1)),reader.GetString(2),mapName,Convert.ToInt64(reader.GetValue(3)),
                    Convert.ToInt32(reader.GetValue(4)),Convert.ToInt32(reader.GetValue(5)),Convert.ToInt32(reader.GetValue(6)),
                    Convert.ToInt64(reader.GetValue(7)),Convert.ToInt32(reader.GetValue(8)));
            }
            await using var delete=connection.CreateCommand();delete.Transaction=transaction;
            delete.CommandText="DELETE FROM st_replays WHERE record_id=@record";delete.AddParameter("@record",recordId);
            if(await delete.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false)!=1) throw new InvalidOperationException("Expected to delete one bonus replay.");
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);return details;
        }
        catch{await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);throw;}
    }

    public async Task<RecordValidationDetails?> GetRecordValidationDetailsAsync(string mapName, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.last_name,r.best_time_us,v.validation_version,v.maximum_speed,v.overspeed_samples,
                   v.maximum_frame_distance,v.position_jump_count,v.flags,v.analyzed_at
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            LEFT JOIN st_run_validation v ON v.record_id=r.id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset
            """;
        var safeRank = Math.Clamp(rank, 1, 10);
        command.AddParameter("@map", mapName); command.AddParameter("@offset", safeRank - 1);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return null;
        if (reader.IsDBNull(2))
            return new RecordValidationDetails(safeRank, reader.GetString(0), Convert.ToInt64(reader.GetValue(1)),
                0, 0, 0, 0, 0, "not_analyzed", DateTime.MinValue);
        return new RecordValidationDetails(safeRank, reader.GetString(0), Convert.ToInt64(reader.GetValue(1)),
            Convert.ToInt32(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4)),
            Convert.ToDouble(reader.GetValue(5)), Convert.ToInt32(reader.GetValue(6)), reader.GetString(7), reader.GetDateTime(8));
    }

    public async Task<ReplayAdminDetails?> DeleteReplayAsync(string mapName, int rank)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            var safeRank = Math.Clamp(rank, 1, 10);
            long recordId;
            ReplayAdminDetails details;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT rp.record_id,r.player_steam_id,p.last_name,r.best_time_us,rp.format_version,rp.sample_rate_hz,
                           rp.frame_count,rp.duration_us,OCTET_LENGTH(rp.compressed_frames)
                    FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
                    JOIN st_replays rp ON rp.record_id=r.id
                    WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
                    ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 1 OFFSET @offset FOR UPDATE
                    """;
                select.AddParameter("@map", mapName); select.AddParameter("@offset", safeRank - 1);
                await using var reader = await select.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);
                    return null;
                }
                recordId = Convert.ToInt64(reader.GetValue(0));
                details = new ReplayAdminDetails(safeRank, Convert.ToUInt64(reader.GetValue(1)), reader.GetString(2), mapName,
                    Convert.ToInt64(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4)), Convert.ToInt32(reader.GetValue(5)),
                    Convert.ToInt32(reader.GetValue(6)), Convert.ToInt64(reader.GetValue(7)), Convert.ToInt32(reader.GetValue(8)));
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM st_replays WHERE record_id=@record";
            delete.AddParameter("@record", recordId);
            var affected = await delete.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
            if (affected != 1) throw new InvalidOperationException($"Expected to delete one replay but deleted {affected}.");
            await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            return details;
        }
        catch
        {
            await transaction.RollbackAsync(_shutdown.Token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadyAsync()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Start();
            Task initialization;
            lock (_initializationSync) initialization = _initialization!;
            try
            {
                await initialization.ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception) && attempt < 3)
            {
                lock (_initializationSync)
                {
                    if (ReferenceEquals(_initialization, initialization)) _initialization = null;
                }
                var delay = TimeSpan.FromMilliseconds(100 * attempt);
                logger.LogWarning(exception, "Database initialization retry {NextAttempt}/3 in {DelayMs}ms.",
                    attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<DbConnection> OpenAsync()
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception) && attempt < 3)
            {
                lastFailure = exception;
                MarkFailure();
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), _shutdown.Token).ConfigureAwait(false);
            }
        }
        throw lastFailure ?? new InvalidOperationException("Database connection failed without an exception.");
    }

    private void MarkSuccess()
    {
        LastSuccessfulOperationAt = DateTimeOffset.UtcNow;
        ConsecutiveFailures = 0;
        Status = "ready";
    }

    private void MarkFailure()
    {
        LastFailedOperationAt = DateTimeOffset.UtcNow;
        ConsecutiveFailures++;
        Status = "degraded";
    }

    private static bool IsTransientWriteFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var number = current.GetType().GetProperty("Number")?.GetValue(current);
            if (number is not null && Convert.ToInt32(number) is 1062 or 1205 or 1213) return true;
            if (current is DbException db && db.SqlState is "40001" or "41000") return true;
        }
        return false;
    }

    private static bool IsTransientConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is DbException or IOException or TimeoutException) return true;
        return false;
    }

    private static async Task UpsertPlayerSeenAsync(DbConnection c, DbTransaction t, CompletedRun run, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO st_players (steam_id,last_name,first_seen_at,last_seen_at,first_server_id,last_server_id,total_connections)
            VALUES (@steam,@name,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@server,@server,1)
            ON DUPLICATE KEY UPDATE last_name=VALUES(last_name),last_seen_at=UTC_TIMESTAMP(6),last_server_id=VALUES(last_server_id)
            """;
        cmd.AddParameter("@steam", run.SteamId); cmd.AddParameter("@name", run.PlayerName); cmd.AddParameter("@server", run.ServerId);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task UpsertMapAsync(DbConnection c, DbTransaction? t, string name, string? workshop, int cps, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO st_maps (name,workshop_id,checkpoint_count,created_at,updated_at)
            VALUES (@name,@workshop,@cps,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE workshop_id=COALESCE(VALUES(workshop_id),workshop_id),
                checkpoint_count=GREATEST(checkpoint_count,VALUES(checkpoint_count)),updated_at=UTC_TIMESTAMP(6)
            """;
        cmd.AddParameter("@name", name); cmd.AddParameter("@workshop", string.IsNullOrWhiteSpace(workshop) ? null : workshop); cmd.AddParameter("@cps", cps);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<long> GetMapIdAsync(DbConnection c, DbTransaction t, string map, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT id FROM st_maps WHERE name=@map"; cmd.AddParameter("@map", map);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static async Task<(long Id, long Best)?> GetRecordForUpdateAsync(DbConnection c, DbTransaction t, long mapId, ulong steam, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "SELECT id,best_time_us FROM st_records WHERE map_id=@map AND player_steam_id=@steam AND route_type='main' AND route_index=0 AND style=0 AND mode='surf' FOR UPDATE";
        cmd.AddParameter("@map", mapId); cmd.AddParameter("@steam", steam);
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1))) : null;
    }

    private static async Task<long> InsertRecordAsync(DbConnection c, DbTransaction t, long mapId, CompletedRun run, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO st_records (map_id,player_steam_id,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id)
            VALUES (@map,@steam,@time,1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@server);
            SELECT LAST_INSERT_ID()
            """;
        cmd.AddParameter("@map", mapId); cmd.AddParameter("@steam", run.SteamId); cmd.AddParameter("@time", run.TimeMicroseconds); cmd.AddParameter("@server", run.ServerId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static async Task UpdateRecordAsync(DbConnection c, DbTransaction t, long id, CompletedRun run, bool pb, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            UPDATE st_records SET completions=completions+1,last_completed_at=UTC_TIMESTAMP(6),last_server_id=@server,
                best_time_us=IF(@pb=1,@time,best_time_us),pb_updated_at=IF(@pb=1,UTC_TIMESTAMP(6),pb_updated_at) WHERE id=@id
            """;
        cmd.AddParameter("@server", run.ServerId); cmd.AddParameter("@pb", pb ? 1 : 0); cmd.AddParameter("@time", run.TimeMicroseconds); cmd.AddParameter("@id", id);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task ReplaceSplitsAsync(DbConnection c, DbTransaction t, long id, IReadOnlyList<long> splits, CancellationToken token)
    {
        await using (var delete = c.CreateCommand()) { delete.Transaction=t; delete.CommandText="DELETE FROM st_record_splits WHERE record_id=@id"; delete.AddParameter("@id", id); await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        for (var i=0; i<splits.Count; i++) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="INSERT INTO st_record_splits (record_id,checkpoint,split_time_us) VALUES (@id,@cp,@time)"; cmd.AddParameter("@id",id); cmd.AddParameter("@cp",i+1); cmd.AddParameter("@time",splits[i]); await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    }

    private static async Task<StageRecordResult> UpsertStageRecordAsync(
        DbConnection c, DbTransaction t, long mapId, CompletedRun run, int stage, long time,
        ReplayCapture? replay, CancellationToken token)
    {
        long? previous = null;
        long? id = null;
        await using (var select = c.CreateCommand())
        {
            select.Transaction = t;
            select.CommandText = "SELECT id,best_time_us FROM st_stage_records WHERE map_id=@map AND player_steam_id=@steam AND stage=@stage FOR UPDATE";
            select.AddParameter("@map", mapId); select.AddParameter("@steam", run.SteamId); select.AddParameter("@stage", stage);
            await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                id = Convert.ToInt64(reader.GetValue(0));
                previous = Convert.ToInt64(reader.GetValue(1));
            }
        }
        var isPb = previous is null || time < previous;
        if (id is null)
        {
            await using var insert = c.CreateCommand(); insert.Transaction = t;
            insert.CommandText = """
                INSERT INTO st_stage_records
                    (map_id,player_steam_id,stage,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id)
                VALUES (@map,@steam,@stage,@time,1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@server);
                SELECT LAST_INSERT_ID()
                """;
            insert.AddParameter("@map", mapId); insert.AddParameter("@steam", run.SteamId); insert.AddParameter("@stage", stage);
            insert.AddParameter("@time", time); insert.AddParameter("@server", run.ServerId);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        else
        {
            await using var update = c.CreateCommand(); update.Transaction = t;
            update.CommandText = """
                UPDATE st_stage_records SET completions=completions+1,last_completed_at=UTC_TIMESTAMP(6),last_server_id=@server,
                    best_time_us=IF(@pb=1,@time,best_time_us),pb_updated_at=IF(@pb=1,UTC_TIMESTAMP(6),pb_updated_at)
                WHERE id=@id
                """;
            update.AddParameter("@server", run.ServerId); update.AddParameter("@pb", isPb ? 1 : 0);
            update.AddParameter("@time", time); update.AddParameter("@id", id.Value);
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (isPb && replay is not null)
            await ReplaceStageReplayAsync(c,t,id!.Value,ReplayCodec.Encode(replay),token).ConfigureAwait(false);
        if (isPb)
            await AppendStagePbHistoryAsync(c,t,id!.Value,mapId,run.SteamId,stage,previous,time,token).ConfigureAwait(false);
        var best = isPb ? time : previous!.Value;
        await using var rank = c.CreateCommand(); rank.Transaction = t;
        rank.CommandText = "SELECT 1+COUNT(*) FROM st_stage_records WHERE map_id=@map AND stage=@stage AND best_time_us<@best";
        rank.AddParameter("@map", mapId); rank.AddParameter("@stage", stage); rank.AddParameter("@best", best);
        return new StageRecordResult(stage, isPb, previous, best,
            Convert.ToInt32(await rank.ExecuteScalarAsync(token).ConfigureAwait(false)));
    }

    private static async Task<int> GetRankAsync(DbConnection c, long mapId, long best, CancellationToken token)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT 1+COUNT(*) FROM st_records WHERE map_id=@map AND route_type='main' AND route_index=0 AND style=0 AND mode='surf' AND best_time_us<@best"; cmd.AddParameter("@map",mapId); cmd.AddParameter("@best",best);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static ReplayCapture? SliceReplay(ReplayCapture? source, long start, long end)
    {
        if (source is null || source.Frames.Count == 0 || end <= start) return null;
        var frames = source.Frames.Where(frame => frame.TimeMicroseconds >= start && frame.TimeMicroseconds <= end)
            .Select(frame => frame with { TimeMicroseconds = Math.Max(0, frame.TimeMicroseconds-start) }).ToList();
        if (frames.Count == 0) return null;
        if (frames[0].TimeMicroseconds != 0) frames[0] = frames[0] with { TimeMicroseconds = 0 };
        return new ReplayCapture(source.SampleRateHz,frames,RecordedDurationMicroseconds:end-start);
    }

    private static async Task ReplaceStageReplayAsync(DbConnection c, DbTransaction t, long id, EncodedReplay replay, CancellationToken token)
    {
        await using var command=c.CreateCommand(); command.Transaction=t;
        command.CommandText="""
            INSERT INTO st_stage_replays(stage_record_id,format_version,sample_rate_hz,frame_count,duration_us,compressed_frames,recorded_at)
            VALUES(@id,@format,@rate,@frames,@duration,@data,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE format_version=VALUES(format_version),sample_rate_hz=VALUES(sample_rate_hz),
              frame_count=VALUES(frame_count),duration_us=VALUES(duration_us),compressed_frames=VALUES(compressed_frames),recorded_at=UTC_TIMESTAMP(6)
            """;
        command.AddParameter("@id",id); command.AddParameter("@format",replay.FormatVersion); command.AddParameter("@rate",replay.SampleRateHz);
        command.AddParameter("@frames",replay.FrameCount); command.AddParameter("@duration",replay.DurationMicroseconds); command.AddParameter("@data",replay.CompressedFrames);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task ReplaceReplayAsync(DbConnection c, DbTransaction t, long id, EncodedReplay replay, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO st_replays (record_id,format_version,sample_rate_hz,frame_count,duration_us,compressed_frames,created_at)
            VALUES (@id,@version,@rate,@count,@duration,@frames,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE format_version=VALUES(format_version),sample_rate_hz=VALUES(sample_rate_hz),
                frame_count=VALUES(frame_count),duration_us=VALUES(duration_us),compressed_frames=VALUES(compressed_frames),created_at=UTC_TIMESTAMP(6)
            """;
        cmd.AddParameter("@id", id); cmd.AddParameter("@version", replay.FormatVersion); cmd.AddParameter("@rate", replay.SampleRateHz);
        cmd.AddParameter("@count", replay.FrameCount); cmd.AddParameter("@duration", replay.DurationMicroseconds); cmd.AddParameter("@frames", replay.CompressedFrames);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task ReplaceValidationAsync(DbConnection c, DbTransaction t, long id, RunTelemetry telemetry, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO st_run_validation
                (record_id,validation_version,maximum_speed,overspeed_samples,maximum_frame_distance,position_jump_count,flags,analyzed_at)
            VALUES (@id,@version,@speed,@overspeed,@distance,@jumps,@flags,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE validation_version=VALUES(validation_version),maximum_speed=VALUES(maximum_speed),
                overspeed_samples=VALUES(overspeed_samples),maximum_frame_distance=VALUES(maximum_frame_distance),
                position_jump_count=VALUES(position_jump_count),flags=VALUES(flags),analyzed_at=UTC_TIMESTAMP(6)
            """;
        cmd.AddParameter("@id", id); cmd.AddParameter("@version", telemetry.ValidationVersion);
        cmd.AddParameter("@speed", telemetry.MaximumSpeed); cmd.AddParameter("@overspeed", telemetry.OverspeedSamples);
        cmd.AddParameter("@distance", telemetry.MaximumFrameDistance); cmd.AddParameter("@jumps", telemetry.PositionJumpCount);
        cmd.AddParameter("@flags", telemetry.Flags);
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task TrackCompletedRunAsync(DbConnection c, DbTransaction t, ulong steamId, long time, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction=t;
        command.CommandText = "INSERT INTO st_player_run_stats(player_steam_id,tracked_completions,tracked_time_us,tracking_started_at,updated_at) VALUES(@steam,1,@time,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE tracked_completions=tracked_completions+1,tracked_time_us=tracked_time_us+VALUES(tracked_time_us),updated_at=UTC_TIMESTAMP(6)";
        command.AddParameter("@steam",steamId); command.AddParameter("@time",time);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task AppendPbHistoryAsync(DbConnection c, DbTransaction t, long recordId, long mapId,
        ulong steamId, string routeType, int routeIndex, long? previous, long time, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction=t;
        command.CommandText = "INSERT INTO st_pb_history(record_id,player_steam_id,map_id,route_type,route_index,previous_time_us,new_time_us,achieved_at) VALUES(@record,@steam,@map,@route,@index,@previous,@time,UTC_TIMESTAMP(6))";
        command.AddParameter("@record",recordId); command.AddParameter("@steam",steamId); command.AddParameter("@map",mapId);
        command.AddParameter("@route",routeType); command.AddParameter("@index",routeIndex);
        command.AddParameter("@previous",previous is null ? DBNull.Value : previous.Value); command.AddParameter("@time",time);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task AppendStagePbHistoryAsync(DbConnection c,DbTransaction t,long recordId,long mapId,
        ulong steamId,int stage,long? previous,long time,CancellationToken token)
    {
        await using var command=c.CreateCommand();command.Transaction=t;
        command.CommandText="INSERT INTO st_stage_pb_history(stage_record_id,player_steam_id,map_id,stage,previous_time_us,new_time_us,achieved_at) VALUES(@record,@steam,@map,@stage,@previous,@time,UTC_TIMESTAMP(6))";
        command.AddParameter("@record",recordId);command.AddParameter("@steam",steamId);command.AddParameter("@map",mapId);command.AddParameter("@stage",stage);
        command.AddParameter("@previous",previous is null?DBNull.Value:previous.Value);command.AddParameter("@time",time);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public void Dispose() { _shutdown.Cancel(); _shutdown.Dispose(); }
}
