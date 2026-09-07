namespace SurfTimer.Practice;

using SwiftlyS2.Shared.Natives;

public sealed record SavedLocation(
    Vector Position,
    QAngle Angles,
    Vector Velocity,
    int Checkpoint,
    string? Name = null);

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
        if (location.Name is { } name)
        {
            var existing = _locations.FindIndex(saved => string.Equals(saved.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { _locations[existing] = location; CurrentIndex = existing; return existing; }
        }
        if (CurrentIndex + 1 < _locations.Count)
            _locations.RemoveRange(CurrentIndex + 1, _locations.Count - CurrentIndex - 1);
        _locations.Add(location);
        CurrentIndex = _locations.Count - 1;
        return CurrentIndex;
    }

    public SavedLocation? Select(string nameOrNumber)
    {
        var index = Find(nameOrNumber);
        if (index < 0) return null;
        CurrentIndex = index;
        IsActive = true;
        return _locations[index];
    }

    private int Find(string nameOrNumber) => int.TryParse(nameOrNumber, out var number)
        ? number >= 1 && number <= _locations.Count ? number - 1 : -1
        : _locations.FindIndex(location => string.Equals(location.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase));

    public bool Delete(string? nameOrNumber = null)
    {
        var index = nameOrNumber is null ? CurrentIndex : Find(nameOrNumber);
        if (index < 0 || index >= _locations.Count) return false;
        _locations.RemoveAt(index);
        if (index < CurrentIndex) CurrentIndex--;
        CurrentIndex = Math.Min(CurrentIndex, _locations.Count - 1);
        return true;
    }

    // Clearing locations must not clear practice/noclip state or make a practice run competitive.
    public void ClearLocations() { _locations.Clear(); CurrentIndex = -1; }

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
