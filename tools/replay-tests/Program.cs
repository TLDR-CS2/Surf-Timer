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
