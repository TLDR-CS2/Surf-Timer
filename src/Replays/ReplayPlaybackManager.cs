using Microsoft.Extensions.Logging;
using SurfTimer.Storage;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SurfTimer.Players;
using BotControllerApi;
using SurfTimer.Maps;

namespace SurfTimer.Replays;

public sealed class ReplayPlaybackManager(
    ISwiftlyCore core,
    RecordRepository records,
    SurfPlayerManager players,
    BotControllerBridge botController,
    MapLifecycle maps,
    ILogger<ReplayPlaybackManager> logger)
{
    private ReplayCapture? _capture;
    private StoredReplay? _selected;
    private string? _selectedMap;
    private string _route = "main";
    private int _routeIndex;
    private long _request;
    private long _viewerRequestSequence;
    private readonly Dictionary<ulong, long> _viewerRequests = [];
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
        var generation = maps.Current?.Generation;
        var request = Interlocked.Increment(ref _request);
        var stored = await records.GetReplayAsync(mapName, rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => { if (Current(mapName, generation) && request == _request) Activate(mapName, stored, decoded, "main", 0); });
        return stored;
    }

    public async Task<StoredReplay?> SelectBonusAsync(string mapName, int bonus, int rank)
    {
        var generation = maps.Current?.Generation;
        var request = Interlocked.Increment(ref _request);
        var stored = await records.GetBonusReplayAsync(mapName, bonus, rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => { if (Current(mapName, generation) && request == _request) Activate(mapName, stored, decoded, "bonus", bonus); });
        return stored;
    }

    public async Task<StoredReplay?> SelectStageAsync(string mapName, int stage, int rank)
    {
        var generation = maps.Current?.Generation;
        var request = Interlocked.Increment(ref _request);
        var stored = await records.GetStageReplayAsync(mapName,stage,rank).ConfigureAwait(false);
        if (stored is null) return null;
        var decoded = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => { if (Current(mapName, generation) && request == _request) Activate(mapName, stored, decoded, "stage", stage); });
        return stored;
    }

    public async Task RefreshIfSelectedAsync(string mapName)
    {
        var selected = _selected;
        if (selected is null || !string.Equals(_selectedMap, mapName, StringComparison.OrdinalIgnoreCase)) return;
        await (_route switch {
            "stage" => SelectStageAsync(mapName, _routeIndex, selected.Rank),
            "bonus" => SelectBonusAsync(mapName, _routeIndex, selected.Rank),
            _ => SelectAsync(mapName, selected.Rank)
        }).ConfigureAwait(false);
    }

    public void InvalidateSelection() => core.Scheduler.NextTick(() => Clear());

    private bool Current(string mapName, long? generation) => _started && generation is not null &&
        maps.Current is { } current && current.Generation == generation && current.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase);

    public async Task<StoredReplay?> WatchAsync(string mapName, string route, int index, int rank, int playerId, ulong sessionId)
    {
        var generation = maps.Current?.Generation;
        var request = Interlocked.Increment(ref _viewerRequestSequence);
        _viewerRequests[sessionId] = request;
        var stored = await (route switch {
            "stage" => records.GetStageReplayAsync(mapName, index, rank),
            "bonus" => records.GetBonusReplayAsync(mapName, index, rank),
            _ => records.GetReplayAsync(mapName, rank)
        }).ConfigureAwait(false);
        if (stored is null) return null;
        var capture = ReplayCodec.Decode(stored.Data);
        core.Scheduler.NextTick(() => {
            if (Current(mapName, generation) && _viewerRequests.GetValueOrDefault(sessionId) == request)
                BeginWatching(playerId, sessionId, stored, capture);
        });
        return stored;
    }

    public void Watch(int playerId, ulong sessionId) => core.Scheduler.NextTick(() =>
    {
        if (_capture is not null && _selected is not null) BeginWatching(playerId, sessionId, _selected, _capture);
    });

    private void BeginWatching(int playerId, ulong sessionId, StoredReplay stored, ReplayCapture capture)
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        var session = players.Get(playerId);
        var pawn = player?.PlayerPawn;
        if (player is null || player.SessionId != sessionId || session is null || !player.IsAlive || pawn is null) return;
        StopWatching(sessionId, restore: true);
        if (pawn.AbsOrigin is not { } origin) return;
        var position = new Vector(origin);
        var angles = new QAngle(pawn.EyeAngles);
        var velocity = new Vector(pawn.AbsVelocity);
        var weapons = new ReplayWeaponState(pawn);
        var native = StartNativeReplay(playerId, pawn, capture);
        weapons.Apply();
        _viewers[sessionId] = new ReplayViewer(
            playerId,
            position,
            angles,
            velocity,
            weapons,
            native,
            NativeCursor: 0,
            NativeLastProgress: core.Engine.GlobalVars.CurrentTime,
            NativeRetried: false,
            NativeHasProgress: false,
            capture, stored, new ReplayTimeline(stored.TimeMicroseconds), core.Engine.GlobalVars.CurrentTime);
        session.SetWatchingReplay(true);
        player.SendChat(native
            ? "[SurfTimer] Watching native first-person replay. Type !replay stop to exit."
            : "[SurfTimer] Native replay unavailable; using compatibility playback. Type !replay stop to exit.");
    }

    public void StopWatching(ulong sessionId, bool restore = true)
    {
        _viewerRequests.Remove(sessionId);
        if (!_viewers.Remove(sessionId, out var viewer)) return;
        viewer.Weapons.Restore();
        var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
        var session = players.Get(viewer.PlayerId);
        if (player is null || player.SessionId != sessionId) return;
        if (viewer.Native) StopNativeReplay(viewer.PlayerId);
        if (session?.SessionId == sessionId) session.SetWatchingReplay(false);
        if (restore && player is not null && player.SessionId == sessionId)
            player.Teleport(viewer.Position, viewer.Angles, viewer.Velocity);
    }

    private void Activate(string mapName, StoredReplay stored, ReplayCapture capture, string route, int routeIndex)
    {
        _selectedMap = mapName; _selected = stored; _capture = capture;
        _route = route; _routeIndex = routeIndex;
        logger.LogInformation("Selected replay rank {Rank}: {PlayerName}, {Frames} frames.", stored.Rank, stored.PlayerName, capture.Frames.Count);
    }

    private bool StartNativeReplay(int playerId, CCSPlayerPawn pawn, ReplayCapture capture)
    {
        var api = botController.Api;
        if (api is null)
        {
            logger.LogWarning("Native replay unavailable for player {PlayerId}: BotController API is missing.", playerId);
            return false;
        }
        var started = false;
        var step = "stop previous replay";
        try
        {
            api.StopReplay(playerId);
            var ticks = NativeReplayAdapter.WeaponlessTicks(capture);
            var subticks = capture.NativeSubticks?.ToArray() ?? [];
            step = "load replay";
            if (!api.LoadReplay(playerId, ticks, subticks)) return false;
            step = "register pawn";
            if (!api.SetReplayPawn(playerId, ((INativeHandle)pawn).Address)) return false;
            step = "start replay";
            if (!api.StartReplay(playerId, loop: true)) return false;
            started = true;
            logger.LogInformation("Started native replay on player {PlayerId}: {Ticks} ticks.", playerId, ticks.Length);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Native replay startup failed for player {PlayerId}.", playerId);
            return false;
        }
        finally
        {
            if (!started)
            {
                logger.LogWarning("Native replay failed at {Step} for player {PlayerId} (ABI {Abi}).", step, playerId, api.AbiVersion);
                StopNativeReplay(playerId);
            }
        }
    }

    private void StopNativeReplay(int playerId)
    {
        try { botController.Api?.StopReplay(playerId); }
        catch (Exception exception) { logger.LogError(exception, "Could not stop native replay for player {PlayerId}.", playerId); }
    }

    private int NativeCursor(int playerId)
    {
        try { return botController.Api?.ReplayCursor(playerId) ?? -1; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read native replay cursor for player {PlayerId}.", playerId);
            return -1;
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
        var now = (double)core.Engine.GlobalVars.CurrentTime;
        foreach (var (sessionId, viewer) in _viewers.ToArray())
        {
            var player = core.PlayerManager.GetPlayer(viewer.PlayerId);
            if (player is null || player.SessionId != sessionId)
            {
                StopWatching(sessionId, restore: false);
                continue;
            }
            if (viewer.Native)
            {
                viewer.Weapons.Apply();
                var cursor = NativeCursor(viewer.PlayerId);
                if (cursor >= 0 && (cursor != viewer.NativeCursor || viewer.Capture.TickCount == 1))
                {
                    _viewers[sessionId] = viewer with { NativeCursor = cursor, NativeHasProgress = true, NativeLastProgress = now, LastTime = now };
                    continue;
                }

                if (cursor >= 0 && now - viewer.NativeLastProgress < 1)
                {
                    _viewers[sessionId] = viewer with { LastTime = now };
                    continue;
                }

                // Retry only a replay that never progressed: restarting a mid-run stall would jump backwards.
                if (!viewer.NativeRetried && !viewer.NativeHasProgress && player.PlayerPawn is { } pawn &&
                    StartNativeReplay(viewer.PlayerId, pawn, viewer.Capture))
                {
                    logger.LogWarning("Retrying native replay startup for player {PlayerId} after no cursor progress.", viewer.PlayerId);
                    _viewers[sessionId] = viewer with { NativeRetried = true, NativeLastProgress = now, LastTime = now };
                    continue;
                }
                core.Engine.ExecuteCommand("bc_status");
                StopNativeReplay(viewer.PlayerId);
                viewer.Timeline.Seek(ReplayTimeline.NativePosition(viewer.Capture, viewer.NativeCursor) / 1_000_000d);
                logger.LogWarning(
                    "Native replay stopped advancing for player {PlayerId}: cursor {Cursor}, last {LastCursor}, ticks {Ticks}, retried {Retried}; switching to compatibility playback.",
                    viewer.PlayerId, cursor, viewer.NativeCursor, viewer.Capture.TickCount, viewer.NativeRetried);
                player.SendChat("[SurfTimer] Native playback is not advancing; using compatibility playback. Details were logged for the server admin.");
            }
            viewer.Timeline.Advance(now - viewer.LastTime);
            viewer.Weapons.Apply();
            if (viewer.Timeline.Sample(viewer.Capture.Frames) is not { } f) { StopWatching(sessionId); continue; }
            player.Teleport(new Vector(f.X, f.Y, f.Z), new QAngle(f.Pitch, f.Yaw, f.Roll),
                viewer.Timeline.Paused ? new Vector(0, 0, 0) : new Vector(
                    f.VelocityX * (float)viewer.Timeline.Speed, f.VelocityY * (float)viewer.Timeline.Speed, f.VelocityZ * (float)viewer.Timeline.Speed));
            _viewers[sessionId] = viewer with { Native = false, LastTime = now };
        }
    }

    private void OnMapLoad(IOnMapLoadEvent gameEvent)
    {
        Clear(restore: false);
        _mapLoadTimer = core.Scheduler.DelayBySeconds(2f, () => { _mapLoadTimer = null; _ = AutoSelectAsync(gameEvent.MapName); });
    }

    public bool TryGetViewerStatus(ulong sessionId, out ReplayPlaybackStatus status)
    {
        status = default;
        if (!_viewers.TryGetValue(sessionId, out var viewer)) return false;
        var elapsed = viewer.Native
            ? ReplayTimeline.NativePosition(viewer.Capture, viewer.NativeCursor)
            : (long)viewer.Timeline.PositionMicroseconds;
        var timeline = new ReplayTimeline(viewer.Stored.TimeMicroseconds);
        timeline.Seek(elapsed / 1_000_000d);
        var frameIndex = timeline.FrameIndex(viewer.Capture.Frames);
        var buttons = frameIndex < 0 ? 0UL : viewer.Capture.Frames[frameIndex].Buttons;
        status = new ReplayPlaybackStatus(elapsed, viewer.Stored.TimeMicroseconds, viewer.Stored.Rank, viewer.Stored.PlayerName, buttons);
        return true;
    }

    private async Task AutoSelectAsync(string mapName)
    {
        try { await SelectAsync(mapName, 1).ConfigureAwait(false); }
        catch (Exception exception) { logger.LogError(exception, "Failed to load the WR replay for {Map}.", mapName); }
    }

    public string Control(ulong sessionId, string action, double value = 0)
    {
        if (!_viewers.TryGetValue(sessionId, out var viewer)) return "You are not watching a replay.";
        if (action is not ("pause" or "resume" or "seek" or "speed")) return "Unknown replay control.";
        if ((action == "seek" && (!double.IsFinite(value) || value < 0)) ||
            (action == "speed" && (!double.IsFinite(value) || value is < 0.25 or > 4))) return "Invalid replay control value.";
        if (viewer.Native && (action == "resume" || (action == "speed" && value == 1)))
            return "Replay is already playing at 1× with native playback.";
        if (viewer.Capture.Frames.Count == 0) return "This replay has no compatibility frames for playback controls.";
        if (viewer.Native)
        {
            viewer.Timeline.Seek(ReplayTimeline.NativePosition(viewer.Capture, viewer.NativeCursor) / 1_000_000d);
            StopNativeReplay(viewer.PlayerId);
        }
        switch (action)
        {
            case "pause": viewer.Timeline.Paused = true; break;
            case "resume": viewer.Timeline.Paused = false; break;
            case "seek": viewer.Timeline.Seek(value); break;
            case "speed": viewer.Timeline.SetSpeed(value); break;
        }
        _viewers[sessionId] = viewer with { Native = false, LastTime = core.Engine.GlobalVars.CurrentTime };
        return $"Replay {(viewer.Timeline.Paused ? "paused" : "playing")} · {viewer.Timeline.Speed:0.##}× · {viewer.Timeline.PositionMicroseconds / 1_000_000:0.00}s · compatibility controls";
    }

    private void OnMapUnload(IOnMapUnloadEvent _)
    {
        foreach (var sessionId in _viewers.Keys.ToArray()) StopWatching(sessionId, restore: false);
        Clear();
    }

    private void Clear(bool restore = true)
    {
        Interlocked.Increment(ref _request);
        _viewerRequests.Clear();
        _mapLoadTimer?.Cancel(); _mapLoadTimer?.Dispose(); _mapLoadTimer = null;
        foreach (var sessionId in _viewers.Keys.ToArray()) StopWatching(sessionId, restore);
        _capture = null; _selected = null; _selectedMap = null;
    }

    private sealed record ReplayViewer(
        int PlayerId,
        Vector Position,
        QAngle Angles,
        Vector Velocity,
        ReplayWeaponState Weapons,
        bool Native,
        int NativeCursor,
        double NativeLastProgress,
        bool NativeRetried,
        bool NativeHasProgress,
        ReplayCapture Capture,
        StoredReplay Stored,
        ReplayTimeline Timeline,
        double LastTime);
}

public readonly record struct ReplayPlaybackStatus(
    long ElapsedMicroseconds,
    long TotalMicroseconds,
    int Rank,
    string PlayerName,
    ulong Buttons);
