using BotControllerApi;
using SurfTimer.Replays;
using SurfTimer.Timing;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void MustReject(EncodedReplay replay, string message)
{
    try { _ = ReplayCodec.Decode(replay); }
    catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException) { return; }
    throw new InvalidOperationException(message);
}

var legacyFrames = new[]
{
    new ReplayFrame(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 8UL | 2UL),
    new ReplayFrame(15_625, 2, 3, 4, 5, 6, 7, 8, 9, 10, 512UL | 4UL)
};
var legacy = ReplayCodec.Encode(new ReplayCapture(64, legacyFrames, RecordedDurationMicroseconds: 15_625));
var legacyDecoded = ReplayCodec.Decode(legacy);
Check(legacyDecoded.Frames.Count == 2, "Legacy frame count did not round-trip.");
Check(legacyDecoded.Frames[0].Buttons == (8UL | 2UL) && legacyDecoded.Frames[1].Buttons == (512UL | 4UL),
    "Legacy buttons did not round-trip.");

var nativeTicks = new[]
{
    new ReplayTick { Pre = new MovementSnapshot { OriginX = 1, Buttons = 8UL | 1024UL } },
    new ReplayTick { Pre = new MovementSnapshot { OriginX = 2, Buttons = 512UL | 2UL } }
};
var native = ReplayCodec.Encode(new ReplayCapture(64, legacyFrames, nativeTicks, [], 15_625));
var nativeDecoded = ReplayCodec.Decode(native);
var armedTick = nativeTicks[0];
armedTick.WeaponDefIndex = 42;
var armedCapture = new ReplayCapture(64, legacyFrames, [armedTick], [], 15_625);
var weaponless = NativeReplayAdapter.WeaponlessTicks(armedCapture);
Check(weaponless[0].WeaponDefIndex == -1, "Replay retained native weapon selection.");
Check(armedCapture.NativeTicks![0].WeaponDefIndex == 42 && weaponless[0].Pre.Equals(armedTick.Pre),
    "Weapon-free playback mutated the stored capture or movement snapshot.");
Check(nativeDecoded.IsNative && nativeDecoded.Frames.Count == 2, "Native HUD frames were not reconstructed.");
Check(nativeDecoded.Frames[0].Buttons == (8UL | 1024UL) && nativeDecoded.Frames[1].Buttons == (512UL | 2UL),
    "Native buttons did not round-trip.");

MustReject(native with { DurationMicroseconds = native.DurationMicroseconds + 1 },
    "Native metadata duration mismatch was accepted.");
MustReject(legacy with { CompressedFrames = legacy.CompressedFrames[..(legacy.CompressedFrames.Length / 2)] },
    "Truncated replay payload was accepted.");
MustReject(legacy with { FrameCount = 2_000_001 }, "Oversized replay frame count was accepted.");

var cleanTelemetry = RunTelemetryAnalyzer.Analyze(new ReplayCapture(64, new[]
{
    new ReplayFrame(0, 0, 0, 0, 0, 0, 0, 100, 0, 0, 0),
    new ReplayFrame(15_625, 10, 0, 0, 0, 0, 0, 200, 0, 0, 0)
}), 3500);
Check(!cleanTelemetry.HasAnomalies && cleanTelemetry.Flags == "none", "Normal movement was flagged.");

var flaggedTelemetry = RunTelemetryAnalyzer.Analyze(new ReplayCapture(64, new[]
{
    new ReplayFrame(0, 0, 0, 0, 0, 0, 0, 4000, 0, 0, 0),
    new ReplayFrame(15_625, 1000, 0, 0, 0, 0, 0, 4000, 0, 0, 0)
}), 3500);
Check(flaggedTelemetry.OverspeedSamples == 2 && flaggedTelemetry.PositionJumpCount == 1 &&
      flaggedTelemetry.Flags.Contains("velocity_over_map_limit") && flaggedTelemetry.Flags.Contains("position_discontinuity"),
    "Anomalous movement telemetry was not flagged.");

