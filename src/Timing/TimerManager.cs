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
using SurfTimer.Titles;

namespace SurfTimer.Timing;

public sealed class TimerManager(
    ISwiftlyCore core,
    SurfPlayerManager players,
    MapLifecycle maps,
    RecordRepository records,
    ReplayRecorder replays,
    ReplayPlaybackManager playback,
    TitleManager titles,
    SurfTimerOptions options,
    ILogger<TimerManager> logger)
{
    private bool _started;
    private readonly Dictionary<ulong,long> _splitRequests = [];
    private long _nextSplitRequest;
    private string? _configurationFingerprint;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _configurationFingerprint=RulesetFingerprint.Capture(core,maps);
        core.GameHooks.Entities.StartTouch.Post += OnEntityStartTouch;
        core.GameHooks.Entities.EndTouch.Post += OnEntityEndTouch;
        core.Event.OnMapUnload += OnMapUnload;
        core.Event.OnMapLoad += OnMapLoad;
        records.PendingRunRecovered += OnPendingRunRecovered;
        core.Event.OnConVarValueChanged += OnMovementChanged;
        maps.ConfigurationChanged += OnConfigurationChanged;
        logger.LogInformation("Timer manager started with map trigger semantics enabled.");
    }

    public void Stop()
    {
        if (!_started) return;
        core.GameHooks.Entities.StartTouch.Post -= OnEntityStartTouch;
        core.GameHooks.Entities.EndTouch.Post -= OnEntityEndTouch;
        core.Event.OnMapUnload -= OnMapUnload;
        core.Event.OnMapLoad -= OnMapLoad;
        records.PendingRunRecovered -= OnPendingRunRecovered;
        core.Event.OnConVarValueChanged -= OnMovementChanged;
        maps.ConfigurationChanged -= OnConfigurationChanged;
        ResetAll();
        _started = false;
    }

    private void OnEntityStartTouch(ref StartTouchEntityPostContext context)
    {
        var touch = ResolveTimerTouch(context.Params.Entity, context.Params.OtherEntity);
        if (touch is null) return;
        var (targetName, player, session) = touch.Value;
        var activeRun = session.ActiveStageAttempt > 0 ? session.StageRun : session.ActiveBonus > 0 ? session.BonusRun : session.Run;
        if (activeRun.State == RunState.Running && activeRun.RulesetFingerprint != RulesetFingerprint.Capture(core, maps))
        {
            RejectRun(player, session, RunInvalidationReason.RulesetChanged, "ruleset fingerprint changed", "competitive settings changed");
            return;
        }

        if (session.ActiveStageAttempt > 0 && !maps.IsCancelTrigger(targetName) && HandleStageAttempt(targetName, player, session)) return;

        if (maps.IsCancelTrigger(targetName))
        {
            replays.Cancel(session.SessionId);
            session.Run.Invalidate(RunInvalidationReason.CancelZone, $"trigger={targetName}");
            session.ClearBonus();
            player.SendChat(ChatFormat.Warning("Timer cancelled · route transition zone."));
            return;
        }

        if (maps.TryParseBonusTrigger(targetName, "start", out var bonus) && player.PlayerPawn is { } bonusPawn &&
            bonusPawn.AbsOrigin is { } bonusOrigin)
        {
            session.SetBonusTransform(bonus, bonusOrigin, bonusPawn.EyeAngles, bonusPawn.AbsVelocity);
            if (session.ActiveBonus != bonus) session.SelectBonus(bonus);
            if (!session.BonusRun.EnterStartZone()) return;
            replays.Cancel(session.SessionId);
            session.Run.Invalidate(RunInvalidationReason.BonusTeleport, $"bonus={bonus}");
            if (session.BonusRun.State == RunState.Armed)
                player.SendChat(ChatFormat.Success($"Bonus {bonus} ready · leave the start zone to begin."));
            return;
        }

        if (maps.TryParseBonusTrigger(targetName, "end", out bonus))
        {
            if (session.ActiveBonus == bonus && session.BonusRun.Finish(Now(), out var bonusElapsed))
            {
                if (bonusElapsed <= 0) { RejectRun(player,session,RunInvalidationReason.FinishOrder,"nonpositive bonus time","finish time must be positive"); return; }
                var replay = replays.Complete(player.SessionId, bonusElapsed);
                player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}BONUS {bonus} FINISHED{ChatFormat.Reset} · {ChatFormat.SuccessColor}{FormatTime(bonusElapsed)}{ChatFormat.Reset}");
                var map = maps.Current;
                if (player.SteamID != 0 && map is not null)
                {
                    var telemetry = RunTelemetryAnalyzer.Analyze(replay, map.Configuration.MaxVelocity);
                    var completed = new CompletedBonusRun(player.SteamID, player.Name, map.Name, map.WorkshopId,
                        bonus, bonusElapsed, options.ServerId, replay, telemetry) { RulesetFingerprint = session.BonusRun.RulesetFingerprint };
                    _ = PersistBonusAsync(completed, player.PlayerID, player.SessionId);
                }
            }
            return;
        }

        if (maps.TryParseStageStart(targetName, out var stage) && player.PlayerPawn is { } stagePawn &&
            stagePawn.AbsOrigin is { } stageOrigin)
        {
            session.SetStageTransform(stage, stageOrigin, stagePawn.EyeAngles, stagePawn.AbsVelocity);
            // HUD location is independent from whether this boundary can be
            // accepted into a valid, ordered timed run.
            session.SetDisplayStage(stage);
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
            if (maps.StageCount > 0) session.SetDisplayStage(1);
            if (!session.Run.EnterStartZone()) return;
            // Re-entering start is an explicit run cancellation (including a
            // map fail teleport). Stop native/legacy capture before re-arming.
            replays.Cancel(session.SessionId);
            session.ClearBonus();
            var pawn = player.PlayerPawn;
            if (pawn?.AbsOrigin is { } origin)
            {
                session.SetRestartTransform(origin, pawn.EyeAngles);
            }
            if (session.Run.State == RunState.Armed)
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
            if (elapsed <= 0 || BuildStageTimes(session.Run,elapsed,maps.StageCount).Any(time=>time<=0))
            { RejectRun(player,session,RunInvalidationReason.FinishOrder,"nonpositive run/stage time","finish time must be positive"); return; }
            var replay = replays.Complete(player.SessionId, elapsed);
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
                var telemetry = RunTelemetryAnalyzer.Analyze(replay, map.Configuration.MaxVelocity);
                if (telemetry.HasAnomalies)
                    logger.LogWarning("Run telemetry flagged {Name} ({SteamId}) on {Map}: {Flags}; maxSpeed={MaxSpeed:F1}; jumps={Jumps}.",
                        player.Name, player.SteamID, map.Name, telemetry.Flags, telemetry.MaximumSpeed, telemetry.PositionJumpCount);
                var completed = new CompletedRun(
                    player.SteamID, player.Name, map.Name, map.WorkshopId,
                    requiredCheckpoints, elapsed, session.Run.CheckpointSplits.ToArray(),
                    BuildStageTimes(session.Run, elapsed, maps.StageCount), options.ServerId,
                    replay, telemetry) { RulesetFingerprint = session.Run.RulesetFingerprint };
                    _ = PersistRunAsync(completed, player.PlayerID, player.SessionId, map.Configuration.Tier);
            }
        }
    }

    private bool HandleStageAttempt(string trigger, SwiftlyS2.Shared.Players.IPlayer player, SurfPlayerSession session)
    {
        var stage = session.ActiveStageAttempt;
        var map = maps.Current;
        if (map is null) return true;
        var ownStart = stage == 1 ? maps.IsStartTrigger(trigger) : maps.TryParseStageStart(trigger, out var found) && found == stage;
        if (ownStart)
        {
            if (!session.StageRun.EnterStartZone()) return true;
            replays.Cancel(session.SessionId);
            if (stage > 1 && session.StageRun.Start(Now(), RulesetFingerprint.Capture(core, maps)))
                replays.Begin(session.SessionId, player.PlayerID);
            return true;
        }
        var finish = stage == maps.StageCount ? maps.IsEndTrigger(trigger)
            : maps.TryParseStageStart(trigger, out var next) && next == stage + 1;
        if (finish)
        {
            if (session.StageRun.Finish(Now(), out var elapsed))
            {
                if (elapsed <= 0) { RejectRun(player,session,RunInvalidationReason.FinishOrder,"nonpositive stage time","finish time must be positive"); return true; }
                var capture = replays.Complete(session.SessionId, elapsed);
                if (!map.Configuration.IndependentStageRecordsCertified || maps.CheckpointCount > 0)
                {
                    player.SendChat(ChatFormat.Success($"Stage {stage} finished · {FormatTime(elapsed)} · unranked practice attempt."));
                    return true;
                }
                if (player.SteamID == 0) return true;
                var run = new CompletedStageRun(player.SteamID, player.Name, map.Name, map.WorkshopId,
                    stage, elapsed, options.ServerId, capture, RunTelemetryAnalyzer.Analyze(capture, map.Configuration.MaxVelocity))
                    { RulesetFingerprint = session.StageRun.RulesetFingerprint, CompetitiveStartCertified = true };
                player.SendChat(ChatFormat.Success($"Stage {stage} finished · {FormatTime(elapsed)} · saving…"));
                _ = PersistStageAsync(run, player.PlayerID, player.SessionId);
            }
            return true;
        }
        if (maps.IsStartTrigger(trigger) || maps.TryParseStageStart(trigger, out _) ||
            maps.TryParseBonusTrigger(trigger, "start", out _) || maps.IsEndTrigger(trigger))
        {
            replays.Cancel(session.SessionId);
            session.ClearStageAttempt();
            player.SendChat(ChatFormat.Warning("Stage attempt cancelled · unexpected route boundary."));
            return false;
        }
        return true;
    }

    private async Task PersistStageAsync(CompletedStageRun run, int playerId, ulong sessionId)
    {
        try
        {
            var result = await records.SaveStageAsync(run).ConfigureAwait(false);
            PlaySavedSound(playerId, sessionId, result.IsPersonalBest);
            ReplySaveStatus(playerId, sessionId, $"Stage {run.Stage} saved · {(result.IsPersonalBest ? "NEW PB" : "PB")} {FormatTime(result.BestMicroseconds)} · #{result.Rank}");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist stage attempt for {SteamId} on {Map}.", run.SteamId, run.MapName);
            ReplySaveStatus(playerId, sessionId, SaveFailureMessage(exception, false));
        }
    }

    private void OnPendingRunRecovered(ulong steamId, string map) => core.Scheduler.NextTick(() =>
    {
        if (!_started) return;
        foreach (var player in core.PlayerManager.GetAllValidPlayers())
            if (!player.IsFakeClient && player.SteamID == steamId)
                player.SendChat(ChatFormat.Success($"Pending run on {map} has now been saved."));
    });

    private void ReplySaveStatus(int playerId, ulong sessionId, string message) => core.Scheduler.NextTick(() =>
    {
        if (!_started) return;
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player?.SessionId == sessionId) player.SendChat(ChatFormat.Message(message));
    });

    private static string SaveFailureMessage(Exception exception, bool saved) => saved
        ? "Your run was saved, but updated standings could not be loaded."
        : exception is RunQuarantinedException
            ? "Run retained for administrator review; its ruleset is not approved for this leaderboard."
        : exception is PendingRunSaveException { InnerException: RulesetAwaitingApprovalException }
            ? "Run safely queued while this map awaits ruleset approval. Saving will retry automatically."
        : exception is PendingRunSaveException
            ? "Your run is safely queued locally. Database saving will retry automatically."
            : "Run could not be saved or queued. Please report this to an administrator.";

    private void PlaySavedSound(int playerId, ulong sessionId, bool personalBest) => core.Scheduler.NextTick(() =>
    {
        if (!_started) return;
        var player = core.PlayerManager.GetPlayer(playerId);
        var session = players.Get(playerId);
        if (player?.SessionId == sessionId && session is not null)
            SurfTimer.Sounds.FinishSounds.Play(player, session, personalBest);
    });

    private async Task SendStageCompletedAsync(
        int playerId, ulong sessionId, ulong steamId, string map, int stage, long stageTime, long cumulative)
    {
        var generation=maps.Current?.Generation;
        var revision=players.Get(playerId)?.Run.Revision;
        var request=++_nextSplitRequest;
        _splitRequests[sessionId]=request;
        try
        {
            var pb = steamId == 0 ? null : await records.GetStagePersonalBestAsync(steamId, map, stage).ConfigureAwait(false);
            var top = await records.GetStageTopAsync(map, stage, 1).ConfigureAwait(false);
            var pbDelta = pb is null ? string.Empty : FormatSignedComparison("PB", stageTime - pb.TimeMicroseconds);
            var wrDelta = top.Count == 0 ? string.Empty : FormatSignedComparison("WR", stageTime - top[0].TimeMicroseconds);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId && IsCurrentSplit(playerId,sessionId,generation,revision,request))
                {
                    ShowSplitComparison(players.Get(playerId), pbDelta, wrDelta);
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}STAGE {stage} COMPLETE{ChatFormat.Reset} · {FormatTime(stageTime)} · Total {FormatTime(cumulative)}{pbDelta}{wrDelta}");
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load Stage {Stage} comparisons for {SteamId} on {Map}.", stage, steamId, map);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId && IsCurrentSplit(playerId,sessionId,generation,revision,request))
                    player.SendChat($"{ChatFormat.Prefix} {ChatFormat.RouteColor}STAGE {stage} COMPLETE{ChatFormat.Reset} · {FormatTime(stageTime)} · Total {FormatTime(cumulative)}");
            });
        }
    }

    private async Task SendCheckpointCompletedAsync(
        int playerId, ulong sessionId, ulong steamId, string map, int checkpoint, long cumulative)
    {
        var generation=maps.Current?.Generation;
        var revision=players.Get(playerId)?.Run.Revision;
        var request=++_nextSplitRequest;
        _splitRequests[sessionId]=request;
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
                if (player is not null && player.SessionId == sessionId && IsCurrentSplit(playerId,sessionId,generation,revision,request))
                {
                    ShowSplitComparison(players.Get(playerId), pbDelta, wrDelta);
                    player.SendChat($"{ChatFormat.Prefix} CHECKPOINT {checkpoint}/{GetCheckpointCount()} · {FormatTime(cumulative)}{pbDelta}{wrDelta}");
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load checkpoint {Checkpoint} comparisons for {SteamId} on {Map}.", checkpoint, steamId, map);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId && IsCurrentSplit(playerId,sessionId,generation,revision,request))
                    player.SendChat($"{ChatFormat.Prefix} CHECKPOINT {checkpoint}/{GetCheckpointCount()} · {FormatTime(cumulative)}");
            });
        }
    }

    private bool IsCurrentSplit(int playerId,ulong sessionId,long? generation,long? revision,long request) =>
        _started && maps.Current?.Generation == generation && _splitRequests.GetValueOrDefault(sessionId)==request &&
        players.Get(playerId) is { } session && session.SessionId==sessionId && session.Run.Revision==revision &&
        session.Run.State is RunState.Running or RunState.Finished;

    private static string FormatSignedComparison(string label, long delta)
    {
        const char defaultColor = '\u0001';
        const char red = '\u0002';
        const char green = '\u0004';
        var color = delta <= 0 ? green : red;
        return $" | {color}[{label} {(delta <= 0 ? "-" : "+")}{FormatTime(Math.Abs(delta))}]{defaultColor}";
    }

    private void ShowSplitComparison(SurfPlayerSession? session, string pb, string wr)
    {
        if (session is null || !options.SplitComparisons.Enabled || options.SplitComparisons.Reference == "Off") return;
        var selected = options.SplitComparisons.Reference switch
        {
            "PB" => ComparisonHtml(pb),
            "WR" => ComparisonHtml(wr),
            _ => JoinComparisonLines(ComparisonHtml(pb), ComparisonHtml(wr))
        };
        if (string.IsNullOrEmpty(selected)) return;
        session.ShowSplitComparison(selected,
            TimeSpan.FromSeconds(options.SplitComparisons.HudDurationSeconds));
    }

    private static string ComparisonHtml(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var plain = value.Replace(" | ", "").Replace("\u0001", "").Replace("\u0002", "").Replace("\u0004", "").Trim();
        var color = plain.Contains('+') ? "#ff6b6b" : "#69db7c";
        return $"<font color='{color}'><b>{System.Net.WebUtility.HtmlEncode(plain)}</b></font>";
    }

    private static string JoinComparisonLines(string pb, string wr) =>
        string.IsNullOrEmpty(pb) ? wr : string.IsNullOrEmpty(wr) ? pb : pb + "<br>" + wr;

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

    private async Task PersistRunAsync(CompletedRun run, int playerId, ulong sessionId, int tier)
    {
        var saved = false;
        try
        {
            var result = await records.SaveRunAsync(run).ConfigureAwait(false);
            saved = true;
            PlaySavedSound(playerId, sessionId, result.IsPersonalBest);
            ReplySaveStatus(playerId, sessionId, "Run saved.");
            if (result.IsPersonalBest)
                await playback.RefreshIfSelectedAsync(run.MapName).ConfigureAwait(false);
            var pb = await records.GetPersonalBestDetailsAsync(run.SteamId, run.MapName).ConfigureAwait(false);
            var overall = await records.GetPlayerOverallRankingAsync(run.SteamId).ConfigureAwait(false);
            if (result.IsPersonalBest)
                await titles.ApplyAsync(playerId, sessionId, run.SteamId).ConfigureAwait(false);
            var mapPoints = pb is null ? null : SurfPointsPolicy.ForMainMap(tier, pb.Rank, pb.TotalRecords);
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
                    AnnounceResult(run.PlayerName, run.MapName, "STAGE", stage.Stage, true,
                        stage.PreviousBestMicroseconds, stage.Rank, stage.BestMicroseconds);
                }
                AnnounceResult(run.PlayerName, run.MapName, "MAP", 0, result.IsPersonalBest,
                    result.PreviousBestMicroseconds, result.Rank, result.BestMicroseconds);
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save completed run for {SteamId} on {Map}.", run.SteamId, run.MapName);
            ReplySaveStatus(playerId, sessionId, SaveFailureMessage(exception, saved));
        }
    }

    private async Task PersistBonusAsync(CompletedBonusRun run, int playerId, ulong sessionId)
    {
        var saved = false;
        try
        {
            var result = await records.SaveBonusAsync(run).ConfigureAwait(false);
            saved = true;
            PlaySavedSound(playerId, sessionId, result.IsPersonalBest);
            ReplySaveStatus(playerId, sessionId, "Bonus run saved.");
            var overall = await records.GetPlayerOverallRankingAsync(run.SteamId).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                var improvement = result.IsPersonalBest && result.PreviousBestMicroseconds is { } previous
                    ? $" (-{FormatTime(previous - result.BestMicroseconds)})" : string.Empty;
                player.SendChat(result.IsPersonalBest
                    ? $"[SurfTimer] New Bonus {run.Bonus} PB: {FormatTime(result.BestMicroseconds)}{improvement} — global rank #{result.Rank}{(result.PreviousBestMicroseconds is null ? $" — +{SurfPointsPolicy.BonusRoutePoints} points" : "") }"
                    : $"[SurfTimer] Bonus {run.Bonus} PB: {FormatTime(result.BestMicroseconds)} — global rank #{result.Rank}");
                player.SendChat(ChatFormat.Row("GLOBAL POINTS ·", $"{overall?.Points ?? 0:N0}"));
                AnnounceResult(run.PlayerName, run.MapName, "BONUS", run.Bonus, result.IsPersonalBest,
                    result.PreviousBestMicroseconds, result.Rank, result.BestMicroseconds);
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save Bonus {Bonus} run for {SteamId} on {Map}.", run.Bonus, run.SteamId, run.MapName);
            ReplySaveStatus(playerId, sessionId, SaveFailureMessage(exception, saved));
        }
    }

    private void OnEntityEndTouch(ref EndTouchEntityPostContext context)
    {
        var touch = ResolveTimerTouch(context.Params.Entity, context.Params.OtherEntity);
        if (touch is null) return;

        var stageSession = touch.Value.Session;
        if (stageSession.ActiveStageAttempt > 0)
        {
            var ownStart = stageSession.ActiveStageAttempt == 1 ? maps.IsStartTrigger(touch.Value.TargetName)
                : maps.TryParseStageStart(touch.Value.TargetName, out var stage) && stage == stageSession.ActiveStageAttempt;
            if (ownStart && stageSession.StageRun.LeaveStartZone() && stageSession.ActiveStageAttempt == 1 &&
                stageSession.StageRun.Start(Now(), RulesetFingerprint.Capture(core, maps))) replays.Begin(stageSession.SessionId, stageSession.PlayerId);
            return;
        }

        if (maps.TryParseBonusTrigger(touch.Value.TargetName, "start", out var bonus))
        {
            if (touch.Value.Session.ActiveBonus == bonus && touch.Value.Session.BonusRun.LeaveStartZone() &&
                touch.Value.Session.BonusRun.Start(Now(), RulesetFingerprint.Capture(core, maps)))
            {
                replays.Begin(touch.Value.Player.SessionId, touch.Value.Player.PlayerID);
                touch.Value.Player.SendChat(ChatFormat.Success($"Bonus {bonus} timer started."));
            }
            return;
        }
        if (!maps.IsStartTrigger(touch.Value.TargetName)) return;

        if (touch.Value.Session.Run.LeaveStartZone() && touch.Value.Session.Run.Start(Now(), RulesetFingerprint.Capture(core, maps)))
        {
            replays.Begin(touch.Value.Player.SessionId, touch.Value.Player.PlayerID);
            touch.Value.Player.SendChat(ChatFormat.Success("Timer started."));
        }
    }

    private void OnMapLoad(IOnMapLoadEvent _) => _configurationFingerprint=RulesetFingerprint.Capture(core,maps);
    private void OnMapUnload(IOnMapUnloadEvent _) => ResetAll();

    private void OnConfigurationChanged()
    {
        var fingerprint=RulesetFingerprint.Capture(core,maps);
        var changed=fingerprint!=_configurationFingerprint;
        _configurationFingerprint=fingerprint;
        foreach(var session in players.Sessions)
        {
            var run=session.ActiveStageAttempt>0?session.StageRun:session.ActiveBonus>0?session.BonusRun:session.Run;
            if(run.State is not (RunState.Running or RunState.Armed))continue;
            if(maps.Current?.Configuration.Enabled==true && (run.State==RunState.Armed ? !changed : run.RulesetFingerprint==fingerprint))continue;
            var player=core.PlayerManager.GetPlayer(session.PlayerId);
            if(player is not null)RejectRun(player,session,RunInvalidationReason.RulesetChanged,"map configuration changed","competitive map settings changed");
        }
    }

    private void OnMovementChanged(IOnConVarValueChanged gameEvent)
    {
        if (!RulesetFingerprint.MovementVariables.Contains(gameEvent.ConVarName) || gameEvent.OldValue == gameEvent.NewValue) return;
        foreach (var session in players.Sessions)
        {
            if (session.Run.State != RunState.Running && session.BonusRun.State != RunState.Running && session.StageRun.State != RunState.Running) continue;
            var player = core.PlayerManager.GetPlayer(session.PlayerId);
            if (player is not null) RejectRun(player, session, RunInvalidationReason.RulesetChanged, gameEvent.ConVarName, "competitive settings changed");
        }
    }

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
        return session is null || !player.IsAlive || !session.IsAlive || player.SessionId != session.SessionId || session.IsWatchingReplay || session.Practice.IsActive ? null : (player, session);
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
        (maps.IsStartTrigger(targetName) || maps.IsEndTrigger(targetName) || maps.IsCancelTrigger(targetName) || maps.TryParseCheckpoint(targetName, out _) ||
         maps.TryParseStageStart(targetName, out _) || maps.TryParseBonusTrigger(targetName, "start", out _) ||
         maps.TryParseBonusTrigger(targetName, "end", out _));

    private void AnnounceResult(string playerName, string mapName, string route, int routeIndex,
        bool isPb, long? previous, int rank, long time)
    {
        if (!options.Announcements.Enabled || !isPb) return;
        string? milestone = null;
        if (rank == 1 && options.Announcements.WorldRecords) milestone = "NEW WORLD RECORD";
        else if (rank <= 10 && options.Announcements.TopTen) milestone = $"NEW TOP {rank}";
        else if (previous is null && options.Announcements.FirstCompletions) milestone = "FIRST COMPLETION";
        if (milestone is null) return;
        var routeName = routeIndex > 0 ? $"{route} {routeIndex}" : route;
        Broadcast($"{ChatFormat.Prefix} {ChatFormat.HighlightColor}{milestone}{ChatFormat.Reset} · {playerName} · {mapName} {routeName} · {FormatTime(time)}");
    }

    private void Broadcast(string message)
    {
        foreach (var player in core.PlayerManager.GetAllValidPlayers())
            if (!player.IsFakeClient) player.SendChat(message);
    }
         

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
        _splitRequests.Clear();
        foreach (var session in players.Sessions)
        {
            replays.Cancel(session.SessionId);
            session.Run.Invalidate(RunInvalidationReason.MapChange);
            session.ClearBonus();
            session.Practice.Reset();
            session.ClearMapTransforms();
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
