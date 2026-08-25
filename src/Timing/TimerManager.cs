using Microsoft.Extensions.Logging;
using SurfTimer.Chat;
using SurfTimer.Players;
using SurfTimer.Maps;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;
using SurfTimer.Configuration;
using SurfTimer.Storage;
using SurfTimer.Replays;

namespace SurfTimer.Timing;

public sealed class TimerManager(
    ISwiftlyCore core,
    SurfPlayerManager players,
    MapLifecycle maps,
    RecordRepository records,
    ReplayRecorder replays,
    ReplayPlaybackManager playback,
    SurfTimerOptions options,
    ILogger<TimerManager> logger)
{
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        core.GameHooks.Entities.StartTouch.Post += OnEntityStartTouch;
        core.GameHooks.Entities.EndTouch.Post += OnEntityEndTouch;
        core.Event.OnMapUnload += OnMapUnload;
        logger.LogInformation("Timer manager started with map trigger semantics enabled.");
    }

    public void Stop()
    {
        if (!_started) return;
        core.GameHooks.Entities.StartTouch.Post -= OnEntityStartTouch;
        core.GameHooks.Entities.EndTouch.Post -= OnEntityEndTouch;
        core.Event.OnMapUnload -= OnMapUnload;
        ResetAll();
        _started = false;
    }

    private void OnEntityStartTouch(ref StartTouchEntityPostContext context)
    {
        var touch = ResolveTimerTouch(context.Params.Entity, context.Params.OtherEntity);
        if (touch is null) return;
        var (targetName, player, session) = touch.Value;

        if (maps.TryParseBonusTrigger(targetName, "start", out var bonus) && player.PlayerPawn is { } bonusPawn &&
            bonusPawn.AbsOrigin is { } bonusOrigin)
        {
            session.SetBonusTransform(bonus, bonusOrigin, bonusPawn.EyeAngles, bonusPawn.AbsVelocity);
            replays.Cancel(session.SessionId);
            session.Run.Invalidate(RunInvalidationReason.BonusTeleport, $"bonus={bonus}");
            if (session.ActiveBonus != bonus) session.SelectBonus(bonus);
            if (session.BonusRun.EnterStartZone())
                player.SendChat(ChatFormat.Success($"Bonus {bonus} ready · leave the start zone to begin."));
            return;
        }

        if (maps.TryParseBonusTrigger(targetName, "end", out bonus))
        {
            if (session.ActiveBonus == bonus && session.BonusRun.Finish(Now(), out var bonusElapsed))
            {
                player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}BONUS {bonus} FINISHED{ChatFormat.Reset} · {ChatFormat.SuccessColor}{FormatTime(bonusElapsed)}{ChatFormat.Reset}");
                var map = maps.Current;
                if (player.SteamID != 0 && map is not null)
                {
                    var replay = replays.Complete(player.SessionId, bonusElapsed);
                    var telemetry = RunTelemetryAnalyzer.Analyze(replay, map.Configuration.MaxVelocity);
                    var completed = new CompletedBonusRun(player.SteamID, player.Name, map.Name, map.WorkshopId,
                        bonus, bonusElapsed, options.ServerId, replay, telemetry);
                    _ = PersistBonusAsync(completed, player.PlayerID, player.SessionId);
                }
            }
            return;
        }

        if (maps.TryParseStageStart(targetName, out var stage) && player.PlayerPawn is { } stagePawn &&
            stagePawn.AbsOrigin is { } stageOrigin)
        {
            session.SetStageTransform(stage, stageOrigin, stagePawn.EyeAngles, stagePawn.AbsVelocity);
            if (session.Run.State == RunState.Running && stage > session.Run.CurrentStage + 1)
            {
                RejectRun(player, session, RunInvalidationReason.StageOrder,
                    $"expected={session.Run.CurrentStage + 1};actual={stage}",
                    $"stage {session.Run.CurrentStage + 1} was skipped");
                return;
            }
            if (stage > 1 && session.Run.TryEnterStage(stage, Now(), out var cumulative, out var stageTime))
                _ = SendStageCompletedAsync(player.PlayerID, player.SessionId, player.SteamID,
                    maps.Current?.Name ?? string.Empty, stage - 1, stageTime, cumulative);
        }

        if (maps.IsStartTrigger(targetName))
        {
            // Re-entering start is an explicit run cancellation (including a
            // map fail teleport). Stop native/legacy capture before re-arming.
            replays.Cancel(session.SessionId);
            session.ClearBonus();
            if (session.Run.State == RunState.Running)
                session.Run.Invalidate(RunInvalidationReason.StartZoneReentry);
            var pawn = player.PlayerPawn;
            if (pawn?.AbsOrigin is { } origin)
            {
                session.SetRestartTransform(origin, pawn.EyeAngles);
            }
            if (session.Run.EnterStartZone())
            {
                player.SendChat(ChatFormat.Success("Ready · leave the start zone to begin."));
            }
            return;
        }


        if (TryParseCheckpoint(targetName, out var checkpoint))
        {
            if (session.Run.State == RunState.Running && checkpoint > session.Run.LastCheckpoint + 1)
            {
                RejectRun(player, session, RunInvalidationReason.CheckpointOrder,
                    $"expected={session.Run.LastCheckpoint + 1};actual={checkpoint}",
                    $"checkpoint {session.Run.LastCheckpoint + 1} was skipped");
                return;
            }
            if (session.Run.TryCheckpoint(checkpoint, Now(), out var split))
            {
                _ = SendCheckpointCompletedAsync(player.PlayerID, player.SessionId, player.SteamID,
                    maps.Current?.Name ?? string.Empty, checkpoint, split);
            }
            return;
        }

        // Stage-start and other configured timing triggers must never fall
        // through into finish validation. Only the map's end trigger finishes.
        if (!maps.IsEndTrigger(targetName)) return;

        var requiredCheckpoints = GetCheckpointCount();
        if (session.Run.State == RunState.Running && session.Run.LastCheckpoint < requiredCheckpoints)
        {
            RejectRun(player, session, RunInvalidationReason.FinishOrder,
                $"checkpoint={session.Run.LastCheckpoint};required={requiredCheckpoints}",
                $"checkpoint {session.Run.LastCheckpoint + 1}/{requiredCheckpoints} was not completed");
            return;
        }
        if (maps.StageCount > 0 && session.Run.State == RunState.Running && session.Run.CurrentStage != maps.StageCount)
        {
            RejectRun(player, session, RunInvalidationReason.FinishOrder,
                $"stage={session.Run.CurrentStage};required={maps.StageCount}",
                $"stage {session.Run.CurrentStage + 1}/{maps.StageCount} was not completed");
            return;
        }

        var finishTimestamp = Now();
        var finalStageTime = finishTimestamp.MicrosecondsSince(session.Run.StageStartedAt);
        if (session.Run.Finish(finishTimestamp, out var elapsed))
        {
            if (maps.StageCount > 0)
                _ = SendStageCompletedAsync(player.PlayerID, player.SessionId, player.SteamID,
                    maps.Current?.Name ?? string.Empty, maps.StageCount, finalStageTime, elapsed);
            var formatted = FormatTime(elapsed);
            player.SendChat($"{ChatFormat.Prefix} FINISHED · {ChatFormat.SuccessColor}{formatted}{ChatFormat.Reset}");
            logger.LogInformation("Run finished: {Name} ({SteamId}) on {Map} in {ElapsedMicroseconds}us.",
                player.Name, player.SteamID,
                core.Engine.GlobalVars.MapName.ToString(), elapsed);
            var map = maps.Current;
            if (player.SteamID != 0 && map is not null)
            {
                var replay = replays.Complete(player.SessionId, elapsed);
                var telemetry = RunTelemetryAnalyzer.Analyze(replay, map.Configuration.MaxVelocity);
                if (telemetry.HasAnomalies)
                    logger.LogWarning("Run telemetry flagged {Name} ({SteamId}) on {Map}: {Flags}; maxSpeed={MaxSpeed:F1}; jumps={Jumps}.",
                        player.Name, player.SteamID, map.Name, telemetry.Flags, telemetry.MaximumSpeed, telemetry.PositionJumpCount);
                var completed = new CompletedRun(
                    player.SteamID, player.Name, map.Name, map.WorkshopId,
                    requiredCheckpoints, elapsed, session.Run.CheckpointSplits.ToArray(),
                    BuildStageTimes(session.Run, elapsed, maps.StageCount), options.ServerId,
                    replay, telemetry);
                _ = PersistRunAsync(completed, player.PlayerID, player.SessionId);
            }
        }
    }

    private async Task SendStageCompletedAsync(
        int playerId, ulong sessionId, ulong steamId, string map, int stage, long stageTime, long cumulative)
    {
        try
        {
            var pb = steamId == 0 ? null : await records.GetStagePersonalBestAsync(steamId, map, stage).ConfigureAwait(false);
            var top = await records.GetStageTopAsync(map, stage, 1).ConfigureAwait(false);
            var pbDelta = pb is null ? string.Empty : FormatSignedComparison("PB", stageTime - pb.TimeMicroseconds);
            var wrDelta = top.Count == 0 ? string.Empty : FormatSignedComparison("WR", stageTime - top[0].TimeMicroseconds);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId)
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}STAGE {stage} COMPLETE{ChatFormat.Reset} · {FormatTime(stageTime)} · Total {FormatTime(cumulative)}{pbDelta}{wrDelta}");
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load Stage {Stage} comparisons for {SteamId} on {Map}.", stage, steamId, map);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId)
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}STAGE {stage} COMPLETE{ChatFormat.Reset} · {FormatTime(stageTime)} · Total {FormatTime(cumulative)}");
            });
        }
    }

    private async Task SendCheckpointCompletedAsync(
        int playerId, ulong sessionId, ulong steamId, string map, int checkpoint, long cumulative)
    {
        try
        {
            var comparison = await records.GetMapRunComparisonAsync(steamId, map).ConfigureAwait(false);
            var pbSplit = comparison.PersonalBest?.Splits.FirstOrDefault(value => value.Checkpoint == checkpoint);
            var wrSplit = comparison.WorldRecord?.Splits.FirstOrDefault(value => value.Checkpoint == checkpoint);
            var pbDelta = pbSplit is null ? string.Empty : FormatSignedComparison("PB", cumulative - pbSplit.TimeMicroseconds);
            var wrDelta = wrSplit is null ? string.Empty : FormatSignedComparison("WR", cumulative - wrSplit.TimeMicroseconds);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId)
                    player.SendChat($"{ChatFormat.Prefix} CHECKPOINT {checkpoint}/{GetCheckpointCount()} · {FormatTime(cumulative)}{pbDelta}{wrDelta}");
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load checkpoint {Checkpoint} comparisons for {SteamId} on {Map}.", checkpoint, steamId, map);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId)
                    player.SendChat($"{ChatFormat.Prefix} CHECKPOINT {checkpoint}/{GetCheckpointCount()} · {FormatTime(cumulative)}");
            });
        }
    }

    private static string FormatSignedComparison(string label, long delta)
    {
        const char defaultColor = '\u0001';
        const char red = '\u0002';
        const char green = '\u0004';
        var color = delta <= 0 ? green : red;
        return $" | {color}[{label} {(delta <= 0 ? "-" : "+")}{FormatTime(Math.Abs(delta))}]{defaultColor}";
    }

    private static IReadOnlyList<long> BuildStageTimes(PlayerRun run, long elapsed, int stageCount)
    {
        if (stageCount <= 0 || run.StageSplits.Count != stageCount - 1) return [];
        var times = new long[stageCount];
        long previous = 0;
        for (var index = 0; index < run.StageSplits.Count; index++)
        {
            times[index] = run.StageSplits[index] - previous;
            previous = run.StageSplits[index];
        }
        times[^1] = elapsed - previous;
        return times;
    }

    private async Task PersistRunAsync(CompletedRun run, int playerId, ulong sessionId)
    {
        try
        {
            var result = await records.SaveRunAsync(run).ConfigureAwait(false);
            if (result.IsPersonalBest)
                await playback.RefreshIfSelectedAsync(run.MapName).ConfigureAwait(false);
            var pb = await records.GetPersonalBestDetailsAsync(run.SteamId, run.MapName).ConfigureAwait(false);
            var overall = await records.GetPlayerOverallRankingAsync(run.SteamId).ConfigureAwait(false);
            var mapPoints = pb is null ? null : SurfPointsPolicy.ForMainMap(maps.Current?.Configuration.Tier ?? 1, pb.Rank, pb.TotalRecords);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                if (result.IsPersonalBest)
                {
                    var improvement = result.PreviousBestMicroseconds is { } previous
                        ? $" (-{FormatTime(previous - result.BestMicroseconds)})"
                        : string.Empty;
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.SuccessColor}NEW PB · {FormatTime(result.BestMicroseconds)}{ChatFormat.Reset}{improvement} · {ChatFormat.Rank(result.Rank)}{(mapPoints?.Group is null ? string.Empty : $" · {mapPoints.Group}")}");
                }
                else
                {
                    player.SendChat($"{ChatFormat.Prefix} PB · {ChatFormat.SuccessColor}{FormatTime(result.BestMicroseconds)}{ChatFormat.Reset} · {ChatFormat.Rank(result.Rank)}{(mapPoints?.Group is null ? string.Empty : $" · {mapPoints.Group}")}");
                }
                if (mapPoints is not null)
                    player.SendChat(ChatFormat.Row("POINTS ·", $"Map {mapPoints.Points:N0} · Global {overall?.Points ?? 0:N0}"));
                foreach (var stage in result.Stages.Where(value => value.IsPersonalBest))
                {
                    var improvement = stage.PreviousBestMicroseconds is { } previous
                        ? $" (-{FormatTime(previous - stage.BestMicroseconds)})"
                        : string.Empty;
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}NEW STAGE {stage.Stage} PB{ChatFormat.Reset} · {ChatFormat.SuccessColor}{FormatTime(stage.BestMicroseconds)}{ChatFormat.Reset}{improvement} · {ChatFormat.Rank(stage.Rank)}");
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save completed run for {SteamId} on {Map}.", run.SteamId, run.MapName);
        }
    }

    private async Task PersistBonusAsync(CompletedBonusRun run, int playerId, ulong sessionId)
    {
        try
        {
            var result = await records.SaveBonusAsync(run).ConfigureAwait(false);
            var overall = await records.GetPlayerOverallRankingAsync(run.SteamId).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                var improvement = result.IsPersonalBest && result.PreviousBestMicroseconds is { } previous
                    ? $" (-{FormatTime(previous - result.BestMicroseconds)})" : string.Empty;
                player.SendChat(result.IsPersonalBest
                    ? $"[SurfTimer] New Bonus {run.Bonus} PB: {FormatTime(result.BestMicroseconds)}{improvement} — global rank #{result.Rank} — +{SurfPointsPolicy.BonusRoutePoints} points"
                    : $"[SurfTimer] Bonus {run.Bonus} PB: {FormatTime(result.BestMicroseconds)} — global rank #{result.Rank}");
                player.SendChat(ChatFormat.Row("GLOBAL POINTS ·", $"{overall?.Points ?? 0:N0}"));
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save Bonus {Bonus} run for {SteamId} on {Map}.", run.Bonus, run.SteamId, run.MapName);
        }
    }

    private void OnEntityEndTouch(ref EndTouchEntityPostContext context)
    {
        var touch = ResolveTimerTouch(context.Params.Entity, context.Params.OtherEntity);
        if (touch is null) return;

        if (maps.TryParseBonusTrigger(touch.Value.TargetName, "start", out var bonus))
        {
            if (touch.Value.Session.ActiveBonus == bonus && touch.Value.Session.BonusRun.LeaveStartZone() &&
                touch.Value.Session.BonusRun.Start(Now()))
            {
                replays.Begin(touch.Value.Player.SessionId, touch.Value.Player.PlayerID);
                touch.Value.Player.SendChat(ChatFormat.Success($"Bonus {bonus} timer started."));
            }
            return;
        }
        if (!maps.IsStartTrigger(touch.Value.TargetName)) return;

        if (touch.Value.Session.Run.LeaveStartZone() && touch.Value.Session.Run.Start(Now()))
        {
            replays.Begin(touch.Value.Player.SessionId, touch.Value.Player.PlayerID);
            touch.Value.Player.SendChat(ChatFormat.Success("Timer started."));
        }
    }

    private void OnMapUnload(IOnMapUnloadEvent _) => ResetAll();

    private void RejectRun(SwiftlyS2.Shared.Players.IPlayer player, SurfPlayerSession session,
        RunInvalidationReason reason, string details, string playerMessage)
    {
        replays.Cancel(session.SessionId);
        session.Run.Invalidate(reason, details);
        session.ClearBonus();
        player.SendChat(ChatFormat.Error($"Run rejected · {playerMessage}."));
        logger.LogWarning("Run invalidated: {Name} ({SteamId}) reason={Reason} details={Details} map={Map}.",
            player.Name, player.SteamID, reason, details, maps.Current?.Name ?? "none");
    }

    private (SwiftlyS2.Shared.Players.IPlayer Player, SurfPlayerSession Session)? ResolvePlayer(CBaseEntity entity)
    {
        var pawn = core.EntitySystem.GetEntityByIndex<CBasePlayerPawn>(entity.Index);
        if (pawn is null) return null;
        var player = core.PlayerManager.GetPlayerFromPawn(pawn);
        if (player is null) return null;
        var session = players.Get(player.PlayerID);
        return session is null || session.IsWatchingReplay || session.Practice.IsActive ? null : (player, session);
    }

    private (string TargetName, SwiftlyS2.Shared.Players.IPlayer Player, SurfPlayerSession Session)?
        ResolveTimerTouch(CBaseEntity first, CBaseEntity second)
    {
        var firstName = first.Identity?.Name;
        if (IsTimerTrigger(firstName))
        {
            var player = ResolvePlayer(second);
            if (player is not null && !player.Value.Player.IsFakeClient)
                return (firstName!, player.Value.Player, player.Value.Session);
        }

        var secondName = second.Identity?.Name;
        if (IsTimerTrigger(secondName))
        {
            var player = ResolvePlayer(first);
            if (player is not null && !player.Value.Player.IsFakeClient)
                return (secondName!, player.Value.Player, player.Value.Session);
        }

        return null;
    }

    private bool IsTimerTrigger(string? targetName) =>
        maps.Current?.Configuration.Enabled == true &&
        (maps.IsStartTrigger(targetName) || maps.IsEndTrigger(targetName) || maps.TryParseCheckpoint(targetName, out _) ||
         maps.TryParseStageStart(targetName, out _) || maps.TryParseBonusTrigger(targetName, "start", out _) ||
         maps.TryParseBonusTrigger(targetName, "end", out _));
         

    private bool TryParseCheckpoint(string? targetName, out int checkpoint) => maps.TryParseCheckpoint(targetName, out checkpoint);

    private int GetCheckpointCount()
    {
        return maps.CheckpointCount;
    }

    private EngineTimestamp Now()
    {
        ref var globals = ref core.Engine.GlobalVars;
        return new EngineTimestamp(globals.CurrentTime);
    }

    private void ResetAll()
    {
        foreach (var session in players.Sessions)
        {
            replays.Cancel(session.SessionId);
            session.Run.Invalidate(RunInvalidationReason.MapChange);
            session.ClearBonus();
            session.Practice.Reset();
            session.StageLocations.Clear();
            session.BonusLocations.Clear();
        }
    }

    public static string FormatTime(long microseconds)
    {
        var totalMilliseconds = microseconds / 1_000;
        var minutes = totalMilliseconds / 60_000;
        var seconds = totalMilliseconds / 1_000 % 60;
        var milliseconds = totalMilliseconds % 1_000;
        return $"{minutes:00}:{seconds:00}.{milliseconds:000}";
    }
}
