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
