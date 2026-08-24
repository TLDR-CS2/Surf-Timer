using Microsoft.Extensions.Logging;
using SurfTimer.Players;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using BotControllerApi;

namespace SurfTimer.Replays;

public sealed class ReplayRecorder(
    ISwiftlyCore core,
    SurfPlayerManager players,
    BotControllerBridge botController,
    ILogger<ReplayRecorder> logger)
{
    public const int SampleRateHz = 64;
    private readonly Dictionary<ulong, List<ReplayFrame>> _frames = [];
    private readonly Dictionary<ulong, int> _nativeSlots = [];
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        core.Event.OnTick += OnTick;
        logger.LogInformation("Replay recorder started at {SampleRateHz} Hz.", SampleRateHz);
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnTick -= OnTick;
        foreach (var sessionId in _frames.Keys.Concat(_nativeSlots.Keys).Distinct().ToArray()) Cancel(sessionId);
        _started = false;
    }

    public void Begin(ulong sessionId, int playerId)
    {
        Cancel(sessionId);
        _frames[sessionId] = new List<ReplayFrame>(4096);
        var api = botController.Api;
        if (api is not null && api.StartRecord(playerId))
        {
            _nativeSlots[sessionId] = playerId;
            logger.LogDebug("Native replay recording started for player {PlayerId}.", playerId);
        }
    }

    public void Cancel(ulong sessionId)
    {
        _frames.Remove(sessionId);
        if (_nativeSlots.Remove(sessionId, out var slot)) botController.Api?.StopRecord(slot);
    }

    public ReplayCapture? Complete(ulong sessionId, long durationMicroseconds)
    {
        _frames.Remove(sessionId, out var frames);
        frames ??= [];
        if (_nativeSlots.Remove(sessionId, out var slot) && botController.Api is { } api)
        {
            api.StopRecord(slot);
            var (ticks, subticks) = api.GetRecordedMotion(slot);
            if (ticks.Length > 0)
            {
                logger.LogInformation("Native replay captured for player {PlayerId}: {Ticks} ticks, {Subticks} subticks.", slot, ticks.Length, subticks.Length);
                return new ReplayCapture(SampleRateHz, frames, ticks, subticks, durationMicroseconds);
            }
            logger.LogWarning("Native replay recording returned no ticks for player {PlayerId}; using legacy frames.", slot);
        }
        if (frames.Count == 0) return null;
        return new ReplayCapture(SampleRateHz, frames);
    }

    private void OnTick()
    {
        // Lifecycle events reset/remove sessions independently of the recorder.
        // Sweep stale captures here so death, team change and disconnect can
        // never leave a native slot recording into the next run.
        foreach (var sessionId in _frames.Keys.Concat(_nativeSlots.Keys).Distinct().ToArray())
        {
            var session = players.Sessions.FirstOrDefault(candidate => candidate.SessionId == sessionId);
            if (session is null || (session.Run.State != RunState.Running && session.BonusRun.State != RunState.Running))
                Cancel(sessionId);
        }

        foreach (var session in players.Sessions)
        {
            var run = session.ActiveBonus > 0 ? session.BonusRun : session.Run;
            if (run.State != RunState.Running || !_frames.TryGetValue(session.SessionId, out var frames)) continue;
            var player = core.PlayerManager.GetPlayer(session.PlayerId);
            var pawn = player?.PlayerPawn;
            if (player is null || pawn is null || !pawn.IsValid || pawn.AbsOrigin is not { } origin) continue;
            var angles = pawn.EyeAngles;
            var velocity = pawn.AbsVelocity;
            ref var globals = ref core.Engine.GlobalVars;
            frames.Add(new ReplayFrame(
                run.ElapsedAt(new EngineTimestamp(globals.CurrentTime)),
                origin.X, origin.Y, origin.Z,
                angles.Pitch, angles.Yaw, angles.Roll,
                velocity.X, velocity.Y, velocity.Z,
                (ulong)player.PressedButtons));
        }
    }
}
