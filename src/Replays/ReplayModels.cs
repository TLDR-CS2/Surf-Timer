namespace SurfTimer.Replays;

using BotControllerApi;

public readonly record struct ReplayFrame(
    long TimeMicroseconds,
    float X, float Y, float Z,
    float Pitch, float Yaw, float Roll,
    float VelocityX, float VelocityY, float VelocityZ,
    ulong Buttons);

public sealed record ReplayCapture(
    int SampleRateHz,
    IReadOnlyList<ReplayFrame> Frames,
    IReadOnlyList<ReplayTick>? NativeTicks = null,
    IReadOnlyList<SubtickMove>? NativeSubticks = null,
    long? RecordedDurationMicroseconds = null)
{
    public bool IsNative => NativeTicks is { Count: > 0 };
    public int TickCount => IsNative ? NativeTicks!.Count : Frames.Count;
    public long DurationMicroseconds => RecordedDurationMicroseconds ??
        (Frames.Count == 0 ? (long)TickCount * 1_000_000 / Math.Max(1, SampleRateHz) : Frames[^1].TimeMicroseconds);
}

public sealed record EncodedReplay(
    int FormatVersion, int SampleRateHz, int FrameCount, long DurationMicroseconds, byte[] CompressedFrames);

public sealed record StoredReplay(
    int Rank, string PlayerName, long TimeMicroseconds, EncodedReplay Data);
