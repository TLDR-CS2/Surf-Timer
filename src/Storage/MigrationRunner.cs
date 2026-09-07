using System.Data.Common;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace SurfTimer.Storage;

public sealed class MigrationRunner(
    ISwiftlyCore core,
    Configuration.SurfTimerOptions options,
    ILogger<MigrationRunner> logger)
{
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await core.Database
            .OpenConnectionAsync(options.DatabaseConnection, cancellationToken)
            .ConfigureAwait(false);

        if (!await AcquireLockAsync(connection, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting for the SurfTimer migration lock.");

        try
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS st_schema_migrations (
                    version INT UNSIGNED NOT NULL,
                    name VARCHAR(255) NOT NULL,
                    applied_at DATETIME(6) NOT NULL,
                    PRIMARY KEY (version)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """, cancellationToken).ConfigureAwait(false);

            await ApplyFileAsync(connection, 1, "001_initial.sql", "initial players, maps, records, and splits", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 2, "002_replays.sql", "versioned compressed PB replays", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 3, "003_map_metadata.sql", "map tier and enabled metadata", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 4, "004_player_preferences.sql", "global player HUD and audio preferences", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 5, "005_keys_enabled_default.sql", "enable key display for new players", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 6, "006_admin_audit.sql", "administrative action audit log", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 7, "007_stage_records.sql", "global per-stage records", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 8, "008_leaderboard_covering_indexes.sql", "cover deterministic leaderboard ordering", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 9, "009_run_validation.sql", "versioned non-punitive run telemetry", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 10, "010_global_player_statistics.sql", "global player statistics and PB history", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 11, "011_stage_replays.sql", "global per-stage PB replays", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 12, "012_stage_pb_history.sql", "global per-stage PB history", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 13, "013_map_route_counts.sql", "authoritative map stage and bonus counts", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 14, "014_player_titles.sql", "VIP title entitlement and display preferences", cancellationToken)
                .ConfigureAwait(false);
            await ApplyFileAsync(connection, 15, "015_title_chat_colors.sql", "expanded tag and name colour preferences", cancellationToken).ConfigureAwait(false);
            await ApplyFileAsync(connection, 16, "016_run_receipts.sql", "idempotent completed runs and catalog authority", cancellationToken).ConfigureAwait(false);
            await ApplyFileAsync(connection, 17, "017_record_archives.sql", "reversible record moderation evidence", cancellationToken).ConfigureAwait(false);
            await ApplyFileAsync(connection, 18, "018_ruleset_provenance.sql", "run ruleset provenance", cancellationToken).ConfigureAwait(false);
            await ApplyFileAsync(connection, 19, "019_competitive_rulesets.sql", "approved competitive map rulesets", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteScalarAsync(connection, "SELECT RELEASE_LOCK('surftimer_schema_migrations')", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ApplyFileAsync(DbConnection connection, int version, string fileName, string name, CancellationToken token)
    {
        var path = Path.Combine(core.PluginPath, "resources", "migrations", "mysql", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("SurfTimer migration resource was not deployed.", path);
        var sql = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS st_migration_checksums(version INT NOT NULL PRIMARY KEY,checksum CHAR(64) NOT NULL)", token).ConfigureAwait(false);
        await using (var checksum = connection.CreateCommand())
        {
            checksum.CommandText = "SELECT checksum FROM st_migration_checksums WHERE version=@version";
            checksum.AddParameter("@version", version);
            var existing = await checksum.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is not null && Convert.ToString(existing) != MigrationRecovery.Checksum(sql))
                throw new InvalidDataException($"Migration {version} changed after deployment.");
            checksum.CommandText = "INSERT IGNORE INTO st_migration_checksums(version,checksum) VALUES(@version,@checksum)";
            checksum.AddParameter("@checksum", MigrationRecovery.Checksum(sql));
            await checksum.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (await IsAppliedAsync(connection, version, token).ConfigureAwait(false)) return;
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var resumable = await MigrationRecovery.RemoveExistingAddedColumnsAsync(connection, statement, token).ConfigureAwait(false);
            if (resumable is not null) await ExecuteAsync(connection, resumable, token).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO st_schema_migrations (version,name,applied_at) VALUES (@version,@name,UTC_TIMESTAMP(6))";
        command.AddParameter("@version", version); command.AddParameter("@name", name);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        logger.LogInformation("Applied database migration {Version}: {Name}.", version, name);
    }

    private static async Task<bool> AcquireLockAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Convert.ToInt32(await ExecuteScalarAsync(
            connection, "SELECT GET_LOCK('surftimer_schema_migrations', 30)", cancellationToken).ConfigureAwait(false)) == 1;

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(DbConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM st_schema_migrations WHERE version = @version";
        command.AddParameter("@version", version);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}



