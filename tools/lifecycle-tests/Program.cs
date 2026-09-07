using SurfTimer.Timing;
using SurfTimer.Storage;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var run = new PlayerRun();

// Duplicate/stale EndTouch must never start an idle run.
Check(!run.LeaveStartZone(), "An unmatched EndTouch was treated as leaving start.");
Check(!run.Start(new EngineTimestamp(1)), "An idle run started without being armed.");

// Overlapping mapper start triggers arm once and start only after all are left.
Check(run.EnterStartZone(), "First start touch did not arm the run.");
Check(!run.EnterStartZone(), "Nested start touch armed the run twice.");
Check(run.State == RunState.Armed && run.StartZoneTouchDepth == 2, "Nested start depth is incorrect.");
Check(!run.LeaveStartZone(), "Leaving one overlapping trigger started too early.");
Check(run.LeaveStartZone(), "Final start trigger exit was not detected.");
Check(run.Start(new EngineTimestamp(10)), "Armed run did not start.");

// Returning after a fail cancels the old run and resets all progress.
Check(run.TryCheckpoint(1, new EngineTimestamp(15), out var split) && split == 5_000_000,
    "Checkpoint split was not recorded.");
Check(run.EnterStartZone(), "Fail return did not re-arm the run.");
Check(run.State == RunState.Armed && run.LastCheckpoint == 0 && run.CheckpointSplits.Count == 0,
    "Fail return retained prior run progress.");

// Reset/restart is deterministic and ignores stale exit events.
run.Reset();
Check(run.State == RunState.Idle && run.StartZoneTouchDepth == 0, "Reset did not clear lifecycle state.");
Check(!run.LeaveStartZone(), "Post-reset stale EndTouch was accepted.");
Check(run.EnterStartZone() && run.LeaveStartZone() && run.Start(new EngineTimestamp(20)),
    "Run could not start after reset.");
Check(run.Finish(new EngineTimestamp(22.5), out var elapsed) && elapsed == 2_500_000,
    "Finished run duration is incorrect.");
Check(!run.Finish(new EngineTimestamp(23), out _), "Run finished twice.");

// Explicit invalidation records a reason and prevents the old run finishing.
run.Reset();
Check(run.EnterStartZone() && run.LeaveStartZone() && run.Start(new EngineTimestamp(24)),
    "Validation test run did not start.");
run.Invalidate(RunInvalidationReason.Noclip, "test");
Check(run.State == RunState.Idle && run.LastInvalidation?.Reason == RunInvalidationReason.Noclip,
    "Invalidation reason was not retained.");
Check(!run.Finish(new EngineTimestamp(25), out _), "Invalidated run was allowed to finish.");

run.EnterStartZone(); run.LeaveStartZone(); run.Start(new EngineTimestamp(26));
run.Invalidate(RunInvalidationReason.CancelZone, "trigger=map_cancel");
Check(run.State == RunState.Idle && run.LastInvalidation?.Reason == RunInvalidationReason.CancelZone,
    "Cancel-zone invalidation was not retained.");

// Stages advance strictly in order and retain cumulative and segment timing.
run.Reset();
Check(run.EnterStartZone() && run.LeaveStartZone() && run.Start(new EngineTimestamp(30)),
    "Staged run did not start.");
Check(!run.TryEnterStage(3, new EngineTimestamp(31), out _, out _),
    "A skipped stage was accepted.");
Check(run.TryEnterStage(2, new EngineTimestamp(32), out var stageCumulative, out var stageTime) &&
      stageCumulative == 2_000_000 && stageTime == 2_000_000,
    "Stage 1 timing was incorrect.");
Check(run.TryEnterStage(3, new EngineTimestamp(35.5), out stageCumulative, out stageTime) &&
      stageCumulative == 5_500_000 && stageTime == 3_500_000,
    "Stage 2 timing was incorrect.");
Check(run.CurrentStage == 3 && run.StageSplits.SequenceEqual(new long[] { 2_000_000, 5_500_000 }),
    "Stage progression was not retained.");