Console.WriteLine("Replay codec and run telemetry regression checks passed.");

// Disk-queue envelope preserves the stable delivery ID and encoded native replay across restart.
var queuedRun = new SurfTimer.Storage.CompletedRun(123, "queue test", "surf_test", null, 0, 15625,
    [], [15625], "test", null, new RunTelemetry(1, 0, 0, 0, 0, "none"));
var queued = new SurfTimer.Storage.PendingRunEnvelope(queuedRun, null, legacy);
var restored = System.Text.Json.JsonSerializer.Deserialize<SurfTimer.Storage.PendingRunEnvelope>(
    System.Text.Json.JsonSerializer.Serialize(queued))!;
Check(restored.Id == queuedRun.RunId, "Pending run ID changed across disk serialization; retry would duplicate completion.");
Check(restored.Main!.StageTimes.Single() == 15625, "Queued stage time was lost.");
Check(ReplayCodec.Decode(restored.Replay!).Frames.SequenceEqual(legacyDecoded.Frames), "Queued replay did not survive restart.");
var receipt = new SurfTimer.Storage.SaveRecordResult(true, 20000, 15625, 1,
    [new SurfTimer.Storage.StageRecordResult(1, true, null, 15625, 1)]);
var restoredReceipt = System.Text.Json.JsonSerializer.Deserialize<SurfTimer.Storage.SaveRecordResult>(System.Text.Json.JsonSerializer.Serialize(receipt))!;
Check(restoredReceipt.Stages.Single() == receipt.Stages.Single() && restoredReceipt.PreviousBestMicroseconds == 20000,
    "Idempotency receipt lost original result.");
Console.WriteLine("Pending-run and receipt restart serialization checks passed.");

var viewerA = new ReplayTimeline(1_000_000);
var viewerB = new ReplayTimeline(1_000_000);
viewerA.Advance(.25);
viewerB.SetSpeed(2);
viewerB.Advance(.25);
Check(viewerA.PositionMicroseconds == 250_000 && viewerB.PositionMicroseconds == 500_000, "Viewer timelines interfered.");
viewerA.Paused = true;
viewerA.Advance(.5);
Check(viewerA.PositionMicroseconds == 250_000, "Paused replay advanced.");
viewerB.Advance(.4);
Check(Math.Abs(viewerB.PositionMicroseconds - 300_000) < 1, "Replay did not loop at configured speed.");
viewerA.Seek(999);
Check(viewerA.PositionMicroseconds == 999_999, "Seek was not clamped to replay duration.");
viewerA.Seek(-1);
Check(viewerA.PositionMicroseconds == 0, "Negative seek was not clamped.");
viewerA.Seek(.02);
Check(viewerA.FrameIndex(legacyFrames) == 1 && viewerA.FrameIndex([]) == -1, "Replay frame lookup failed.");
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, .1, 5 })
{
    try { viewerA.SetSpeed(invalid); throw new InvalidOperationException("Invalid replay speed accepted."); }
    catch (ArgumentOutOfRangeException) { }
}

var practice = new SurfTimer.Practice.PlayerPracticeState();
SurfTimer.Practice.SavedLocation Location(string? name = null) => new(new SwiftlyS2.Shared.Natives.Vector(), new SwiftlyS2.Shared.Natives.QAngle(), new SwiftlyS2.Shared.Natives.Vector(), 0, name);
practice.Save(Location("ramp"));
practice.Save(Location("landing"));
practice.Save(Location("exit"));
Check(practice.Select("RAMP")?.Name == "ramp", "Names must resolve case-insensitively.");
practice.Save(Location("landing"));
Check(practice.Locations.Count == 3 && practice.CurrentIndex == 1, "Replacing named location discarded later locations.");
Check(practice.Delete("ramp") && practice.Current()?.Name == "landing", "Deleting earlier location changed selection.");
Check(practice.Select("2")?.Name == "exit", "Numeric location selection failed.");
Check(practice.Delete() && practice.Current()?.Name == "landing", "Deleting last location left invalid selection.");
practice.SetNoclip(true);
practice.ClearLocations();
Check(practice.IsActive && practice.IsNoclip && practice.Current() is null && practice.CurrentIndex == -1, "Clearing locations reset practice protections.");
practice.Save(Location());practice.Save(Location());practice.Move(-1);practice.Save(Location());
Check(practice.Locations.Count == 2, "Unnamed save must preserve forward-history replacement semantics.");
practice.Reset();
Check(!practice.IsActive && !practice.IsNoclip && practice.Locations.Count == 0, "Practice reset retained map locations.");
Console.WriteLine("Independent replay timeline and named practice location regression checks passed.");

