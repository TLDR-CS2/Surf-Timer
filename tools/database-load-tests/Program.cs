using System.Diagnostics;
using System.Text.Json;
using MySqlConnector;

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var configPath = Path.Combine(workspace, "tools", "local-server", "database.local.jsonc");
var fakePlayers = args.Length > 0 ? int.Parse(args[0]) : 25_000;
if (fakePlayers is < 1_000 or > 250_000) throw new ArgumentOutOfRangeException(nameof(fakePlayers));

using var document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
{ CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
var root = document.RootElement;
var connectionName = root.GetProperty("default_connection").GetString()!;
var value = root.GetProperty("connections").GetProperty(connectionName);
var connectionString = new MySqlConnectionStringBuilder
{
    Server = value.GetProperty("host").GetString(),
    Port = value.GetProperty("port").GetUInt32(),
    Database = value.GetProperty("database").GetString(),
    UserID = value.GetProperty("user").GetString(),
    Password = value.GetProperty("pass").GetString(),
    CharacterSet = "utf8mb4",
    MaximumPoolSize = 64
}.ConnectionString;

var prefix = "st_bench_" + Guid.NewGuid().ToString("N")[..8];
var players = prefix + "_players";
var maps = prefix + "_maps";
var records = prefix + "_records";
var splits = prefix + "_splits";
var replays = prefix + "_replays";

await using var admin = new MySqlConnection(connectionString);
await admin.OpenAsync();
try
{
    await ExecuteAsync(admin, $"""
        CREATE TABLE `{players}` (
          steam_id BIGINT UNSIGNED NOT NULL PRIMARY KEY, last_name VARCHAR(64) NOT NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE `{maps}` (
          id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, name VARCHAR(255) NOT NULL,
          UNIQUE KEY uq_name(name)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE `{records}` (
          id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
          map_id BIGINT UNSIGNED NOT NULL, player_steam_id BIGINT UNSIGNED NOT NULL,
          route_type VARCHAR(16) NOT NULL DEFAULT 'main', route_index SMALLINT UNSIGNED NOT NULL DEFAULT 0,
          style SMALLINT UNSIGNED NOT NULL DEFAULT 0, mode VARCHAR(24) NOT NULL DEFAULT 'surf',
          best_time_us BIGINT UNSIGNED NOT NULL, completions INT UNSIGNED NOT NULL DEFAULT 1,
          pb_updated_at DATETIME(6) NOT NULL, last_server_id VARCHAR(64) NOT NULL,
          UNIQUE KEY uq_route(map_id,player_steam_id,route_type,route_index,style,mode),
          KEY ix_leaderboard(map_id,route_type,route_index,style,mode,best_time_us,pb_updated_at,player_steam_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE `{splits}` (
          record_id BIGINT UNSIGNED NOT NULL, checkpoint SMALLINT UNSIGNED NOT NULL,
          split_time_us BIGINT UNSIGNED NOT NULL, PRIMARY KEY(record_id,checkpoint)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE `{replays}` (
          record_id BIGINT UNSIGNED NOT NULL PRIMARY KEY, duration_us BIGINT UNSIGNED NOT NULL,
          compressed_frames LONGBLOB NOT NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        INSERT INTO `{maps}` (name) VALUES ('surf_benchmark');
        """);

    Console.WriteLine($"Isolated tables: {prefix}_* (production st_* tables are untouched)");
    var seedTimer = Stopwatch.StartNew();
    await SeedAsync(admin, fakePlayers);
    Console.WriteLine($"Seeded {fakePlayers:N0} fake players and records in {seedTimer.Elapsed.TotalSeconds:F2}s.");

    var querySteamId = 90_000_000_000_000_000UL + (ulong)(fakePlayers / 2);
    var topLatency = await MeasureAsync(admin, $"""
        SELECT r.player_steam_id,p.last_name,r.best_time_us,r.completions
        FROM `{records}` r JOIN `{maps}` m ON m.id=r.map_id JOIN `{players}` p ON p.steam_id=r.player_steam_id
        WHERE m.name='surf_benchmark' AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
        ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 10
        """, 100);
    var pbLatency = await MeasureAsync(admin, $"""
        SELECT r.best_time_us,r.completions,
          1+(SELECT COUNT(*) FROM `{records}` f WHERE f.map_id=r.map_id AND f.route_type='main'
             AND f.route_index=0 AND f.style=0 AND f.mode='surf' AND f.best_time_us<r.best_time_us)
        FROM `{records}` r JOIN `{maps}` m ON m.id=r.map_id
        WHERE m.name='surf_benchmark' AND r.player_steam_id={querySteamId}
          AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
        """, 100);

    Console.WriteLine($"Top-ten latency (100 queries): avg={topLatency.Average:F3}ms p95={topLatency.P95:F3}ms max={topLatency.Maximum:F3}ms");
    Console.WriteLine($"PB+rank latency (100 queries): avg={pbLatency.Average:F3}ms p95={pbLatency.P95:F3}ms max={pbLatency.Maximum:F3}ms");

    const ulong raceSteamId = 99_999_999_999_999_999UL;
    await ExecuteAsync(admin, $"INSERT INTO `{players}` VALUES ({raceSteamId},'Concurrent Surfer')");
    var attempts = Enumerable.Range(0, 64)
        .Select(i => (Time: 60_000_000L - i * 125_000L, Server: i % 2 == 0 ? "surf-1" : "surf-3"))
        .OrderBy(_ => Random.Shared.Next()).ToArray();
    await Task.WhenAll(attempts.Select(value => SaveAttemptWithRetryAsync(raceSteamId, value.Time, value.Server)));

    await using var verify = new MySqlCommand($"""
        SELECT r.best_time_us,r.completions,r.last_server_id,s.split_time_us,rp.duration_us,
          (SELECT COUNT(*) FROM `{records}` WHERE player_steam_id={raceSteamId})
        FROM `{records}` r JOIN `{splits}` s ON s.record_id=r.id AND s.checkpoint=1
        JOIN `{replays}` rp ON rp.record_id=r.id WHERE r.player_steam_id={raceSteamId}
        """, admin);
    await using var reader = await verify.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) throw new InvalidOperationException("Concurrent record was not created.");
    var expected = attempts.Min(value => value.Time);
    var best = reader.GetInt64(0);
    var completions = reader.GetInt32(1);
    var split = reader.GetInt64(3);
    var replayDuration = reader.GetInt64(4);
    var recordCount = reader.GetInt32(5);
    if (best != expected || completions != attempts.Length || split != expected / 2 ||
        replayDuration != expected || recordCount != 1)
        throw new InvalidOperationException($"Concurrency invariant failed: best={best}, completions={completions}, split={split}, replay={replayDuration}, rows={recordCount}.");
    Console.WriteLine($"Concurrent PB test passed: {attempts.Length} writes, one row, best={best}us, replay/split aligned.");

    async Task SeedAsync(MySqlConnection connection, int count)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var player = new MySqlCommand($"INSERT INTO `{players}` VALUES (@steam,@name)", connection, transaction);
        player.Parameters.Add("@steam", MySqlDbType.UInt64); player.Parameters.Add("@name", MySqlDbType.VarChar);
        await using var record = new MySqlCommand($"""
            INSERT INTO `{records}` (map_id,player_steam_id,best_time_us,completions,pb_updated_at,last_server_id)
            VALUES (1,@steam,@time,1,UTC_TIMESTAMP(6),'seed')
            """, connection, transaction);
        record.Parameters.Add("@steam", MySqlDbType.UInt64); record.Parameters.Add("@time", MySqlDbType.Int64);
        for (var i = 0; i < count; i++)
        {
            var steam = 90_000_000_000_000_000UL + (ulong)i;
            player.Parameters[0].Value = steam; player.Parameters[1].Value = $"Fake Surfer {i}";
            await player.ExecuteNonQueryAsync();
            record.Parameters[0].Value = steam; record.Parameters[1].Value = 30_000_000L + i * 1_000L;
            await record.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    async Task SaveAttemptWithRetryAsync(ulong steamId, long time, string server)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try { await SaveAttemptAsync(steamId, time, server); return; }
            catch (MySqlException exception) when (exception.Number is 1062 or 1205 or 1213 && attempt < 5)
            { await Task.Delay(attempt * 10); }
        }
    }

    async Task SaveAttemptAsync(ulong steamId, long time, string server)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long? id = null; long? previous = null;
        await using (var select = new MySqlCommand($"SELECT id,best_time_us FROM `{records}` WHERE map_id=1 AND player_steam_id=@steam AND route_type='main' AND route_index=0 AND style=0 AND mode='surf' FOR UPDATE", connection, transaction))
        {
            select.Parameters.AddWithValue("@steam", steamId);
            await using var row = await select.ExecuteReaderAsync();
            if (await row.ReadAsync()) { id = row.GetInt64(0); previous = row.GetInt64(1); }
        }
        var isPb = previous is null || time < previous;
        if (id is null)
        {
            await using var insert = new MySqlCommand($"INSERT INTO `{records}` (map_id,player_steam_id,best_time_us,completions,pb_updated_at,last_server_id) VALUES (1,@steam,@time,1,UTC_TIMESTAMP(6),@server); SELECT LAST_INSERT_ID()", connection, transaction);
            insert.Parameters.AddWithValue("@steam", steamId); insert.Parameters.AddWithValue("@time", time); insert.Parameters.AddWithValue("@server", server);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync());
        }
        else
        {
            await using var update = new MySqlCommand($"UPDATE `{records}` SET completions=completions+1,last_server_id=@server,best_time_us=IF(@pb=1,@time,best_time_us),pb_updated_at=IF(@pb=1,UTC_TIMESTAMP(6),pb_updated_at) WHERE id=@id", connection, transaction);
            update.Parameters.AddWithValue("@server", server); update.Parameters.AddWithValue("@pb", isPb ? 1 : 0); update.Parameters.AddWithValue("@time", time); update.Parameters.AddWithValue("@id", id.Value);
            await update.ExecuteNonQueryAsync();
        }
        if (isPb)
        {
            await using var split = new MySqlCommand($"INSERT INTO `{splits}` VALUES (@id,1,@split) ON DUPLICATE KEY UPDATE split_time_us=VALUES(split_time_us)", connection, transaction);
            split.Parameters.AddWithValue("@id", id.Value); split.Parameters.AddWithValue("@split", time / 2); await split.ExecuteNonQueryAsync();
            await using var replay = new MySqlCommand($"INSERT INTO `{replays}` VALUES (@id,@duration,@frames) ON DUPLICATE KEY UPDATE duration_us=VALUES(duration_us),compressed_frames=VALUES(compressed_frames)", connection, transaction);
            replay.Parameters.AddWithValue("@id", id.Value); replay.Parameters.AddWithValue("@duration", time); replay.Parameters.AddWithValue("@frames", new byte[] { 1, 2, 3 }); await replay.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
finally
{
    await ExecuteAsync(admin, $"DROP TABLE IF EXISTS `{replays}`,`{splits}`,`{records}`,`{maps}`,`{players}`");
    Console.WriteLine("Disposable benchmark tables removed.");
}

static async Task ExecuteAsync(MySqlConnection connection, string sql)
{
    await using var command = new MySqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<(double Average, double P95, double Maximum)> MeasureAsync(MySqlConnection connection, string sql, int iterations)
{
    var values = new double[iterations];
    for (var i = 0; i < iterations; i++)
    {
        var timer = Stopwatch.StartNew();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
        values[i] = timer.Elapsed.TotalMilliseconds;
    }
    Array.Sort(values);
    return (values.Average(), values[(int)Math.Ceiling(values.Length * .95) - 1], values[^1]);
}
