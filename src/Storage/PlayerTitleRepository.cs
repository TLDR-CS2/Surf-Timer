using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Titles;
using SwiftlyS2.Shared;

namespace SurfTimer.Storage;

public sealed class PlayerTitleRepository(ISwiftlyCore core, SurfTimerOptions options, MigrationRunner migrations,
    ILogger<PlayerTitleRepository> logger)
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _ready;
    public void Start() => _ready ??= Task.Run(() => migrations.ApplyAsync(_shutdown.Token));
    public void Stop() { _shutdown.Cancel(); _shutdown.Dispose(); }

    public async Task<PlayerTitleSettings> LoadAsync(ulong steamId)
    {
        await ReadyAsync(); await using var connection = await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_vip,custom_title,color_pattern,name_color FROM st_player_titles WHERE player_steam_id=@steam";
        command.AddParameter("@steam", steamId); await using var reader = await command.ExecuteReaderAsync(_shutdown.Token);
        return await reader.ReadAsync(_shutdown.Token)
            ? new(Convert.ToBoolean(reader.GetValue(0)), reader.IsDBNull(1)?null:reader.GetString(1), reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?"default":reader.GetString(3))
            : new(false,null,null,"default");
    }

    public async Task SaveNameColorAsync(ulong steamId,string color)
    {
        await ReadyAsync();await using var connection=await core.Database.OpenConnectionAsync(options.DatabaseConnection,_shutdown.Token);
        await using var command=connection.CreateCommand();command.CommandText="UPDATE st_player_titles SET name_color=@color,updated_at=UTC_TIMESTAMP(6) WHERE player_steam_id=@steam AND is_vip=TRUE";
        command.AddParameter("@steam",steamId);command.AddParameter("@color",color);await command.ExecuteNonQueryAsync(_shutdown.Token);
    }

    public async Task SaveSelectionAsync(ulong steamId, string? title, string? colors)
    {
        await ReadyAsync(); await using var connection = await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token);
        await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO st_player_titles(player_steam_id,is_vip,custom_title,color_pattern,updated_at)
            VALUES(@steam,FALSE,@title,@colors,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE custom_title=VALUES(custom_title),color_pattern=VALUES(color_pattern),updated_at=UTC_TIMESTAMP(6)
            """;
        command.AddParameter("@steam",steamId); command.AddParameter("@title",title); command.AddParameter("@colors",colors);
        await command.ExecuteNonQueryAsync(_shutdown.Token);
    }

    public async Task SetVipAsync(ulong steamId, bool enabled)
    {
        await ReadyAsync(); await using var connection = await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token);
        await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO st_player_titles(player_steam_id,is_vip,updated_at) VALUES(@steam,@vip,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE is_vip=VALUES(is_vip),custom_title=IF(VALUES(is_vip),custom_title,NULL),
                color_pattern=IF(VALUES(is_vip),color_pattern,NULL),updated_at=UTC_TIMESTAMP(6)
            """;
        command.AddParameter("@steam",steamId); command.AddParameter("@vip",enabled?1:0);
        await command.ExecuteNonQueryAsync(_shutdown.Token);
    }

    private async Task ReadyAsync(){Start();try{await _ready!;}catch(Exception e){logger.LogError(e,"Player title storage is unavailable.");throw;}}
}