var heldDuration = ReplayCodec.Encode(new ReplayCapture(64, legacyFrames, RecordedDurationMicroseconds: 100_000));
Check(ReplayCodec.Decode(heldDuration).DurationMicroseconds == 100_000, "Legacy decoding discarded the finish duration.");
var backwards = ReplayCodec.Encode(new ReplayCapture(64, [legacyFrames[1], legacyFrames[0]], RecordedDurationMicroseconds: 100_000));
MustReject(backwards, "Out-of-order replay timestamps were accepted.");
Check(ReplayTimeline.NativePosition(nativeDecoded, 1) == 0 &&
      ReplayTimeline.NativePosition(nativeDecoded, 2) == 15_625 &&
      ReplayTimeline.NativePosition(nativeDecoded, -1) == 0,
    "Native next-tick cursor did not map to the displayed frame.");
var smoothFrames = new[] {
    new ReplayFrame(0, 0, 0, 0, 0, 179, 0, 100, 0, 0, 8),
    new ReplayFrame(20_000, 2, 0, 0, 0, -179, 0, 100, 0, 0, 512)
};
var smooth = new ReplayTimeline(40_000);
smooth.Seek(.01);
var midpoint = smooth.Sample(smoothFrames)!.Value;
Check(Math.Abs(midpoint.X - 1) < .001 && Math.Abs(midpoint.Yaw - 180) < .001 && midpoint.Buttons == 8,
    "Interpolation must smooth positions and take the shortest angle path without inventing buttons.");
Check(smooth.Sample([smoothFrames[0], smoothFrames[1] with { X = 1000 }])!.Value.X == 0,
    "Replay interpolation crossed a teleport discontinuity.");
Check(smooth.Sample([smoothFrames[0], smoothFrames[1] with { TimeMicroseconds = 200_000 }])!.Value.X == 0,
    "Replay interpolation crossed a recording gap.");
smooth.Seek(.03);
Check(smooth.Sample(smoothFrames) == smoothFrames[1], "Replay did not hold its last sample through the finish.");
Check(smooth.Sample([]) is null && smooth.Sample([smoothFrames[0]]) == smoothFrames[0],
    "Empty or single-frame replay sampling failed.");
Console.WriteLine("Replay duration, native cursor and interpolation regression checks passed.");
foreach (var evidence in new object[] { DBNull.Value, new byte[] { 0, 255, 11 }, ulong.MaxValue, 1234567890L,
    new DateTime(2026, 9, 7, 12, 34, 56).AddTicks(123456), 123.456d, true, "evidence" })
{
    var serialized = System.Text.Json.JsonSerializer.Serialize(SurfTimer.Storage.ArchivedValue.From(evidence));
    var roundtrip = System.Text.Json.JsonSerializer.Deserialize<SurfTimer.Storage.ArchivedValue>(serialized)!.ToValue();
    Check(evidence is byte[] bytes ? ((byte[])roundtrip).SequenceEqual(bytes) : evidence.Equals(roundtrip),
        "Archived evidence changed type or content across restore: " + evidence.GetType().Name);
}
Console.WriteLine("Record archive evidence serialization checks passed.");
