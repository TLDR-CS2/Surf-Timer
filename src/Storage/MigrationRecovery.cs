using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SurfTimer.Storage;

public static class MigrationRecovery
{
    public static string Checksum(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));

    // These migrations contain only literal ADD COLUMN clauses. Avoid suppressing arbitrary DDL errors.
    public static async Task<string?> RemoveExistingAddedColumnsAsync(DbConnection connection, string sql, CancellationToken token)
    {
        var match = Regex.Match(sql, @"^ALTER\s+TABLE\s+(\w+)\s+(ADD\s+COLUMN\s+.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return sql;
        var clauses = Regex.Split(match.Groups[2].Value, @",\s*(?=ADD\s+COLUMN\s)", RegexOptions.IgnoreCase);
        var remaining = new List<string>();
        foreach (var clause in clauses)
        {
            var column = Regex.Match(clause, @"^ADD\s+COLUMN\s+(\w+)\s+((?:TINYINT|SMALLINT|BIGINT|INT|VARCHAR\(\d+\)|BOOLEAN)(?:\s+UNSIGNED)?)\s+(NOT NULL|NULL)(?:\s+DEFAULT\s+('(?:[^']*)'|\w+))?(?:\s+AFTER\s+\w+)?$", RegexOptions.IgnoreCase);
            if (!column.Success) return sql;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column";
            command.AddParameter("@table", match.Groups[1].Value); command.AddParameter("@column", column.Groups[1].Value);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) { remaining.Add(clause); continue; }
            static string Type(string value) => Regex.Replace(value.ToLowerInvariant().Replace("boolean", "tinyint"), @"(?<=int)\(\d+\)", "");
            static string Default(string value) => value.Trim('\'').ToLowerInvariant() switch { "true" => "1", "false" => "0", "null" => "", var other => other };
            var expectedDefault = column.Groups[4].Value;
            var actualDefault = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2))!;
            if (Type(reader.GetString(0)) != Type(column.Groups[2].Value) ||
                reader.GetString(1) != (column.Groups[3].Value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? "YES" : "NO") ||
                Default(expectedDefault) != Default(actualDefault))
                throw new InvalidDataException($"Existing column {match.Groups[1].Value}.{column.Groups[1].Value} does not match the migration; explicit schema repair is required.");
        }
        return remaining.Count == 0 ? null : $"ALTER TABLE {match.Groups[1].Value} {string.Join(",", remaining)}";
    }
}

