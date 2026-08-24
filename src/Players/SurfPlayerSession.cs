namespace SurfTimer.Players;

using SurfTimer.Timing;
using SurfTimer.Practice;
using SwiftlyS2.Shared.Natives;

public sealed class SurfPlayerSession(
    int playerId,
    ulong sessionId,
    string name,
    bool isBot)
{
    public int PlayerId { get; } = playerId;
    public ulong SessionId { get; } = sessionId;
    public string Name { get; private set; } = name;
    public bool IsBot { get; } = isBot;
    public bool IsAuthorized { get; private set; }
    public ulong SteamId { get; private set; }
    public bool IsAlive { get; private set; }
    public byte Team { get; private set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public PlayerRun Run { get; } = new();
    public PlayerRun BonusRun { get; } = new();
    public int ActiveBonus { get; private set; }
    public Vector? RestartPosition { get; private set; }
    public QAngle? RestartAngles { get; private set; }
    public Dictionary<int, SavedLocation> StageLocations { get; } = [];
    public Dictionary<int, SavedLocation> BonusLocations { get; } = [];
    public bool IsWatchingReplay { get; private set; }
    public PlayerPreferences Preferences { get; private set; } = new();
    public PlayerPracticeState Practice { get; } = new();

    public void Refresh(string name, bool isAuthorized, ulong steamId, bool isAlive)
    {
        Name = name;
        IsAuthorized = isAuthorized;
        SteamId = steamId;
        IsAlive = isAlive;
    }

    public void MarkAuthorized(ulong steamId)
    {
        IsAuthorized = true;
        SteamId = steamId;
    }

    public void SetRestartTransform(Vector position, QAngle angles)
    {
        RestartPosition = new Vector(position);
        RestartAngles = new QAngle(angles);
    }

    public void SetStageTransform(int stage, Vector position, QAngle angles, Vector velocity) =>
        StageLocations[stage] = new SavedLocation(new Vector(position), new QAngle(angles), new Vector(velocity), stage);

    public void SetBonusTransform(int bonus, Vector position, QAngle angles, Vector velocity) =>
        BonusLocations[bonus] = new SavedLocation(new Vector(position), new QAngle(angles), new Vector(velocity), bonus);

    public void SelectBonus(int bonus) { ActiveBonus = bonus; BonusRun.Reset(); }
    public void ClearBonus() { ActiveBonus = 0; BonusRun.Reset(); }

    public void SetWatchingReplay(bool watching)
    {
        IsWatchingReplay = watching;
        if (watching) Run.Invalidate(RunInvalidationReason.ReplayPlayback);
        else Run.Reset();
        ClearBonus();
    }

    public void SetPreferences(PlayerPreferences preferences) => Preferences = preferences;

    public void MarkSpawned() => IsAlive = true;

    public void MarkDead()
    {
        if (!IsAlive) return;
        IsAlive = false;
        Run.Invalidate(RunInvalidationReason.Death);
        ClearBonus();
        Practice.ClearNoclip();
    }

    public bool ChangeTeam(byte team)
    {
        if (Team == team) return false;
        Team = team;
        if (team < 2)
        {
            IsAlive = false;
        }


        Run.Invalidate(RunInvalidationReason.TeamChange, $"team={team}");
        ClearBonus();
        if (team < 2) Practice.Reset();
        return true;
    }
}
