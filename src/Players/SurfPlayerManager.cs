using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SurfTimer.Storage;

namespace SurfTimer.Players;

public sealed class SurfPlayerManager(
    ISwiftlyCore core,
    RecordRepository records,
    PlayerPreferenceRepository preferences,
    ILogger<SurfPlayerManager> logger)
{
    private readonly Dictionary<int, SurfPlayerSession> _sessions = [];
    private readonly List<Guid> _gameEventHooks = [];
    private bool _started;

    public int Count => _sessions.Count;
    public IReadOnlyCollection<SurfPlayerSession> Sessions => _sessions.Values;

    public void Start(bool hotReload)
    {
        if (_started) return;
        _started = true;

        core.Event.OnClientPutInServer += OnClientPutInServer;
        core.Event.OnClientSteamAuthorize += OnClientSteamAuthorize;
        core.Event.OnClientDisconnected += OnClientDisconnected;
        _gameEventHooks.Add(core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn));
        _gameEventHooks.Add(core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeath));
        _gameEventHooks.Add(core.GameEvent.HookPost<EventPlayerTeam>(OnPlayerTeam));

        if (hotReload)
        {
            foreach (var player in core.PlayerManager.GetAllPlayers())
            {
                AddOrRefresh(player);
            }
        }

        logger.LogInformation("Player manager started with {PlayerCount} restored sessions.", Count);
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnClientPutInServer -= OnClientPutInServer;
        core.Event.OnClientSteamAuthorize -= OnClientSteamAuthorize;
        core.Event.OnClientDisconnected -= OnClientDisconnected;
        foreach (var hook in _gameEventHooks) core.GameEvent.Unhook(hook);
        _gameEventHooks.Clear();
        _sessions.Clear();
        _started = false;
    }

    public SurfPlayerSession? Get(int playerId) =>
        _sessions.GetValueOrDefault(playerId);

    private void OnClientPutInServer(IOnClientPutInServerEvent gameEvent)
    {
        var player = core.PlayerManager.GetPlayer(gameEvent.PlayerId);
        if (player is not null) AddOrRefresh(player);
    }

    private void OnClientSteamAuthorize(IOnClientSteamAuthorizeEvent gameEvent)
    {
        var player = core.PlayerManager.GetPlayer(gameEvent.PlayerId);
        if (player is null) return;
        var session = AddOrRefresh(player);
        session.MarkAuthorized(player.SteamID);
        _ = records.UpsertPlayerConnectionAsync(player.SteamID, player.Name);
        _ = LoadPreferencesAsync(session.PlayerId, session.SessionId, player.SteamID);
        logger.LogInformation("Player authorized: {Name} ({SteamId}, player {PlayerId}).",
            player.Name, player.SteamID, player.PlayerID);
    }

    private async Task LoadPreferencesAsync(int playerId, ulong sessionId, ulong steamId)
    {
        try
        {
            var loaded = await preferences.LoadAsync(steamId).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var session = Get(playerId);
                if (session is not null && session.SessionId == sessionId && session.SteamId == steamId)
                    session.SetPreferences(loaded);
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load preferences for {SteamId}.", steamId);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent gameEvent)
    {
        if (_sessions.Remove(gameEvent.PlayerId, out var session))
        {
            logger.LogInformation("Player disconnected: {Name} (player {PlayerId}, reason {Reason}).",
                session.Name, session.PlayerId, gameEvent.Reason);
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn gameEvent)
    {
        var session = FromEventPlayer(gameEvent.UserIdPlayer);
        session?.MarkSpawned();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath gameEvent)
    {
        Get(gameEvent.UserIdPlayer?.PlayerID ?? -1)?.MarkDead();
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam gameEvent)
    {
        Get(gameEvent.UserIdPlayer?.PlayerID ?? -1)?.ChangeTeam(gameEvent.Team);
        return HookResult.Continue;
    }

    private SurfPlayerSession? FromEventPlayer(IPlayer? player) =>
        player is null ? null : AddOrRefresh(player);

    private SurfPlayerSession AddOrRefresh(IPlayer player)
    {
        if (!_sessions.TryGetValue(player.PlayerID, out var session) || session.SessionId != player.SessionId)
        {
            session = new SurfPlayerSession(player.PlayerID, player.SessionId, player.Name, player.IsFakeClient);
            _sessions[player.PlayerID] = session;
            logger.LogInformation("Player session created: {Name} (player {PlayerId}, session {SessionId}).",
                player.Name, player.PlayerID, player.SessionId);
        }

        session.Refresh(player.Name, player.IsAuthorized, player.SteamID, player.IsAlive);
        return session;
    }
}
