using SurfTimer.Maps;
using SurfTimer.Players;
using SurfTimer.Replays;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Natives;

namespace SurfTimer.Commands;

public sealed class StageAttemptCommands(ISwiftlyCore core, MapLifecycle maps, SurfPlayerManager players,
    ReplayRecorder replays, ReplayPlaybackManager playback, PracticeCommands practice)
{
    private Guid? _registration;
    public void Register() => _registration ??= core.Command.RegisterCommand("stageattempt", OnAttempt,
        registerRaw: false, helpText: "Starts an independent stage attempt: !stageattempt <stage>.");
    public void Unregister()
    {
        if (_registration is { } registration) core.Command.UnregisterCommand(registration);
        _registration = null;
    }
    private void OnAttempt(ICommandContext context)
    {
        var player = context.Sender;
        var map = maps.Current;
        if (player is null || map is null || !map.Configuration.Enabled || !player.IsAlive)
        { context.Reply("[SurfTimer] Join an enabled map and spawn first."); return; }
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out var stage) || stage < 1 || stage > maps.StageCount)
        { context.Reply($"[SurfTimer] Usage: !stageattempt <1-{maps.StageCount}>"); return; }
        var session = players.Get(player.PlayerID);
        if (session is null || !maps.TryGetStageStartTransform(stage, out var position, out var angles))
        { context.Reply("[SurfTimer] Stage start trigger is unavailable."); return; }
        replays.Cancel(session.SessionId);
        playback.StopWatching(session.SessionId, restore: false);
        practice.Exit(session, player.PlayerPawn);
        session.Run.Invalidate(RunInvalidationReason.StageTeleport);
        session.SelectStageAttempt(stage);
        session.SetDisplayStage(stage);
        player.Teleport(position, angles, new Vector(0, 0, 0));
        if (!map.Configuration.IndependentStageRecordsCertified || maps.CheckpointCount > 0)
            context.Reply("[SurfTimer] Practice timing only · this map's independent stage starts are not certified for records.");
        context.Reply(stage == 1
            ? "[SurfTimer] Stage 1 attempt · leave the main start to begin."
            : $"[SurfTimer] Stage {stage} attempt · timing starts on entry and ends at the next stage boundary.");
    }
}
