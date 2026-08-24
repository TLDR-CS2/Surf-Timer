using SurfTimer.Replays;

namespace SurfTimer.Timing;

public sealed record RunTelemetry(
    int ValidationVersion,
    double MaximumSpeed,
    int OverspeedSamples,
    double MaximumFrameDistance,
    int PositionJumpCount,
    string Flags)
{
    public const int CurrentVersion = 1;
    public bool HasAnomalies => OverspeedSamples > 0 || PositionJumpCount > 0;
}

public static class RunTelemetryAnalyzer
{
    public static RunTelemetry Analyze(ReplayCapture? replay, int configuredMaximumVelocity)
    {
        if (replay is null || replay.Frames.Count == 0)
            return new RunTelemetry(RunTelemetry.CurrentVersion, 0, 0, 0, 0, "no_replay_frames");

        var speedLimit = configuredMaximumVelocity * 1.05d;
        var maximumSpeed = 0d;
        var overspeed = 0;
        var maximumDistance = 0d;
        var jumps = 0;
        ReplayFrame? previous = null;
        foreach (var frame in replay.Frames)
        {
            var speed = Math.Sqrt(frame.VelocityX * frame.VelocityX + frame.VelocityY * frame.VelocityY + frame.VelocityZ * frame.VelocityZ);
            maximumSpeed = Math.Max(maximumSpeed, speed);
            if (speed > speedLimit) overspeed++;
            if (previous is { } prior)
            {
                var dx = frame.X - prior.X; var dy = frame.Y - prior.Y; var dz = frame.Z - prior.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                maximumDistance = Math.Max(maximumDistance, distance);
                var elapsedSeconds = Math.Max(1d / Math.Max(1, replay.SampleRateHz),
                    (frame.TimeMicroseconds - prior.TimeMicroseconds) / 1_000_000d);
                var plausibleDistance = Math.Max(512d, configuredMaximumVelocity * elapsedSeconds * 2.5d);
                if (distance > plausibleDistance) jumps++;
            }
            previous = frame;
        }
        var flags = string.Join(',', new[]
        {
            overspeed > 0 ? "velocity_over_map_limit" : null,
            jumps > 0 ? "position_discontinuity" : null
        }.Where(value => value is not null));
        return new RunTelemetry(RunTelemetry.CurrentVersion, maximumSpeed, overspeed, maximumDistance, jumps,
            string.IsNullOrEmpty(flags) ? "none" : flags);
    }
}
