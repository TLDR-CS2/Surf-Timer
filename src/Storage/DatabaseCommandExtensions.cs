using System.Data.Common;

namespace SurfTimer.Storage;

internal static class DatabaseCommandExtensions
{
    public static void AddParameter(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public static async Task<DbConnection> OpenConnectionAsync(
        this SwiftlyS2.Shared.Database.IDatabaseService database,
        string connectionName,
        CancellationToken cancellationToken = default)
    {
        if (database.GetConnection(connectionName) is not DbConnection connection)
            throw new InvalidOperationException("The configured database provider does not expose DbConnection.");

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