// Points must remain deterministic at every documented placement boundary.
Check(SurfPointsPolicy.ForMainMap(1, 1, 100).Points == 275, "Tier 1 WR Points changed unexpectedly.");
Check(SurfPointsPolicy.ForMainMap(7, 1, 100).Points == 17_600, "Tier 7 WR Points changed unexpectedly.");
Check(SurfPointsPolicy.ForMainMap(1, 2, 100).Points == 225, "Rank 2 placement decay is incorrect.");
Check(SurfPointsPolicy.ForMainMap(1, 10, 100).Points == 125, "Rank 10 placement decay is incorrect.");
Check(SurfPointsPolicy.ForMainMap(1, 100, 100).Points == 35, "Rank 100 placement decay is incorrect.");
Check(SurfPointsPolicy.ForMainMap(1, 101, 101).Points == 25, "Outside-Top-100 completion Points are incorrect.");
Check(SurfPointsPolicy.ForMainMap(1, 1, 100).Group == "Group 1", "WR group classification is incorrect.");
Check(SurfPointsPolicy.ForMainMap(1, 51, 100).Group is null, "Bottom-half time incorrectly received a group.");

Console.WriteLine("Lifecycle regression checks passed.");

// Map cleanup must remove every teleport destination and HUD comparison from the old map.
var session = new SurfTimer.Players.SurfPlayerSession(1, 42, "test", false);
var position = new SwiftlyS2.Shared.Natives.Vector(10, 20, 30);
var angles = new SwiftlyS2.Shared.Natives.QAngle(0, 90, 0);
session.SetRestartTransform(position, angles);
session.SetStageTransform(2, position, angles, position);
session.SetBonusTransform(1, position, angles, position);
session.SetDisplayStage(2);
session.ShowSplitComparison("old map split", TimeSpan.FromSeconds(10));
session.ClearMapTransforms();
Check(session.RestartPosition is null && session.RestartAngles is null, "Map cleanup retained the old restart destination.");
Check(session.StageLocations.Count == 0 && session.BonusLocations.Count == 0, "Map cleanup retained route destinations.");
Check(session.DisplayStage == 0 && session.SplitComparisonHtml is null && session.SplitComparisonUntil == default,
    "Map cleanup retained stale route HUD information.");
Console.WriteLine("Map session cleanup checks passed.");

// Independent attempts retain the same boundaries as full-map stage splits.
var fullStageRun = new PlayerRun();
fullStageRun.EnterStartZone();fullStageRun.LeaveStartZone();fullStageRun.Start(new EngineTimestamp(10));
Check(fullStageRun.TryEnterStage(2,new EngineTimestamp(12),out _,out var fullStageOne), "Full-map stage 1 boundary failed.");
Check(fullStageRun.TryEnterStage(3,new EngineTimestamp(15),out _,out var fullStageTwo), "Full-map stage 2 boundary failed.");
var independent = new PlayerRun();
independent.EnterStartZone();independent.EnterStartZone();
Check(!independent.LeaveStartZone(), "Stage 1 overlapping exit started too soon.");
Check(independent.LeaveStartZone() && independent.Start(new EngineTimestamp(10)), "Stage 1 final exit failed.");
Check(independent.Finish(new EngineTimestamp(12),out var independentOne) && independentOne==fullStageOne, "Independent stage 1 differs from full-map timing.");
independent.Reset();
Check(independent.EnterStartZone() && independent.Start(new EngineTimestamp(12)), "Stage 2 entry did not start.");
Check(!independent.EnterStartZone() && independent.State==RunState.Running, "Overlapping stage 2 trigger rearmed a running attempt.");
Check(!independent.LeaveStartZone() && independent.LeaveStartZone(), "Stage 2 touch depth did not drain.");
Check(independent.Finish(new EngineTimestamp(15),out var independentTwo) && independentTwo==fullStageTwo, "Independent stage 2 differs from entry-to-entry split.");
Check(!independent.Finish(new EngineTimestamp(16),out _), "Duplicate finish produced a second stage result.");

