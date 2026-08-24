namespace SurfTimer.Practice;

using SwiftlyS2.Shared.Natives;

public sealed record SavedLocation(
    Vector Position,
    QAngle Angles,
    Vector Velocity,
    int Checkpoint);

public sealed class PlayerPracticeState
{
    public const int DefaultNoclipSpeed = 1000;
    private readonly List<SavedLocation> _locations = [];

    public bool IsActive { get; private set; }
    public bool IsNoclip { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public int NoclipSpeed { get; private set; } = DefaultNoclipSpeed;
    public int CurrentStage { get; private set; }
    public IReadOnlyList<SavedLocation> Locations => _locations;

    public int Save(SavedLocation location)
    {
        IsActive = true;
        if (CurrentIndex + 1 < _locations.Count)
            _locations.RemoveRange(CurrentIndex + 1, _locations.Count - CurrentIndex - 1);
        _locations.Add(location);
        CurrentIndex = _locations.Count - 1;
        return CurrentIndex;
    }

    public SavedLocation? Current() => CurrentIndex >= 0 && CurrentIndex < _locations.Count
        ? _locations[CurrentIndex] : null;

    public SavedLocation? Move(int direction)
    {
        if (_locations.Count == 0) return null;
        CurrentIndex = Math.Clamp(CurrentIndex + direction, 0, _locations.Count - 1);
        IsActive = true;
        return _locations[CurrentIndex];
    }

    public void Activate() => IsActive = true;
    public void SetNoclip(bool enabled) { IsActive = true; IsNoclip = enabled; }
    public void ClearNoclip() => IsNoclip = false;
    public void SetNoclipSpeed(int speed) => NoclipSpeed = Math.Clamp(speed, 500, 2000);
    public void SetStage(int stage) { IsActive = true; CurrentStage = Math.Max(0, stage); }

    public void Reset()
    {
        IsActive = false;
        IsNoclip = false;
        CurrentIndex = -1;
        CurrentStage = 0;
        _locations.Clear();
    }
}
