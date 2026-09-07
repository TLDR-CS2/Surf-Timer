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
    public PlayerRun StageRun { get; } = new();
    public int ActiveStageAttempt { get; private set; }
    public int ActiveBonus { get; private set; }
    public Vector? RestartPosition { get; private set; }
    public QAngle? RestartAngles { get; private set; }
    public Dictionary<int, SavedLocation> StageLocations { get; } = [];
    public int DisplayStage { get; private set; }
    public Dictionary<int, SavedLocation> BonusLocations { get; } = [];
    public bool IsWatchingReplay { get; private set; }
    public PlayerPreferences Preferences { get; private set; } = new();
    public PlayerPracticeState Practice { get; } = new();
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;
    public string? SplitComparisonHtml { get; private set; }
    public DateTimeOffset SplitComparisonUntil { get; private set; }

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

    public void ClearMapTransforms()
    {
        RestartPosition = null;
        RestartAngles = null;
        StageLocations.Clear();
        BonusLocations.Clear();
        DisplayStage = 0;
        SplitComparisonHtml = null;
        SplitComparisonUntil = default;
    }

    public void SetRestartTransform(Vector position, QAngle angles)
    {
        RestartPosition = new Vector(position);
        RestartAngles = new QAngle(angles);
    }

    public void SetStageTransform(int stage, Vector position, QAngle angles, Vector velocity) =>
        StageLocations[stage] = new SavedLocation(new Vector(position), new QAngle(angles), new Vector(velocity), stage);

    public void SetDisplayStage(int stage) => DisplayStage = Math.Max(0, stage);

    public void SetBonusTransform(int bonus, Vector position, QAngle angles, Vector velocity) =>
        BonusLocations[bonus] = new SavedLocation(new Vector(position), new QAngle(angles), new Vector(velocity), bonus);

    public void SelectBonus(int bonus) { ClearStageAttempt(); ActiveBonus = bonus; BonusRun.Reset(); }
    public void ClearBonus() { ActiveBonus = 0; BonusRun.Reset(); ClearStageAttempt(); }
    public void SelectStageAttempt(int stage) { ClearBonus(); ActiveStageAttempt = stage; StageRun.Reset(); }
    public void ClearStageAttempt() { ActiveStageAttempt = 0; StageRun.Reset(); }

    public void SetWatchingReplay(bool watching)
    {
        IsWatchingReplay = watching;
        if (watching) Run.Invalidate(RunInvalidationReason.ReplayPlayback);
        else Run.Reset();
        ClearBonus();
    }

    public long PreferenceRevision { get; private set; }
    public bool PreferencesLoaded { get; private set; }
    public bool PreferencesLoading { get; private set; }
    public bool TryBeginPreferenceLoad()
    {
        if (PreferencesLoaded || PreferencesLoading || !IsAuthorized || SteamId == 0 || IsBot) return false;
        PreferencesLoading = true;
        return true;
    }
    public void PreferenceLoadFailed() => PreferencesLoading = false;
    public void SetPreferences(PlayerPreferences preferences)
    {
        Preferences = preferences;
        PreferenceRevision++;
    }

    public bool TryLoadPreferences(PlayerPreferences preferences, long expectedRevision)
    {
        PreferencesLoading = false;
        if (PreferenceRevision != expectedRevision) return false;
        SetPreferences(preferences);
        PreferencesLoaded = true;
        return true;
    }
    public void MarkActivity() => LastActivityAt = DateTimeOffset.UtcNow;
    public bool IsAfk(TimeSpan threshold) => DateTimeOffset.UtcNow - LastActivityAt >= threshold;
    public void ShowSplitComparison(string html, TimeSpan duration)
    {
        SplitComparisonHtml = html;
        SplitComparisonUntil = DateTimeOffset.UtcNow.Add(duration);
    }

    public void MarkSpawned() => IsAlive = true;

    public void MarkDead()
    {
        if (!IsAlive) return;
        IsAlive = false;
        Run.Invalidate(RunInvalidationReason.Death);
        ClearBonus();
        Practice.ClearNoclip();
        DisplayStage = 0;
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
        DisplayStage = 0;
        return true;
    }
}