var stageSession = new SurfTimer.Players.SurfPlayerSession(2,43,"stage test",false);
stageSession.SelectBonus(1);stageSession.SelectStageAttempt(2);
Check(stageSession.ActiveBonus==0 && stageSession.ActiveStageAttempt==2, "Selecting a stage retained bonus route.");
stageSession.StageRun.EnterStartZone();stageSession.StageRun.Start(new EngineTimestamp(1));
stageSession.SelectBonus(1);
Check(stageSession.ActiveStageAttempt==0 && stageSession.StageRun.State==RunState.Idle, "Bonus selection retained stage attempt.");
foreach(var terminate in new Action<SurfTimer.Players.SurfPlayerSession>[] {
    s=>s.ClearBonus(), s=>s.SetWatchingReplay(true), s=>s.ChangeTeam(1), s=>s.MarkDead()
})
{
    stageSession.MarkSpawned();stageSession.SelectStageAttempt(2);
    stageSession.StageRun.EnterStartZone();stageSession.StageRun.Start(new EngineTimestamp(1));
    terminate(stageSession);
    Check(stageSession.ActiveStageAttempt==0 && stageSession.StageRun.State==RunState.Idle, "Lifecycle transition retained a competitive stage attempt.");
}
Console.WriteLine("Independent stage timing policy and lifecycle checks passed.");


var revisionRun = new PlayerRun();
revisionRun.EnterStartZone();revisionRun.LeaveStartZone();revisionRun.Start(new EngineTimestamp(1));
var oldRevision=revisionRun.Revision;
revisionRun.TryCheckpoint(1,new EngineTimestamp(2),out _);
Check(revisionRun.Revision==oldRevision,"A valid split changed the run identity.");
revisionRun.Reset();revisionRun.EnterStartZone();revisionRun.LeaveStartZone();revisionRun.Start(new EngineTimestamp(1));
Check(revisionRun.Revision!=oldRevision,"Same-tick restart reused the async split identity.");
oldRevision=revisionRun.Revision;
revisionRun.Invalidate(RunInvalidationReason.RulesetChanged);
Check(revisionRun.Revision!=oldRevision,"Invalidation retained stale async split identity.");
Console.WriteLine("Async run revision checks passed.");

// Empty PBs, failures and stale async callbacks must not create a query per HUD tick.
var cache = new SurfTimer.Hud.RefreshCache<string, string>();
var cacheNow = DateTimeOffset.UtcNow;
Check(cache.TryBegin("unranked", cacheNow, out var cacheGeneration), "Initial PB query suppressed.");
cache.Complete("unranked", cacheGeneration, null, cacheNow.AddSeconds(30));
for (var tick = 0; tick < 1920; tick++)
    Check(!cache.TryBegin("unranked", cacheNow.AddSeconds(tick / 64.0), out _), "Empty PB caused repeated HUD lookup.");
Check(cache.TryBegin("unranked", cacheNow.AddSeconds(30), out cacheGeneration), "Empty PB never expired.");
cache.Complete("unranked", cacheGeneration, null, cacheNow.AddSeconds(45));
Check(!cache.TryBegin("unranked", cacheNow.AddSeconds(31), out _), "Failure backoff ignored.");
cache.Invalidate(_ => true);
Check(cache.TryBegin("unranked", cacheNow, out var freshGeneration), "Save did not invalidate cache.");
cache.Complete("unranked", cacheGeneration, "stale", cacheNow.AddMinutes(1));
Check(cache.Get("unranked") is null, "Old lookup overwrote post-save cache.");
cache.Complete("unranked", freshGeneration, "new PB", cacheNow.AddSeconds(30));
Check(cache.Get("unranked") == "new PB", "Fresh lookup was discarded.");
var preferencesSession = new SurfTimer.Players.SurfPlayerSession(1, 10, "preferences", false);
var preferenceRevision = preferencesSession.PreferenceRevision;
preferencesSession.SetPreferences(preferencesSession.Preferences with { HudEnabled = false });
Check(!preferencesSession.TryLoadPreferences(new(), preferenceRevision), "Delayed initial load overwrote user's toggle.");
Check(!preferencesSession.Preferences.HudEnabled, "User preference was lost.");
Console.WriteLine("HUD negative cache/backoff/invalidation and preference revision checks passed.");
