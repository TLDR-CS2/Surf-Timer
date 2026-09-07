using System.Data.Common;

namespace SurfTimer.Storage;

public sealed class RulesetRejectedException(string message) : Exception(message);
public sealed class RulesetAwaitingApprovalException(string map) : Exception($"Map {map} is awaiting its authority server's ruleset baseline.");
public sealed class RunQuarantinedException(Exception inner) : Exception("Run evidence retained in quarantine for administrator review.", inner);

public sealed partial class RecordRepository
{
    private async Task RequireRulesetAsync(DbConnection connection, DbTransaction transaction, string map, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint is "legacy" or "no-map")
            throw new RulesetRejectedException("Run has no verifiable ruleset fingerprint.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (string.IsNullOrWhiteSpace(options.CatalogAuthorityServerId) || options.CatalogAuthorityServerId == options.ServerId)
        {
            command.CommandText = "INSERT IGNORE INTO st_catalog_authority(id,server_id) VALUES(1,@owner)";
            command.AddParameter("@owner", options.ServerId);
            await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        // Only the configured catalog authority (or persisted owner) may establish a new map's baseline.
        command.CommandText = """
            INSERT IGNORE INTO st_competitive_rulesets(map_name,fingerprint,approved_by_server,approved_at)
            SELECT @map,@fingerprint,@server,UTC_TIMESTAMP(6) FROM st_catalog_authority
            WHERE id=1 AND server_id=@server
            """;
        command.AddParameter("@map", map); command.AddParameter("@fingerprint", fingerprint);
        command.AddParameter("@server", options.ServerId);
        if (string.IsNullOrWhiteSpace(options.CatalogAuthorityServerId) || options.CatalogAuthorityServerId == options.ServerId)
            await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        command.CommandText = "SELECT fingerprint FROM st_competitive_rulesets WHERE map_name=@map FOR UPDATE";
        var approved = Convert.ToString(await command.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false));
        if (string.IsNullOrEmpty(approved)) throw new RulesetAwaitingApprovalException(map);
        if (!string.Equals(approved, fingerprint, StringComparison.Ordinal))
            throw new RulesetRejectedException($"Map {map} has a different approved ruleset. Submitted fingerprint: {fingerprint}.");
    }
}
