using System.Globalization;
using MySqlConnector;

return await SharpTimerImporter.RunAsync(args);

internal static class SharpTimerImporter
{
    private const string SourceEnvironment = "SURFTIMER_IMPORT_SOURCE";
    private const string TargetEnvironment = "SURFTIMER_IMPORT_TARGET";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ImportOptions.Parse(args);
            if (options.Help)
            {
                PrintHelp();
                return 0;
            }

            await using var source = new MySqlConnection(options.SourceConnection);
            await using var target = new MySqlConnection(options.TargetConnection);
            await source.OpenAsync();
            await target.OpenAsync();

            await ValidateSchemaAsync(source, options.PlayerStatsTable);
            await ValidateTargetSchemaAsync(target);
            var rows = await ReadSourceAsync(source, options);
            PrintSummary(rows, options);

            if (!options.Commit)
            {
                Console.WriteLine("Dry run only; no target rows were changed. Pass --commit to import this selection.");
                return 0;
            }

            var result = await ImportAsync(target, rows, options);
            Console.WriteLine($"Committed: players={result.Players}, maps={result.Maps}, records={result.Records}.");
            Console.WriteLine("The operation is idempotent: rerunning it preserves faster local PBs and does not add completions twice.");
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Argument error: {exception.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Import failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task ValidateSchemaAsync(MySqlConnection connection, string playerStatsTable)
    {
        await RequireColumnsAsync(connection, "PlayerRecords",
            "MapName", "SteamID", "PlayerName", "TimerTicks", "UnixStamp", "LastFinished", "TimesFinished", "Style", "Mode");
        await RequireColumnsAsync(connection, playerStatsTable,
            "SteamID", "PlayerName", "TimesConnected", "LastConnected");
    }

    private static async Task ValidateTargetSchemaAsync(MySqlConnection connection)
    {
        await RequireColumnsAsync(connection, "st_players", "steam_id", "last_name", "first_seen_at", "last_seen_at");
        await RequireColumnsAsync(connection, "st_maps", "id", "name");
        await RequireColumnsAsync(connection, "st_records", "map_id", "player_steam_id", "best_time_us", "completions");
    }

    private static async Task RequireColumnsAsync(MySqlConnection connection, string table, params string[] required)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table";
        command.Parameters.AddWithValue("@table", table);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));
        var missing = required.Where(column => !found.Contains(column)).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException($"Table {table} is missing required columns: {string.Join(", ", missing)}.");
    }

    private static async Task<IReadOnlyList<SourceRecord>> ReadSourceAsync(MySqlConnection source, ImportOptions options)
    {
        var stats = new Dictionary<ulong, PlayerStats>();
        await using (var command = source.CreateCommand())
        {
            command.CommandText = $"SELECT SteamID,PlayerName,TimesConnected,LastConnected FROM `{options.PlayerStatsTable}`";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!TrySteamId(reader.GetValue(0), out var steamId)) continue;
                stats[steamId] = new PlayerStats(
                    CleanName(reader.GetValue(1)?.ToString()),
                    Math.Max(1, ToInt(reader.GetValue(2), 1)),
                    ToLong(reader.GetValue(3)));
            }
        }

        var records = new List<SourceRecord>();
        await using (var command = source.CreateCommand())
        {
            command.CommandText = """
                SELECT MapName,SteamID,PlayerName,TimerTicks,UnixStamp,LastFinished,TimesFinished
                FROM PlayerRecords
                WHERE Style=0 AND LOWER(Mode)=LOWER(@mode) AND MapName LIKE 'surf\_%' ESCAPE '\\'
                ORDER BY MapName,SteamID
                """;
            command.Parameters.AddWithValue("@mode", options.SourceMode);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var map = reader.GetValue(0)?.ToString()?.Trim() ?? string.Empty;
                if (map.Length is 0 or > 255 || !TrySteamId(reader.GetValue(1), out var steamId)) continue;
                var ticks = ToInt(reader.GetValue(3));
                if (ticks <= 0) continue;
                var statsRow = stats.GetValueOrDefault(steamId);
                var name = CleanName(reader.GetValue(2)?.ToString());
                if (string.IsNullOrWhiteSpace(name)) name = statsRow?.Name ?? steamId.ToString(CultureInfo.InvariantCulture);
                records.Add(new SourceRecord(map, steamId, name, TicksToMicroseconds(ticks),
                    Math.Max(1, ToInt(reader.GetValue(6), 1)), ToLong(reader.GetValue(4)),
                    ToLong(reader.GetValue(5)), statsRow));
            }
        }
        return records;
    }

    private static async Task<ImportResult> ImportAsync(
        MySqlConnection target, IReadOnlyList<SourceRecord> rows, ImportOptions options)
    {
        await using var transaction = await target.BeginTransactionAsync();
        try
        {
            foreach (var playerGroup in rows.GroupBy(row => row.SteamId))
            {
                var newest = playerGroup.OrderByDescending(row => Math.Max(row.LastFinished, row.PbUnix)).First();
                var stats = newest.Stats;
                var first = UnixOrFallback(playerGroup.Min(row => PositiveOrMax(row.PbUnix)), DateTime.UtcNow);
                var lastUnix = new[] { stats?.LastConnected ?? 0, playerGroup.Max(row => row.LastFinished), playerGroup.Max(row => row.PbUnix) }.Max();
                var last = UnixOrFallback(lastUnix, first);
                await ExecuteAsync(target, transaction, """
                    INSERT INTO st_players
                        (steam_id,last_name,first_seen_at,last_seen_at,first_server_id,last_server_id,total_connections)
                    VALUES (@steam,@name,@first,@last,@server,@server,@connections)
                    ON DUPLICATE KEY UPDATE
                        last_name=VALUES(last_name), first_seen_at=LEAST(first_seen_at,VALUES(first_seen_at)),
                        last_seen_at=GREATEST(last_seen_at,VALUES(last_seen_at)),
                        total_connections=GREATEST(total_connections,VALUES(total_connections))
                    """, ("@steam", newest.SteamId), ("@name", newest.Name), ("@first", first), ("@last", last),
                    ("@server", options.ServerId), ("@connections", stats?.Connections ?? 1));
            }

            foreach (var map in rows.Select(row => row.Map).Distinct(StringComparer.OrdinalIgnoreCase))
                await ExecuteAsync(target, transaction, """
                    INSERT INTO st_maps (name,workshop_id,checkpoint_count,created_at,updated_at)
                    VALUES (@map,NULL,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                    ON DUPLICATE KEY UPDATE updated_at=updated_at
                    """, ("@map", map));

            foreach (var row in rows)
            {
                var pbAt = UnixOrFallback(row.PbUnix, DateTime.UtcNow);
                var lastAt = UnixOrFallback(row.LastFinished, pbAt);
                await ExecuteAsync(target, transaction, """
                    INSERT INTO st_records
                        (map_id,player_steam_id,route_type,route_index,style,mode,best_time_us,completions,
                         first_completed_at,last_completed_at,pb_updated_at,last_server_id)
                    SELECT id,@steam,'main',0,0,'surf',@time,@completions,@pb,@last,@pb,@server
                    FROM st_maps WHERE name=@map
                    ON DUPLICATE KEY UPDATE
                        pb_updated_at=IF(VALUES(best_time_us)<best_time_us,VALUES(pb_updated_at),pb_updated_at),
                        last_server_id=IF(VALUES(last_completed_at)>last_completed_at,VALUES(last_server_id),last_server_id),
                        completions=GREATEST(completions,VALUES(completions)),
                        first_completed_at=LEAST(first_completed_at,VALUES(first_completed_at)),
                        last_completed_at=GREATEST(last_completed_at,VALUES(last_completed_at)),
                        best_time_us=LEAST(best_time_us,VALUES(best_time_us))
                    """, ("@steam", row.SteamId), ("@time", row.TimeMicroseconds),
                    ("@completions", row.Completions), ("@pb", pbAt), ("@last", lastAt),
                    ("@server", options.ServerId), ("@map", row.Map));
            }

            await transaction.CommitAsync();
            return new ImportResult(rows.Select(row => row.SteamId).Distinct().Count(),
                rows.Select(row => row.Map).Distinct(StringComparer.OrdinalIgnoreCase).Count(), rows.Count);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static void PrintSummary(IReadOnlyList<SourceRecord> rows, ImportOptions options)
    {
        Console.WriteLine($"SharpTimer selection: mode={options.SourceMode}, style=0, route=main, maps=surf_*.");
        Console.WriteLine($"Validated rows: records={rows.Count}, players={rows.Select(r => r.SteamId).Distinct().Count()}, maps={rows.Select(r => r.Map).Distinct(StringComparer.OrdinalIgnoreCase).Count()}.");
        Console.WriteLine("SharpTimer stage times and replay files are intentionally not imported; neither represents SurfTimer PB splits/replays directly.");
    }

    private static bool TrySteamId(object value, out ulong steamId) =>
        ulong.TryParse(value?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out steamId) && steamId > 0;
    private static string CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim()[..Math.Min(name.Trim().Length, 64)];
    private static int ToInt(object value, int fallback = 0) => int.TryParse(value?.ToString(), out var number) ? number : fallback;
    private static long ToLong(object value) => long.TryParse(value?.ToString(), out var number) ? number : 0;
    private static long TicksToMicroseconds(int ticks) => ((long)ticks * 1_000_000L + 32L) / 64L;
    private static long PositiveOrMax(long value) => value > 0 ? value : long.MaxValue;
    private static DateTime UnixOrFallback(long unix, DateTime fallback)
    {
        if (unix <= 0 || unix > 253402300799) return DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    }

    private static void PrintHelp() => Console.WriteLine($$"""
        Imports latest Martian/poor-SharpTimer MariaDB records into SurfTimer.

        Usage:
          dotnet run --project tools/sharptimer-import -- [options]

        Connection strings are read from {{SourceEnvironment}} and {{TargetEnvironment}}.
        If the target variable is omitted, the source connection is also used as the target.

        Options:
          --commit                    Apply the import (default is a read-only dry run)
          --mode <name>               SharpTimer mode to import (default: Standard)
          --player-stats <table>      PlayerStats table, including SharpTimer prefix (default: PlayerStats)
          --server-id <id>            Provenance server ID (default: legacy-sharptimer)
          --help                      Show this help
        """);

    private sealed record PlayerStats(string Name, int Connections, long LastConnected);
    private sealed record SourceRecord(string Map, ulong SteamId, string Name, long TimeMicroseconds,
        int Completions, long PbUnix, long LastFinished, PlayerStats? Stats);
    private sealed record ImportResult(int Players, int Maps, int Records);

    private sealed record ImportOptions(bool Help, bool Commit, string SourceConnection, string TargetConnection,
        string SourceMode, string PlayerStatsTable, string ServerId)
    {
        public static ImportOptions Parse(string[] args)
        {
            var help = false;
            var commit = false;
            var mode = "Standard";
            var stats = "PlayerStats";
            var server = "legacy-sharptimer";
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h": help = true; break;
                    case "--commit": commit = true; break;
                    case "--mode": mode = Next(args, ref i, "--mode"); break;
                    case "--player-stats": stats = Next(args, ref i, "--player-stats"); break;
                    case "--server-id": server = Next(args, ref i, "--server-id"); break;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }
            if (!IsIdentifier(stats)) throw new ArgumentException("--player-stats must contain only letters, digits, or underscores.");
            if (string.IsNullOrWhiteSpace(server) || server.Length > 64) throw new ArgumentException("--server-id must be 1-64 characters.");
            var source = Environment.GetEnvironmentVariable(SourceEnvironment) ?? string.Empty;
            var target = Environment.GetEnvironmentVariable(TargetEnvironment) ?? source;
            if (!help && string.IsNullOrWhiteSpace(source)) throw new ArgumentException($"Set {SourceEnvironment} to the SharpTimer MariaDB connection string.");
            return new ImportOptions(help, commit, source, target, mode, stats, server);
        }

        private static string Next(string[] args, ref int index, string option) =>
            ++index < args.Length ? args[index] : throw new ArgumentException($"{option} requires a value.");
        private static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 && value.All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}
