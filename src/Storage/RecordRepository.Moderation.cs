using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurfTimer.Storage;

public sealed partial class RecordRepository
{
    private static readonly HashSet<string> ArchiveTables = ["st_records", "st_stage_records", "st_replays", "st_record_splits", "st_run_validation", "st_pb_history", "st_stage_replays", "st_stage_pb_history"];

    public async Task<Guid?> InvalidateRecordAsync(ulong steamId, string map, string route, int index, ulong actor, string reason)
    {
        if (route is not ("main" or "stage" or "bonus") || (route == "main" ? index != 0 : index < 1)) throw new ArgumentException("Invalid record route.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024) throw new ArgumentException("Provide a reason of 1–1024 characters.");
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        var stage = route == "stage";
        var table = stage ? "st_stage_records" : "st_records";
        long id;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT r.id FROM {table} r JOIN st_maps m ON m.id=r.map_id WHERE m.name=@map AND r.player_steam_id=@steam AND " +
                (stage ? "r.stage=@index" : "r.route_type=@route AND r.route_index=@index AND r.style=0 AND r.mode='surf'") + " FOR UPDATE";
            select.AddParameter("@map", map); select.AddParameter("@steam", steamId); select.AddParameter("@index", index); select.AddParameter("@route", route);
            var value = await select.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false);
            if (value is null) return null;
            id = Convert.ToInt64(value);
        }
        var payload = new List<ArchivedTable> { await SnapshotAsync(connection, transaction, table, "id", id).ConfigureAwait(false) };
        foreach (var dependency in stage ? new[] { "st_stage_replays", "st_stage_pb_history" } : new[] { "st_replays", "st_record_splits", "st_run_validation", "st_pb_history" })
            payload.Add(await SnapshotAsync(connection, transaction, dependency, stage ? "stage_record_id" : "record_id", id).ConfigureAwait(false));
        var archiveId = Guid.NewGuid();
        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = "INSERT INTO st_record_archives(archive_id,player_steam_id,map_name,route_type,route_index,actor_steam_id,reason,payload,invalidated_at) VALUES(@id,@steam,@map,@route,@index,@actor,@reason,@payload,UTC_TIMESTAMP(6))";
            archive.AddParameter("@id", archiveId.ToString("N")); archive.AddParameter("@steam", steamId); archive.AddParameter("@map", map);
            archive.AddParameter("@route", route); archive.AddParameter("@index", index); archive.AddParameter("@actor", actor);
            archive.AddParameter("@reason", reason); archive.AddParameter("@payload", JsonSerializer.Serialize(payload));
            await archive.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction; delete.CommandText = $"DELETE FROM {table} WHERE id=@id"; delete.AddParameter("@id", id);
            await delete.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
        return archiveId;
    }

    public async Task<bool> RestoreRecordAsync(Guid archiveId, ulong actor)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(_shutdown.Token).ConfigureAwait(false);
        string payload;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT payload FROM st_record_archives WHERE archive_id=@id AND restored_at IS NULL FOR UPDATE";
            select.AddParameter("@id", archiveId.ToString("N"));
            var value = await select.ExecuteScalarAsync(_shutdown.Token).ConfigureAwait(false);
            if (value is null) return false;
            payload = Convert.ToString(value)!;
        }
        foreach (var table in JsonSerializer.Deserialize<List<ArchivedTable>>(payload) ?? throw new InvalidDataException("Empty archive."))
        {
            if (!ArchiveTables.Contains(table.Table)) throw new InvalidDataException("Unknown archive table.");
            foreach (var row in table.Rows)
            {
                var columns = row.Keys.ToArray();
                if (columns.Any(column => !Regex.IsMatch(column, @"^\w+$"))) throw new InvalidDataException("Invalid archive column.");
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                // Plain INSERT deliberately refuses to replace a newer active PB or overwrite any evidence.
                insert.CommandText = $"INSERT INTO {table.Table} ({string.Join(',', columns.Select(column => $"`{column}`"))}) VALUES ({string.Join(',', columns.Select((_, i) => $"@p{i}"))})";
                for (var i = 0; i < columns.Length; i++) insert.AddParameter($"@p{i}", row[columns[i]].ToValue());
                await insert.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE st_record_archives SET restored_at=UTC_TIMESTAMP(6),restored_by=@actor WHERE archive_id=@id";
            update.AddParameter("@actor", actor); update.AddParameter("@id", archiveId.ToString("N"));
            await update.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
        return true;
    }

    private async Task<ArchivedTable> SnapshotAsync(DbConnection connection, DbTransaction transaction, string table, string key, long id)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT * FROM {table} WHERE {key}=@id FOR UPDATE"; command.AddParameter("@id", id);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        var rows = new List<Dictionary<string, ArchivedValue>>();
        while (await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false))
        {
            var row = new Dictionary<string, ArchivedValue>();
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), ArchivedValue.From(reader.GetValue(i)));
            rows.Add(row);
        }
        return new(table, rows);
    }
}

