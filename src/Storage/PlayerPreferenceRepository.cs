using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Players;
using SwiftlyS2.Shared;

namespace SurfTimer.Storage;

public sealed class PlayerPreferenceRepository(
    ISwiftlyCore core,
    SurfTimerOptions options,
    MigrationRunner migrations,
    ILogger<PlayerPreferenceRepository> logger)
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _ready;

    public void Start() => _ready ??= Task.Run(() => migrations.ApplyAsync(_shutdown.Token));

    public void Stop()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    public async Task<PlayerPreferences> LoadAsync(ulong steamId)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hud_enabled,speed_enabled,status_enabled,keys_enabled,sounds_enabled,replay_hud_enabled
            FROM st_player_preferences WHERE player_steam_id=@steam
            """;
        command.AddParameter("@steam", steamId);
        await using var reader = await command.ExecuteReaderAsync(_shutdown.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_shutdown.Token).ConfigureAwait(false)) return new PlayerPreferences();
        return new PlayerPreferences(
            Convert.ToBoolean(reader.GetValue(0)), Convert.ToBoolean(reader.GetValue(1)),
            Convert.ToBoolean(reader.GetValue(2)), Convert.ToBoolean(reader.GetValue(3)),
            Convert.ToBoolean(reader.GetValue(4)), Convert.ToBoolean(reader.GetValue(5)));
    }

    public async Task SaveAsync(ulong steamId, PlayerPreferences preferences)
    {
        await ReadyAsync().ConfigureAwait(false);
        await using var connection = await core.Database.OpenConnectionAsync(options.DatabaseConnection, _shutdown.Token)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO st_player_preferences
                (player_steam_id,hud_enabled,speed_enabled,status_enabled,keys_enabled,sounds_enabled,replay_hud_enabled,updated_at)
            VALUES (@steam,@hud,@speed,@status,@keys,@sounds,@replay,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE hud_enabled=VALUES(hud_enabled),speed_enabled=VALUES(speed_enabled),
                status_enabled=VALUES(status_enabled),keys_enabled=VALUES(keys_enabled),
                sounds_enabled=VALUES(sounds_enabled),replay_hud_enabled=VALUES(replay_hud_enabled),
                updated_at=UTC_TIMESTAMP(6)
            """;
        command.AddParameter("@steam", steamId);
        command.AddParameter("@hud", preferences.HudEnabled ? 1 : 0);
        command.AddParameter("@speed", preferences.SpeedEnabled ? 1 : 0);
        command.AddParameter("@status", preferences.StatusEnabled ? 1 : 0);
        command.AddParameter("@keys", preferences.KeysEnabled ? 1 : 0);
        command.AddParameter("@sounds", preferences.SoundsEnabled ? 1 : 0);
        command.AddParameter("@replay", preferences.ReplayHudEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(_shutdown.Token).ConfigureAwait(false);
    }

    public async Task SaveSafelyAsync(ulong steamId, PlayerPreferences preferences)
    {
        try { await SaveAsync(steamId, preferences).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogError(exception, "Failed to save preferences for {SteamId}.", steamId); }
    }

    private async Task ReadyAsync()
    {
        Start();
        await _ready!.ConfigureAwait(false);
    }
}
