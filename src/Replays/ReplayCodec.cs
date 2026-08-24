using System.IO.Compression;
using System.Runtime.InteropServices;
using BotControllerApi;

namespace SurfTimer.Replays;

public static class ReplayCodec
{
    public const int LegacyFormatVersion = 1;
    public const int NativeFormatVersion = 2;
    private const int MaximumFrameCount = 2_000_000;
    private const int MaximumSubtickCount = 20_000_000;
    private const long MaximumDurationMicroseconds = 24L * 60 * 60 * 1_000_000;
    private const int MaximumCompressedBytes = 256 * 1024 * 1024;

    public static EncodedReplay Encode(ReplayCapture capture)
    {
        if (capture.SampleRateHz is < 1 or > 128) throw new InvalidDataException("Replay sample rate is invalid.");
        if (capture.TickCount is < 1 or > MaximumFrameCount) throw new InvalidDataException("Replay frame count is invalid.");
        if (capture.DurationMicroseconds is <= 0 or > MaximumDurationMicroseconds) throw new InvalidDataException("Replay duration is invalid.");
        return capture.IsNative ? EncodeNative(capture) : EncodeLegacy(capture);
    }

    private static EncodedReplay EncodeLegacy(ReplayCapture capture)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(brotli))
        {
            writer.Write(0x31525453); // STR1
            writer.Write(LegacyFormatVersion);
            writer.Write(capture.SampleRateHz);
            writer.Write(capture.Frames.Count);
            foreach (var f in capture.Frames)
            {
                writer.Write(f.TimeMicroseconds);
                writer.Write(f.X); writer.Write(f.Y); writer.Write(f.Z);
                writer.Write(f.Pitch); writer.Write(f.Yaw); writer.Write(f.Roll);
                writer.Write(f.VelocityX); writer.Write(f.VelocityY); writer.Write(f.VelocityZ);
                writer.Write(f.Buttons);
            }
        }
        return new EncodedReplay(LegacyFormatVersion, capture.SampleRateHz, capture.Frames.Count,
            capture.DurationMicroseconds, output.ToArray());
    }

    private static EncodedReplay EncodeNative(ReplayCapture capture)
    {
        var ticks = capture.NativeTicks!.ToArray();
        var subticks = capture.NativeSubticks?.ToArray() ?? [];
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(brotli))
        {
            writer.Write(0x32525453); // STR2
            writer.Write(NativeFormatVersion);
            writer.Write(capture.SampleRateHz);
            writer.Write(ticks.Length);
            writer.Write(subticks.Length);
            writer.Write(capture.DurationMicroseconds);
            writer.Write(MemoryMarshal.AsBytes(ticks.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(subticks.AsSpan()));
        }
        return new EncodedReplay(NativeFormatVersion, capture.SampleRateHz, ticks.Length,
            capture.DurationMicroseconds, output.ToArray());
    }

    public static ReplayCapture Decode(EncodedReplay replay)
    {
        ValidateEnvelope(replay);
        if (replay.FormatVersion == NativeFormatVersion) return DecodeNative(replay);
        if (replay.FormatVersion != LegacyFormatVersion) throw new InvalidDataException($"Unsupported replay format {replay.FormatVersion}.");
        using var input = new MemoryStream(replay.CompressedFrames, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli);
        if (reader.ReadInt32() != 0x31525453 || reader.ReadInt32() != LegacyFormatVersion)
            throw new InvalidDataException("Replay header is invalid.");
        var rate = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (rate != replay.SampleRateHz || count != replay.FrameCount) throw new InvalidDataException("Replay metadata does not match its payload.");
        var frames = new List<ReplayFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var frame = new ReplayFrame(reader.ReadInt64(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadUInt64());
            ValidateFrame(frame);
            frames.Add(frame);
        }
        if (frames[^1].TimeMicroseconds > replay.DurationMicroseconds + 1_000_000)
            throw new InvalidDataException("Replay frame timestamps exceed the stored duration.");
        return new ReplayCapture(rate, frames);
    }

    private static ReplayCapture DecodeNative(EncodedReplay replay)
    {
        using var input = new MemoryStream(replay.CompressedFrames, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli);
        if (reader.ReadInt32() != 0x32525453 || reader.ReadInt32() != NativeFormatVersion)
            throw new InvalidDataException("Native replay header is invalid.");
        var rate = reader.ReadInt32();
        var tickCount = reader.ReadInt32();
        var subtickCount = reader.ReadInt32();
        var duration = reader.ReadInt64();
        if (rate != replay.SampleRateHz || tickCount != replay.FrameCount ||
            subtickCount is < 0 or > MaximumSubtickCount || duration != replay.DurationMicroseconds ||
            duration <= 0 || duration > MaximumDurationMicroseconds)
            throw new InvalidDataException("Native replay metadata does not match its payload.");
        var tickBytes = reader.ReadBytes(checked(tickCount * Marshal.SizeOf<ReplayTick>()));
        var subtickBytes = reader.ReadBytes(checked(subtickCount * Marshal.SizeOf<SubtickMove>()));
        if (tickBytes.Length != tickCount * Marshal.SizeOf<ReplayTick>() || subtickBytes.Length != subtickCount * Marshal.SizeOf<SubtickMove>())
            throw new EndOfStreamException("Native replay payload is truncated.");
        var ticks = MemoryMarshal.Cast<byte, ReplayTick>(tickBytes).ToArray();
        var subticks = MemoryMarshal.Cast<byte, SubtickMove>(subtickBytes).ToArray();
        var frames = new ReplayFrame[ticks.Length];
        for (var index = 0; index < ticks.Length; index++)
        {
            var snapshot = ticks[index].Pre;
            var elapsed = (long)index * duration / Math.Max(1, ticks.Length - 1);
            frames[index] = new ReplayFrame(
                elapsed,
                snapshot.OriginX, snapshot.OriginY, snapshot.OriginZ,
                snapshot.Pitch, snapshot.Yaw, snapshot.Roll,
                snapshot.VelX, snapshot.VelY, snapshot.VelZ,
                snapshot.Buttons);
            ValidateFrame(frames[index]);
        }
        return new ReplayCapture(rate, frames, ticks, subticks, duration);
    }

    private static void ValidateEnvelope(EncodedReplay replay)
    {
        if (replay.FormatVersion is not LegacyFormatVersion and not NativeFormatVersion)
            throw new InvalidDataException($"Unsupported replay format {replay.FormatVersion}.");
        if (replay.SampleRateHz is < 1 or > 128) throw new InvalidDataException("Replay sample rate is invalid.");
        if (replay.FrameCount is < 1 or > MaximumFrameCount) throw new InvalidDataException("Replay frame count is invalid.");
        if (replay.DurationMicroseconds is <= 0 or > MaximumDurationMicroseconds) throw new InvalidDataException("Replay duration is invalid.");
        if (replay.CompressedFrames.Length is < 1 or > MaximumCompressedBytes)
            throw new InvalidDataException("Replay compressed payload size is invalid.");
    }

    private static void ValidateFrame(ReplayFrame frame)
    {
        if (frame.TimeMicroseconds < 0 ||
            !float.IsFinite(frame.X) || !float.IsFinite(frame.Y) || !float.IsFinite(frame.Z) ||
            !float.IsFinite(frame.Pitch) || !float.IsFinite(frame.Yaw) || !float.IsFinite(frame.Roll) ||
            !float.IsFinite(frame.VelocityX) || !float.IsFinite(frame.VelocityY) || !float.IsFinite(frame.VelocityZ))
            throw new InvalidDataException("Replay contains an invalid frame.");
    }
}
