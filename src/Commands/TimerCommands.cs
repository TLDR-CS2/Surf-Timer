using SurfTimer.Players;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Natives;
using SurfTimer.Replays;
using SurfTimer.Timing;
using SurfTimer.Maps;

namespace SurfTimer.Commands;

public sealed class TimerCommands(
    ISwiftlyCore core,
    SurfPlayerManager players,
    ReplayRecorder replays,
    ReplayPlaybackManager playback,
    PracticeCommands practice,
    MapLifecycle maps)
{
    private Guid? _restartRegistration;
    private Guid? _stageRegistration;
    private Guid? _restartStageRegistration;
    private Guid? _bonusRegistration;
    private Guid? _restartBonusRegistration;

    public void Register()
    {
        if (_restartRegistration is not null) return;

        _restartRegistration = core.Command.RegisterCommand(
            "restart",
            OnRestart,
            registerRaw: false,
            helpText: "Restarts the current Surf run.");
        core.Command.RegisterCommandAlias("sw_restart", "r", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_restart", "css_r", registerRaw: true);
        core.Command.RegisterCommandAlias("sw_restart", "css_restart", registerRaw: true);
        _stageRegistration = core.Command.RegisterCommand("stage", OnStage, registerRaw: false,
            helpText: "Teleports to a discovered stage: !stage <number>.");
        core.Command.RegisterCommandAlias("sw_stage", "s", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_stage", "css_stage", registerRaw: true);
        core.Command.RegisterCommandAlias("sw_stage", "css_s", registerRaw: true);
        _restartStageRegistration = core.Command.RegisterCommand("restartstage", OnRestartStage, registerRaw: false,
            helpText: "Restarts the current stage in practice mode.");
        core.Command.RegisterCommandAlias("sw_restartstage", "rs", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_restartstage", "css_restartstage", registerRaw: true);
        core.Command.RegisterCommandAlias("sw_restartstage", "css_rs", registerRaw: true);
        _bonusRegistration = core.Command.RegisterCommand("bonus", OnBonus, registerRaw: false,
            helpText: "Teleports to a discovered bonus: !b [number].");
        core.Command.RegisterCommandAlias("sw_bonus", "b", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_bonus", "css_bonus", registerRaw: true);
        core.Command.RegisterCommandAlias("sw_bonus", "css_b", registerRaw: true);
        _restartBonusRegistration = core.Command.RegisterCommand("restartbonus", OnRestartBonus, registerRaw: false,
            helpText: "Restarts the current bonus: !rb.");
        core.Command.RegisterCommandAlias("sw_restartbonus", "rb", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_restartbonus", "css_restartbonus", registerRaw: true);
        core.Command.RegisterCommandAlias("sw_restartbonus", "css_rb", registerRaw: true);
    }

    public void Unregister()
    {
        if (_restartRegistration is not { } registration) return;
        core.Command.UnregisterCommand("sw_r");
        core.Command.UnregisterCommand("css_r");
        core.Command.UnregisterCommand("css_restart");
        foreach (var name in new[] { "sw_s", "css_stage", "css_s", "sw_rs", "css_restartstage", "css_rs",
                     "sw_b", "css_bonus", "css_b", "sw_rb", "css_restartbonus", "css_rb" })
            core.Command.UnregisterCommand(name);
        core.Command.UnregisterCommand(registration);
        if (_stageRegistration is { } stage) core.Command.UnregisterCommand(stage);
        if (_restartStageRegistration is { } restartStage) core.Command.UnregisterCommand(restartStage);
        if (_bonusRegistration is { } bonus) core.Command.UnregisterCommand(bonus);
        if (_restartBonusRegistration is { } restartBonus) core.Command.UnregisterCommand(restartBonus);
        _restartRegistration = null;
        _stageRegistration = null;
        _restartStageRegistration = null;
        _bonusRegistration = null;
        _restartBonusRegistration = null;
    }

    private void OnBonus(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer] This command requires a player caller."); return; }
        var bonus = 1;
        if (context.Args.Length > 1 || (context.Args.Length == 1 && !int.TryParse(context.Args[0], out bonus)))
        { context.Reply("[SurfTimer] Usage: !b [number]"); return; }
        if (bonus < 1 || bonus > maps.BonusCount)
        { context.Reply($"[SurfTimer] Bonus {bonus} is not configured on this map."); return; }
        var session = players.Get(context.Sender.PlayerID);
        if (session is null) { context.Reply("[SurfTimer] Player session is not ready."); return; }
        if (!TryGetBonusLocation(session, bonus, out var location))
        { context.Reply($"[SurfTimer] Bonus {bonus} start trigger is unavailable."); return; }
        TeleportToBonus(context, session, bonus, location);
    }

    private void OnRestartBonus(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer] This command requires a player caller."); return; }
        var session = players.Get(context.Sender.PlayerID);
        if (session is null) { context.Reply("[SurfTimer] Player session is not ready."); return; }
        var bonus = session.ActiveBonus > 0 ? session.ActiveBonus : 1;
        if (!TryGetBonusLocation(session, bonus, out var location))
        { context.Reply($"[SurfTimer] Bonus {bonus} start position is not known yet."); return; }
        TeleportToBonus(context, session, bonus, location);
    }

    private bool TryGetBonusLocation(SurfPlayerSession session, int bonus, out Practice.SavedLocation location)
    {
        if (session.BonusLocations.TryGetValue(bonus, out location!)) return true;
        if (!maps.TryGetBonusStartTransform(bonus, out var position, out var angles))
        {
            location = null!;
            return false;
        }
        location = new Practice.SavedLocation(position, angles, new Vector(0f, 0f, 0f), bonus);
        session.BonusLocations[bonus] = location;
        return true;
    }

    private void TeleportToBonus(ICommandContext context, SurfPlayerSession session, int bonus, Practice.SavedLocation location)
    {
        replays.Cancel(session.SessionId);
        playback.StopWatching(session.SessionId, restore: false);
        practice.Exit(session, context.Sender!.PlayerPawn);
        session.Run.Invalidate(RunInvalidationReason.BonusTeleport, $"bonus={bonus}");
        session.SelectBonus(bonus);
        var playerId = context.Sender.PlayerID;
        var sessionId = context.Sender.SessionId;
        if (!context.Sender.IsAlive)
        {
            context.Sender.Respawn();
            core.Scheduler.NextTick(() => FinishBonusTeleport(playerId, sessionId, bonus, location));
        }
        else FinishBonusTeleport(playerId, sessionId, bonus, location);
        context.Reply($"[SurfTimer] Bonus {bonus} restarted.");
    }

    private void FinishBonusTeleport(int playerId, ulong sessionId, int bonus, Practice.SavedLocation location)
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is null || player.SessionId != sessionId) return;
        player.Teleport(location.Position, location.Angles, new Vector(0f, 0f, 0f));
        core.Scheduler.DelayBySeconds(0.1f, () =>
        {
            var current = core.PlayerManager.GetPlayer(playerId);
            var session = players.Get(playerId);
            if (current is null || current.SessionId != sessionId || session?.ActiveBonus != bonus) return;
            if (session.BonusRun.State == RunState.Idle && session.BonusRun.EnterStartZone())
                current.SendChat($"[SurfTimer] Bonus {bonus} ready — leave the start zone to begin.");
        });
    }

    private void OnStage(ICommandContext context)
    {
        if (context.Sender is null || context.Args.Length != 1 || !int.TryParse(context.Args[0], out var stage))
        { context.Reply("[SurfTimer] Usage: !stage <number>"); return; }
        var session = players.Get(context.Sender.PlayerID);
        if (session is null) { context.Reply("[SurfTimer] Player session is not ready."); return; }
        if (!session.StageLocations.TryGetValue(stage, out var location))
        { context.Reply($"[SurfTimer] Stage {stage} has not been discovered yet. Reach it once first."); return; }
        TeleportToStage(context, session, stage, location);
    }

    private void OnRestartStage(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer] This command requires a player caller."); return; }
        var session = players.Get(context.Sender.PlayerID);
        if (session is null) { context.Reply("[SurfTimer] Player session is not ready."); return; }
        var stage = session.Practice.IsActive && session.Practice.CurrentStage > 0
            ? session.Practice.CurrentStage
            : Math.Max(1, session.Run.CurrentStage);
        if (!session.StageLocations.TryGetValue(stage, out var location))
        { context.Reply("[SurfTimer] Current stage position is not known yet."); return; }
        TeleportToStage(context, session, stage, location);
    }

    private void TeleportToStage(ICommandContext context, SurfPlayerSession session, int stage, Practice.SavedLocation location)
    {
        replays.Cancel(session.SessionId);
        playback.StopWatching(session.SessionId, restore: false);
        session.Run.Invalidate(RunInvalidationReason.StageTeleport, $"stage={stage}");
        session.Practice.Activate();
        session.Practice.SetStage(stage);
        var playerId = context.Sender!.PlayerID;
        var sessionId = context.Sender.SessionId;
        if (!context.Sender.IsAlive)
        {
            context.Sender.Respawn();
            core.Scheduler.NextTick(() => FinishStageTeleport(playerId, sessionId, location));
        }
        else FinishStageTeleport(playerId, sessionId, location);
        context.Reply($"[SurfTimer] Practice — stage {stage}.");
    }

    private void FinishStageTeleport(int playerId, ulong sessionId, Practice.SavedLocation location)
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is not null && player.SessionId == sessionId)
            player.Teleport(location.Position, location.Angles, new Vector(0f, 0f, 0f));
    }

    private void OnRestart(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null)
        {
            context.Reply("This command requires a player caller.");
            return;
        }

        var session = players.Get(context.Sender.PlayerID);
        if (session is null)
        {
            context.Reply("SurfTimer has not created your player session yet.");
            return;
        }

        replays.Cancel(session.SessionId);
        playback.StopWatching(session.SessionId, restore: false);
        practice.Exit(session, context.Sender.PlayerPawn);
        session.Run.Invalidate(RunInvalidationReason.Restart);
        session.ClearBonus();
        var position = session.RestartPosition;
        var angles = session.RestartAngles;
        if ((position is null || angles is null) && maps.TryGetMainStartTransform(out var mapPosition, out var mapAngles))
        {
            session.SetRestartTransform(mapPosition, mapAngles);
            position = mapPosition;
            angles = mapAngles;
        }
        if (position is null || angles is null)
        {
            context.Reply("[SurfTimer] Main start trigger is unavailable.");
            return;
        }

        var playerId = context.Sender.PlayerID;
        var sessionId = context.Sender.SessionId;
        if (!context.Sender.IsAlive)
        {
            context.Sender.Respawn();
            core.Scheduler.NextTick(() => FinishRestart(playerId, sessionId, position.Value, angles.Value));
        }
        else
        {
            FinishRestart(playerId, sessionId, position.Value, angles.Value);
        }
        context.Reply("[SurfTimer] Run restarted.");
    }

    private void FinishRestart(int playerId, ulong sessionId, Vector position, QAngle angles)
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is null || player.SessionId != sessionId) return;
        player.Teleport(position, angles, new Vector(0f, 0f, 0f));
        core.Scheduler.DelayBySeconds(0.1f, () =>
        {
            var current = core.PlayerManager.GetPlayer(playerId);
            var currentSession = players.Get(playerId);
            if (current is null || current.SessionId != sessionId || currentSession is null) return;
            // Teleport normally emits StartTouch. This is the fallback for maps
            // or engine states that do not emit it when the destination is
            // already inside the trigger volume.
            if (currentSession.Run.State == RunState.Idle && currentSession.Run.EnterStartZone())
                current.SendChat("[SurfTimer] Ready — leave the start zone to begin.");
        });
    }
}
