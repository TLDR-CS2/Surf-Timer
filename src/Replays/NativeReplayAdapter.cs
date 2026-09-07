using BotControllerApi;

namespace SurfTimer.Replays;

internal static class NativeReplayAdapter
{
    public static ReplayTick[] WeaponlessTicks(ReplayCapture capture)
    {
        // Work on a copy: stored captures and concurrent viewers retain their original data.
        var ticks = capture.IsNative ? capture.NativeTicks!.ToArray() : Convert(capture);
        for (var index = 0; index < ticks.Length; index++)
        {
            ticks[index].WeaponDefIndex = -1;
        }
        return ticks;
    }

    // Source 2's MOVETYPE_WALK. Surf acceleration and gravity are applied by the
    // native movement hook between the pre/post snapshots.
    private const byte MoveTypeWalk = 2;

    public static ReplayTick[] Convert(ReplayCapture capture)
    {
        var ticks = new ReplayTick[capture.Frames.Count];
        for (var index = 0; index < capture.Frames.Count; index++)
        {
            var frame = capture.Frames[index];
            var snapshot = new MovementSnapshot
            {
                OriginX = frame.X,
                OriginY = frame.Y,
                OriginZ = frame.Z,
                VelX = frame.VelocityX,
                VelY = frame.VelocityY,
                VelZ = frame.VelocityZ,
                Pitch = frame.Pitch,
                Yaw = frame.Yaw,
                Roll = frame.Roll,
                MoveType = MoveTypeWalk,
                ActualMoveType = MoveTypeWalk,
                Buttons = frame.Buttons
            };
            ticks[index] = new ReplayTick { Pre = snapshot, Post = snapshot };
        }
        return ticks;
    }
}
