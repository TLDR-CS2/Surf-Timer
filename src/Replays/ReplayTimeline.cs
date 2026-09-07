namespace SurfTimer.Replays;

/// <summary>A viewer's simulation-time cursor, independent of other viewers and tick rate.</summary>
public sealed class ReplayTimeline(long durationMicroseconds)
{
    public long DurationMicroseconds { get; } = Math.Max(1, durationMicroseconds);
    public double PositionMicroseconds { get; private set; }
    public double Speed { get; private set; } = 1;
    public bool Paused { get; set; }

    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        PositionMicroseconds = Math.Clamp(seconds * 1_000_000, 0, DurationMicroseconds - 1);
    }

    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4) throw new ArgumentOutOfRangeException(nameof(speed));
        Speed = speed;
    }

    public void Advance(double seconds)
    {
        if (Paused || !double.IsFinite(seconds) || seconds <= 0) return;
        PositionMicroseconds = (PositionMicroseconds + seconds * 1_000_000 * Speed) % DurationMicroseconds;
    }

    public int FrameIndex(IReadOnlyList<ReplayFrame> frames)
    {
        if (frames.Count == 0) return -1;
        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (frames[middle].TimeMicroseconds <= PositionMicroseconds) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    // Native cursor identifies the NEXT tick, not the last displayed snapshot.
    public static long NativePosition(ReplayCapture capture, int cursor)
    {
        if (capture.Frames.Count == 0) return 0;
        return capture.Frames[Math.Clamp(cursor, 1, capture.Frames.Count) - 1].TimeMicroseconds;
    }

    public ReplayFrame? Sample(IReadOnlyList<ReplayFrame> frames)
    {
        var index = FrameIndex(frames);
        if (index < 0) return null;
        var a = frames[index];
        if (index == frames.Count - 1 || PositionMicroseconds <= a.TimeMicroseconds) return a;
        var b = frames[index + 1];
        var interval = b.TimeMicroseconds - a.TimeMicroseconds;
        if (interval <= 0) return a;
        // Preserve teleports and gaps rather than flying through walls between samples.
        var distance = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
        var speed = Math.Max(Math.Sqrt(a.VelocityX * a.VelocityX + a.VelocityY * a.VelocityY + a.VelocityZ * a.VelocityZ),
            Math.Sqrt(b.VelocityX * b.VelocityX + b.VelocityY * b.VelocityY + b.VelocityZ * b.VelocityZ));
        if (interval > 100_000 || distance > Math.Max(64, speed * interval / 1_000_000d * 2 + 16)) return a;
        var fraction = (float)((PositionMicroseconds - a.TimeMicroseconds) / interval);
        float Lerp(float start, float end) => start + (end - start) * fraction;
        float Angle(float start, float end) => start + (float)Math.IEEERemainder(end - start, 360) * fraction;
        return a with {
            X = Lerp(a.X, b.X), Y = Lerp(a.Y, b.Y), Z = Lerp(a.Z, b.Z),
            Pitch = Angle(a.Pitch, b.Pitch), Yaw = Angle(a.Yaw, b.Yaw), Roll = Angle(a.Roll, b.Roll),
            VelocityX = Lerp(a.VelocityX, b.VelocityX), VelocityY = Lerp(a.VelocityY, b.VelocityY),
            VelocityZ = Lerp(a.VelocityZ, b.VelocityZ)
        };
    }
}
