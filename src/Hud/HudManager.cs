using System.Text;
using Microsoft.Extensions.Logging;
using SurfTimer.Maps;
using SurfTimer.Players;
using SurfTimer.Timing;
using SurfTimer.Configuration;
using SurfTimer.Replays;
using SurfTimer.Storage;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace SurfTimer.Hud;

public sealed class HudManager(
    ISwiftlyCore core,
    SurfPlayerManager players,
    MapLifecycle maps,
    SurfTimerOptions options,
    ReplayPlaybackManager playback,
    RecordRepository records,
    ILogger<HudManager> logger)
{
    private const int MessageDurationMilliseconds = 40;
    private CancellationTokenSource? _timer;
    private readonly Dictionary<(ulong SteamId,string Map), CachedPb> _pbCache=[];
    private readonly HashSet<(ulong SteamId,string Map)> _pbRequests=[];
    private readonly Dictionary<string,CachedStandings> _standingsCache=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _standingsRequests=new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        if (_timer is not null) return;
        var refreshRate = Math.Clamp(options.HudRefreshRateHz, 1, 128);
        _timer = core.Scheduler.RepeatBySeconds(1f / refreshRate, Update);
        logger.LogInformation("HUD manager started at {RefreshRateHz} Hz.", refreshRate);
    }

    public void Stop()
    {
        _timer?.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    private void Update()
    {
        ref var globals = ref core.Engine.GlobalVars;
        var now = new EngineTimestamp(globals.CurrentTime);
        var checkpointCount = GetCheckpointCount();
        var stageCount = maps.StageCount;
        var validPlayers = core.PlayerManager.GetAllValidPlayers().Where(p=>!p.IsFakeClient&&p.IsValid).ToArray();
        var observed = new Dictionary<int,(IPlayer Player,SurfPlayerSession Session)>();
        var spectatorCounts = new Dictionary<int,int>();
        foreach(var viewer in validPlayers)
        {
            if(!TryGetObserved(viewer,out var target) || target.IsFakeClient) continue;
            var targetSession=players.Get(target.PlayerID); if(targetSession is null) continue;
            observed[viewer.PlayerID]=(target,targetSession);
            spectatorCounts[target.PlayerID]=spectatorCounts.GetValueOrDefault(target.PlayerID)+1;
        }

        foreach (var player in validPlayers)
        {
            if (player.IsFakeClient || !player.IsValid) continue;
            var session = players.Get(player.PlayerID);
            if (session is null) continue;

            var elapsed = session.ActiveBonus > 0
                ? session.BonusRun.ElapsedAt(now)
                : session.Run.ElapsedAt(now);
            var speed = GetHorizontalSpeed(player.PlayerPawn);
            var maxVelocity = maps.Current?.Configuration.MaxVelocity ?? 3500;
            if (playback.TryGetViewerStatus(session.SessionId, out var replay))
            {
                if (!session.Preferences.ReplayHudEnabled) continue;
                player.SendCenterHTML(
                    BuildReplayHtml(replay, speed, maxVelocity, session.Preferences.KeysEnabled),
                    MessageDurationMilliseconds);
                continue;
            }
            if (!session.Preferences.HudEnabled) continue;
            if (observed.TryGetValue(player.PlayerID,out var watched))
            {
                var target=watched.Player; var targetSession=watched.Session;
                var targetElapsed=targetSession.ActiveBonus>0?targetSession.BonusRun.ElapsedAt(now):targetSession.Run.ElapsedAt(now);
                var targetSpeed=GetHorizontalSpeed(target.PlayerPawn);
                player.SendCenterHTML(BuildSpectatorHtml(targetSession,session.Preferences,target.Name,targetElapsed,targetSpeed,maxVelocity,
                    checkpointCount,stageCount,(ulong)target.PressedButtons,spectatorCounts.GetValueOrDefault(target.PlayerID)),MessageDurationMilliseconds);
                continue;
            }
            player.SendCenterHTML(
                BuildHtml(session, session.Preferences, elapsed, speed, maxVelocity, checkpointCount, stageCount, (ulong)player.PressedButtons),
                MessageDurationMilliseconds);
        }
    }

    private static string BuildReplayHtml(
        ReplayPlaybackStatus replay,
        int horizontalSpeed,
        int maxVelocity,
        bool keysEnabled) =>
        $"<font class='fontSize-l horizontal-center' color='{GetSpeedColor(horizontalSpeed, maxVelocity)}'><b>{horizontalSpeed:0000}</b></font><br>" +
        $"<font color='#ffd166'><b>{TimerManager.FormatTime(replay.ElapsedMicroseconds)}</b></font><br>" +
        $"<font color='#8f9aa3'>REPLAY</font> <font color='#d8dde1'>#{replay.Rank} · {System.Net.WebUtility.HtmlEncode(replay.PlayerName)} · {TimerManager.FormatTime(replay.TotalMicroseconds)}</font>" +
        (keysEnabled ? $"<br>{BuildKeysHtml(replay.Buttons)}" : string.Empty) +
        "<br>";

    private string BuildHtml(
        SurfPlayerSession session,
        PlayerPreferences preferences,
        long elapsedMicroseconds,
        int horizontalSpeed,
        int maxVelocity,
        int checkpointCount,
        int stageCount,
        ulong pressedButtons)
    {
        var comparison = GetHudComparison(session, elapsedMicroseconds);
        var html = new StringBuilder(320);

        if (preferences.SpeedEnabled)
        {
            html.Append("<font class='fontSize-l horizontal-center' color='")
                .Append(GetSpeedColor(horizontalSpeed, maxVelocity))
                .Append("'><b>")
                .Append(horizontalSpeed.ToString("0000"))
                .Append("</b></font><br>");
        }

        html.Append("<font color='#58d6ff'><b>")
            .Append(TimerManager.FormatTime(elapsedMicroseconds))
            .Append("</b></font>");
        if (session.Run.State == RunState.Running && session.ActiveBonus == 0)
            html.Append(" <font color='").Append(GetRankColor(comparison.ProjectedRank)).Append("'><b>[#")
                .Append(comparison.ProjectedRank).Append("]</b></font>");

        if (preferences.StatusEnabled)
        {
            var activeRun = session.ActiveBonus > 0 ? session.BonusRun : session.Run;
            var (stateText, stateColor) = session.Practice.IsActive
                ? (session.Practice.IsNoclip ? "Practice | Noclip" : "Practice", "#ffd166")
                : GetRunStateDisplay(activeRun.State);
            html.Append(" <font color='#68737c'>· </font><font color='#aab2b8'>");
            if (session.ActiveBonus > 0)
                html.Append("BONUS ").Append(session.ActiveBonus).Append(" · ");
            else if (stageCount > 0)
                html.Append("STAGE ").Append(Math.Max(1, session.Run.CurrentStage)).Append('/').Append(stageCount).Append(" · ");
            html.Append("</font><font color='").Append(stateColor).Append("'><b>")
                .Append(stateText.ToUpperInvariant()).Append("</b></font>");
        }

        html.Append("<br><nobr><font color='#7f8991'>PB&#160;</font><font color='#d2d7db'>")
            .Append(comparison.PersonalBest is null ? "--:--.---" : TimerManager.FormatTime(comparison.PersonalBest.Time))
            .Append("&#160;[")
            .Append(comparison.PersonalBest is null ? "-" : comparison.PersonalBest.Rank)
            .Append('/').Append(comparison.TotalRecords)
            .Append("]&#160;</font><font color='#68737c'>·</font><font color='#aab2b8'>&#160;T")
            .Append(maps.Current?.Configuration.Tier ?? 1)
            .Append("&#160;·&#160;").Append(stageCount > 0 ? "STAGED" : "LINEAR")
            .Append("</font></nobr>");

        if (preferences.KeysEnabled)
            html.Append("<br>").Append(BuildKeysHtml(pressedButtons));
        // CS2's center-HTML panel drops/clips the final line unless the
        // message ends with a break. Keep the disposable empty line last.
        html.Append("<br>");
        return html.ToString();
    }

    private string BuildSpectatorHtml(SurfPlayerSession target,PlayerPreferences viewerPreferences,string targetName,
        long elapsed,int speed,int maxVelocity,int checkpoints,int stages,ulong buttons,int spectators)
    {
        var html=new StringBuilder(320);
        html.Append("<font color='#ffd166' size='3'>SPECTATING ").Append(System.Net.WebUtility.HtmlEncode(targetName)).Append("</font><br>");
        html.Append(BuildHtml(target,viewerPreferences,elapsed,speed,maxVelocity,checkpoints,stages,buttons));
        html.Append("<font color='#c8c8c8' size='3'>");
        html.Append("Spectators: ").Append(spectators).Append("</font><br>");
        return html.ToString();
    }

    private HudComparison GetHudComparison(SurfPlayerSession target,long elapsed)
    {
        var map=maps.Current?.Name;
        if(string.IsNullOrWhiteSpace(map)) return new(null,1,0);
        RequestStandings(map);
        var times=_standingsCache.TryGetValue(map,out var standings)?standings.Times:[];
        var projected=target.Run.State==RunState.Running?1+CountFaster(times,elapsed):1;
        return new(GetCachedPb(target),projected,times.Count);
    }

    private CachedPb? GetCachedPb(SurfPlayerSession target)
    {
        var map=maps.Current?.Name;
        if(!target.IsAuthorized || string.IsNullOrWhiteSpace(map)) return null;
        var key=(target.SteamId,map);
        if(_pbCache.TryGetValue(key,out var cached) && DateTimeOffset.UtcNow-cached.LoadedAt<TimeSpan.FromSeconds(30)) return cached;
        if(_pbRequests.Add(key)) _=LoadPbAsync(key);
        return cached;
    }

    private async Task LoadPbAsync((ulong SteamId,string Map) key)
    {
        try
        {
            var pb=await records.GetPersonalBestDetailsAsync(key.SteamId,key.Map).ConfigureAwait(false);
            core.Scheduler.NextTick(()=>
            {
                if(pb is not null) _pbCache[key]=new(pb.TimeMicroseconds,pb.Rank,pb.TotalRecords,DateTimeOffset.UtcNow);
                else _pbCache.Remove(key);
                _pbRequests.Remove(key);
            });
        }
        catch(Exception exception)
        {
            logger.LogWarning(exception,"Could not refresh spectator PB for {SteamId} on {Map}.",key.SteamId,key.Map);
            core.Scheduler.NextTick(()=>_pbRequests.Remove(key));
        }
    }

    private void RequestStandings(string map)
    {
        if(_standingsCache.TryGetValue(map,out var cached) && DateTimeOffset.UtcNow-cached.LoadedAt<TimeSpan.FromSeconds(15)) return;
        if(_standingsRequests.Add(map)) _=LoadStandingsAsync(map);
    }

    private async Task LoadStandingsAsync(string map)
    {
        try
        {
            var times=await records.GetRankedTimesAsync(map).ConfigureAwait(false);
            core.Scheduler.NextTick(()=>
            {
                _standingsCache[map]=new(times,DateTimeOffset.UtcNow);
                _standingsRequests.Remove(map);
            });
        }
        catch(Exception exception)
        {
            logger.LogWarning(exception,"Could not refresh live standings for {Map}.",map);
            core.Scheduler.NextTick(()=>_standingsRequests.Remove(map));
        }
    }

    private static int CountFaster(IReadOnlyList<long> times,long elapsed)
    {
        var low=0; var high=times.Count;
        while(low<high)
        {
            var middle=low+((high-low)/2);
            if(times[middle]<elapsed) low=middle+1; else high=middle;
        }
        return low;
    }

    private static bool TryGetObserved(IPlayer viewer,out IPlayer target)
    {
        target=null!;
        try
        {
            if(viewer.IsAlive) return false;
            var handle=viewer.PlayerPawn?.ObserverServices?.ObserverTarget;
            if(handle is null || !handle.Value.IsValid || handle.Value.Value is not { } pawn) return false;
            var observedPlayer=pawn.As<CBasePlayerPawn>().ToPlayer();
            if(observedPlayer is null || !observedPlayer.IsValid || observedPlayer.PlayerID==viewer.PlayerID) return false;
            target=observedPlayer;
            return true;
        }
        catch { return false; }
    }

    private sealed record CachedPb(long Time,int Rank,int Total,DateTimeOffset LoadedAt);
    private sealed record CachedStandings(IReadOnlyList<long> Times,DateTimeOffset LoadedAt);
    private sealed record HudComparison(CachedPb? PersonalBest,int ProjectedRank,int TotalRecords);

    private static string BuildKeysHtml(ulong buttons) =>
        $"<font class='stratum-light-mono'>{KeyHtml(buttons,512,'A')} {KeyHtml(buttons,8,'W')} {KeyHtml(buttons,1024,'D')} " +
        $"{KeyHtml(buttons,16,'S')} {KeyHtml(buttons,2,'J')} {KeyHtml(buttons,4,'C')}</font>";

    private static string KeyHtml(ulong buttons,ulong flag,char label) =>
        (buttons&flag)!=0
            ? $"<font color='#f4f7f8'><b>[{label}]</b></font>"
            : $"<font color='#59636b'>[{label}]</font>";

    private static string GetRankColor(int rank) => rank switch
    {
        1 => "#ffd166",
        <= 10 => "#c792ea",
        _ => "#aab2b8"
    };

    private static (string Text, string Color) GetRunStateDisplay(RunState state) => state switch
    {
        RunState.Armed => ("Ready", "#90ee90"),
        RunState.Running => ("Running", "#4caf50"),
        RunState.Finished => ("Finished", "#ff5c5c"),
        _ => ("Idle", "#c8c8c8")
    };

    private static int GetHorizontalSpeed(SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerPawn? pawn)
    {
        if (pawn is null || !pawn.IsValid) return 0;
        var velocity = pawn.AbsVelocity;
        return (int)Math.Round(Math.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y)));
    }

    private static string GetSpeedColor(int speed, int maxVelocity)
    {
        if (speed >= maxVelocity) return "#90ee90";

        var amount = Math.Clamp(speed / (double)maxVelocity, 0d, 1d);
        const int orangeRed = 0xff;
        const int orangeGreen = 0x9f;
        const int orangeBlue = 0x1c;
        const int redRed = 0xff;
        const int redGreen = 0x3b;
        const int redBlue = 0x30;
        var red = (int)Math.Round(orangeRed + ((redRed - orangeRed) * amount));
        var green = (int)Math.Round(orangeGreen + ((redGreen - orangeGreen) * amount));
        var blue = (int)Math.Round(orangeBlue + ((redBlue - orangeBlue) * amount));
        return $"#{red:x2}{green:x2}{blue:x2}";
    }

    private int GetCheckpointCount()
    {
        return maps.CheckpointCount;
    }
}
