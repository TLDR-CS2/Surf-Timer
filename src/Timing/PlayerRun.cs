namespace SurfTimer.Timing;

public enum RunState
{
    Idle,
    Armed,
    Running,
    Finished
}

public enum RunInvalidationReason
{
    None,
    Restart,
    StartZoneReentry,
    Death,
    TeamChange,
    PracticeSave,
    PracticeTeleport,
    Noclip,
    StageTeleport,
    BonusTeleport,
    ReplayPlayback,
    CheckpointOrder,
    StageOrder,
    FinishOrder,
    MapChange
}

public sealed record RunInvalidation(RunInvalidationReason Reason, string Details, DateTimeOffset OccurredAt);

public readonly record struct EngineTimestamp(double SimulationSeconds)
{
    public long MicrosecondsSince(EngineTimestamp earlier)
    {
        var elapsedSeconds = SimulationSeconds - earlier.SimulationSeconds;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return 0;
        return (long)Math.Round(elapsedSeconds * 1_000_000d);
    }
}

public sealed class PlayerRun
{
    private readonly List<long> _checkpointSplits = [];
    private readonly List<long> _stageSplits = [];

    public RunState State { get; private set; }
    public EngineTimestamp StartedAt { get; private set; }
    public long LastElapsedMicroseconds { get; private set; }
    public int LastCheckpoint { get; private set; }
    public int StartZoneTouchDepth { get; private set; }
    public int CurrentStage { get; private set; }
    public EngineTimestamp StageStartedAt { get; private set; }
    public IReadOnlyList<long> CheckpointSplits => _checkpointSplits;
    public IReadOnlyList<long> StageSplits => _stageSplits;
    public RunInvalidation? LastInvalidation { get; private set; }

    public bool EnterStartZone()
    {
        if (StartZoneTouchDepth < int.MaxValue) StartZoneTouchDepth++;
        if (StartZoneTouchDepth != 1) return false;
        State = RunState.Armed;
        LastElapsedMicroseconds = 0;
        LastCheckpoint = 0;
        CurrentStage = 1;
        _checkpointSplits.Clear();
        _stageSplits.Clear();
        return true;
    }

    public bool LeaveStartZone()
    {
        // Source 2 can emit stale/duplicate EndTouch notifications after
        // teleports and entity rebuilds. Only a real 1 -> 0 transition means
        // the player left the start zone.
        if (StartZoneTouchDepth <= 0) return false;
        StartZoneTouchDepth--;
        return StartZoneTouchDepth == 0;
    }

    public bool TryCheckpoint(int checkpoint, EngineTimestamp timestamp, out long splitMicroseconds)
    {
        splitMicroseconds = 0;
        if (State != RunState.Running || checkpoint != LastCheckpoint + 1) return false;

        splitMicroseconds = timestamp.MicrosecondsSince(StartedAt);
        LastCheckpoint = checkpoint;
        _checkpointSplits.Add(splitMicroseconds);
        return true;
    }

    public bool Start(EngineTimestamp timestamp)
    {
        if (State != RunState.Armed) return false;
        StartedAt = timestamp;
        StageStartedAt = timestamp;
        State = RunState.Running;
        return true;
    }

    public bool TryEnterStage(int stage, EngineTimestamp timestamp, out long cumulativeSplit, out long stageTime)
    {
        cumulativeSplit = 0;
        stageTime = 0;
        if (State != RunState.Running || stage != CurrentStage + 1) return false;
        cumulativeSplit = timestamp.MicrosecondsSince(StartedAt);
        stageTime = timestamp.MicrosecondsSince(StageStartedAt);
        _stageSplits.Add(cumulativeSplit);
        CurrentStage = stage;
        StageStartedAt = timestamp;
        return true;
    }

    public bool Finish(EngineTimestamp timestamp, out long elapsedMicroseconds)
    {
        elapsedMicroseconds = 0;
        if (State != RunState.Running) return false;
        elapsedMicroseconds = timestamp.MicrosecondsSince(StartedAt);
        LastElapsedMicroseconds = elapsedMicroseconds;
        State = RunState.Finished;
        return true;
    }

    public long ElapsedAt(EngineTimestamp timestamp) => State switch
    {
        RunState.Running => timestamp.MicrosecondsSince(StartedAt),
        RunState.Finished => LastElapsedMicroseconds,
        _ => 0
    };

    public void Reset()
    {
        ResetCore();
    }

    public void Invalidate(RunInvalidationReason reason, string details = "")
    {
        if (reason == RunInvalidationReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
        LastInvalidation = new RunInvalidation(reason, details, DateTimeOffset.UtcNow);
        ResetCore();
    }

    private void ResetCore()
    {
        State = RunState.Idle;
        StartedAt = default;
        LastElapsedMicroseconds = 0;
        LastCheckpoint = 0;
        StartZoneTouchDepth = 0;
        CurrentStage = 0;
        StageStartedAt = default;
        _checkpointSplits.Clear();
        _stageSplits.Clear();
    }
}
