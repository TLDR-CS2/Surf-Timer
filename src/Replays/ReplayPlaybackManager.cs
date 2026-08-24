using Microsoft.Extensions.Logging;
using SurfTimer.Storage;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SurfTimer.Players;
using BotControllerApi;

namespace SurfTimer.Replays;

public sealed class ReplayPlaybackManager(
    ISwiftlyCore core,
    RecordRepository records,
    SurfPlayerManager players,
    BotControllerBridge botController,
    ILogger<ReplayPlaybackManager> logger)
{
    private ReplayCapture? _capture;
    private StoredReplay? _selected;
    private string? _selectedMap;
    private int _frame;
    private CancellationTokenSource? _mapLoadTimer;
    private readonly Dictionary<ulong, ReplayViewer> _viewers = [];
    private bool _started;

    public string Description => _selected is null ? "none" : $"#{_selected.Rank} {_selected.PlayerName}";

    public void Start()
    {
        if (_started) return;
        _started = true;
        core.Event.OnTick += OnTick;
        core.Event.OnMapLoad += OnMapLoad;
        core.Event.OnMapUnload += OnMapUnload;
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnTick -= OnTick;
        core.Event.OnMapLoad -= OnMapLoad;
        core.Event.OnMapUnload -= OnMapUnload;
        Clear();
        _started = false;
    }

    public async Task<StoredReplay?> SelectAsync(string mapName, int rank)
    {
        var stored = await records.GetReplayAsync(mapName, rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => Activate(mapName, stored, decoded));
        return stored;
    }

    public async Task<StoredReplay?> SelectBonusAsync(string mapName, int bonus, int rank)
    {
        var stored = await records.GetBonusReplayAsync(mapName, bonus, rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => Activate(mapName, stored, decoded));
        return stored;
    }

    public async Task<StoredReplay?> SelectStageAsync(string mapName, int stage, int rank)
    {
        var stored = await records.GetStageReplayAsync(mapName,stage,rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(()=>Activate(mapName,stored,decoded));
        return stored;
    }

    public async Task RefreshIfSelectedAsync(string mapName)
    {
        var selected = _selected;
        if (selected is null || !string.Equals(_selectedMap, mapName, StringComparison.OrdinalIgnoreCase)) return;
        await SelectAsync(mapName, selected.Rank).ConfigureAwait(false);
    }

    public void InvalidateSelection() => core.Scheduler.NextTick(Clear);

    public void Watch(int playerId, ulong sessionId) => core.Scheduler.NextTick(() =>
    {
        if (_capture is null) return;
        var player = core.PlayerManager.GetPlayer(playerId);
        var session = players.Get(playerId);
        var pawn = player?.PlayerPawn;
        if (player is null || player.SessionId != sessionId || session is null || pawn?.AbsOrigin is not { } origin) return;
        StopWatching(sessionId, restore: true);
        var native = StartNativeReplay(playerId, pawn);
        _viewers[sessionId] = new ReplayViewer(playerId, new Vector(origin), new QAngle(pawn.EyeAngles), new Vector(pawn.AbsVelocity), native);
        session.SetWatchingReplay(true);
        player.SendChat(native
            ? "[SurfTimer] Watching native first-person replay. Type !replay stop to exit."
            : "[SurfTimer] Native replay unavailable; using compatibility playback. Type !replay stop to exit.");
    });

    public void StopWatching(ulong sessionId, bool restore = true)
    {
        if (!_viewers.Remove(sessionId, out var viewer)) return;
        var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
        var session = players.Get(viewer.PlayerId);
        if (viewer.Native) botController.Api?.StopReplay(viewer.PlayerId);
        session?.SetWatchingReplay(false);
        if (restore && player is not null && player.SessionId == sessionId)
            player.Teleport(viewer.Position, viewer.Angles, viewer.Velocity);
    }

    private void Activate(string mapName, StoredReplay stored, ReplayCapture capture)
    {
        _selectedMap = mapName; _selected = stored; _capture = capture; _frame = 0;
        foreach (var (sessionId, viewer) in _viewers.ToArray())
        {
            if (!viewer.Native) continue;
            var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
            var pawn = player?.PlayerPawn;
            if (player is null || player.SessionId != sessionId || pawn is null || !StartNativeReplay(viewer.PlayerId, pawn))
            {
                if (player is null || player.SessionId != sessionId) StopWatching(sessionId, restore: false);
                else _viewers[sessionId] = viewer with { Native = false };
            }
        }
        logger.LogInformation("Selected replay rank {Rank}: {PlayerName}, {Frames} frames.", stored.Rank, stored.PlayerName, capture.Frames.Count);
    }

    private bool StartNativeReplay(int playerId, CCSPlayerPawn pawn)
    {
        var api = botController.Api;
        if (api is null || _capture is null) return false;
        try
        {
            api.StopReplay(playerId);
            var ticks = _capture.IsNative ? _capture.NativeTicks!.ToArray() : NativeReplayAdapter.Convert(_capture);
            var subticks = _capture.NativeSubticks?.ToArray() ?? [];
            if (!api.LoadReplay(playerId, ticks, subticks) ||
                !api.SetReplayPawn(playerId, ((INativeHandle)pawn).Address))
                return false;
            pawn.ItemServices?.RemoveItems();
            if (!api.StartReplay(playerId, loop: true)) return false;
            logger.LogInformation("Started native replay on player {PlayerId}: {Ticks} ticks.", playerId, ticks.Length);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Native replay startup failed for player {PlayerId}.", playerId);
            return false;
        }
    }

    private void OnTick()
    {
        if (_viewers.Count == 0) return;
        foreach (var (sessionId, viewer) in _viewers.ToArray())
        {
            var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
            var session = players.Get(viewer.PlayerId);
            if (player is null || player.SessionId != sessionId || session is null ||
                !session.IsWatchingReplay || !session.IsAlive)
                StopWatching(sessionId, restore: false);
        }
        if (_capture is null || _capture.Frames.Count == 0 || _viewers.Count == 0) return;
        if (_frame >= _capture.Frames.Count) _frame = 0;
        var f = _capture.Frames[_frame++];
        var position = new Vector(f.X, f.Y, f.Z);
        var angles = new QAngle(f.Pitch, f.Yaw, f.Roll);
        var velocity = new Vector(f.VelocityX, f.VelocityY, f.VelocityZ);
        foreach (var (sessionId, viewer) in _viewers.ToArray())
        {
            var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
            if (player is null || player.SessionId != sessionId)
            {
                StopWatching(sessionId, restore: false);
                continue;
            }
            if (viewer.Native) continue;
            player.Teleport(position, angles, velocity);
        }
    }

    private void OnMapLoad(IOnMapLoadEvent gameEvent)
    {
        Clear();
        _mapLoadTimer = core.Scheduler.DelayBySeconds(2f, () => _ = AutoSelectAsync(gameEvent.MapName));
    }

    public bool TryGetViewerStatus(ulong sessionId, out ReplayPlaybackStatus status)
    {
        status = default;
        if (!_viewers.TryGetValue(sessionId, out var viewer) || _capture is null || _selected is null) return false;
        var cursor = viewer.Native ? botController.Api?.ReplayCursor(viewer.PlayerId) ?? 0 : _frame;
        cursor = Math.Clamp(cursor, 0, Math.Max(0, _capture.TickCount - 1));
        var elapsed = _capture.IsNative
            ? (long)cursor * _capture.DurationMicroseconds / Math.Max(1, _capture.TickCount - 1)
            : (_capture.Frames.Count == 0 ? 0 : _capture.Frames[cursor].TimeMicroseconds);
        var frameIndex = _capture.Frames.Count <= 1
            ? 0
            : (int)((long)cursor * (_capture.Frames.Count - 1) / Math.Max(1, _capture.TickCount - 1));
        var buttons = _capture.Frames.Count == 0 ? 0UL : _capture.Frames[frameIndex].Buttons;
        status = new ReplayPlaybackStatus(elapsed, _selected.TimeMicroseconds, _selected.Rank, _selected.PlayerName, buttons);
        return true;
    }

    private async Task AutoSelectAsync(string mapName)
    {
        try { await SelectAsync(mapName, 1).ConfigureAwait(false); }
        catch (Exception exception) { logger.LogError(exception, "Failed to load the WR replay for {Map}.", mapName); }
    }

    private void OnMapUnload(IOnMapUnloadEvent _) => Clear();

    private void Clear()
    {
        _mapLoadTimer?.Cancel(); _mapLoadTimer?.Dispose(); _mapLoadTimer = null;
        foreach (var sessionId in _viewers.Keys.ToArray()) StopWatching(sessionId);
        _capture = null; _selected = null; _selectedMap = null; _frame = 0;
    }

    private sealed record ReplayViewer(int PlayerId, Vector Position, QAngle Angles, Vector Velocity, bool Native);
}

public readonly record struct ReplayPlaybackStatus(
    long ElapsedMicroseconds,
    long TotalMicroseconds,
    int Rank,
    string PlayerName,
    ulong Buttons);
