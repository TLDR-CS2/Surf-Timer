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
                var pb=GetCachedPb(targetSession);
                player.SendCenterHTML(BuildSpectatorHtml(targetSession,session.Preferences,target.Name,targetElapsed,targetSpeed,maxVelocity,
                    checkpointCount,stageCount,(ulong)target.PressedButtons,spectatorCounts.GetValueOrDefault(target.PlayerID),pb),MessageDurationMilliseconds);
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
        $"<font color='#ffd166' size='6'><b>{TimerManager.FormatTime(replay.ElapsedMicroseconds)}</b></font><br>" +
        $"<font color='{GetSpeedColor(horizontalSpeed, maxVelocity)}' size='4'>{horizontalSpeed} u/s</font><br>" +
        $"<font color='#c8c8c8' size='3'>REPLAY #{replay.Rank} | {System.Net.WebUtility.HtmlEncode(replay.PlayerName)} | {TimerManager.FormatTime(replay.TotalMicroseconds)}</font>" +
        (keysEnabled ? $"<br><font color='#d8d8d8' size='3'>{BuildKeys(replay.Buttons)}</font>" : string.Empty);

    private static string BuildHtml(
        SurfPlayerSession session,
        PlayerPreferences preferences,
        long elapsedMicroseconds,
        int horizontalSpeed,
        int maxVelocity,
        int checkpointCount,
        int stageCount,
        ulong pressedButtons)
    {
        var html = new StringBuilder(192);
        html.Append("<font color='#58d6ff' size='6'><b>")
            .Append(TimerManager.FormatTime(elapsedMicroseconds))
            .Append("</b></font>");

        if (preferences.SpeedEnabled)
        {
            html.Append("<br><font color='")
                .Append(GetSpeedColor(horizontalSpeed, maxVelocity))
                .Append("' size='4'>")
                .Append(horizontalSpeed)
                .Append(" u/s</font>");
        }

        if (preferences.StatusEnabled)
        {
            html.Append("<br>");
            if (session.ActiveBonus > 0)
            {
                html.Append("<font color='#c8c8c8' size='3'>Bonus ")
                    .Append(session.ActiveBonus)
                    .Append(" | </font>");
            }
            else if (stageCount > 0)
            {
                html.Append("<font color='#c8c8c8' size='3'>Stage ")
                    .Append(Math.Max(1, session.Run.CurrentStage))
                    .Append('/')
                    .Append(stageCount)
                    .Append(" | </font>");
            }
            else if (checkpointCount > 0)
            {
                html.Append("<font color='#c8c8c8' size='3'>Checkpoint ")
                    .Append(session.Run.LastCheckpoint)
                    .Append('/')
                    .Append(checkpointCount)
                    .Append(" | </font>");
            }

            var activeRun = session.ActiveBonus > 0 ? session.BonusRun : session.Run;
            var (stateText, stateColor) = session.Practice.IsActive
                ? (session.Practice.IsNoclip ? "Practice | Noclip" : "Practice", "#ffd166")
                : GetRunStateDisplay(activeRun.State);
            html.Append("<font color='")
                .Append(stateColor)
                .Append("' size='3'>")
                .Append(stateText)
                .Append("</font>");
        }

        if (preferences.KeysEnabled)
            html.Append("<br><font color='#d8d8d8' size='3'>")
                .Append(BuildKeys(pressedButtons)).Append("</font>");
        return html.ToString();
    }

    private static string BuildSpectatorHtml(SurfPlayerSession target,PlayerPreferences viewerPreferences,string targetName,
        long elapsed,int speed,int maxVelocity,int checkpoints,int stages,ulong buttons,int spectators,CachedPb? pb)
    {
        var html=new StringBuilder(320);
        html.Append("<font color='#ffd166' size='3'>SPECTATING ").Append(System.Net.WebUtility.HtmlEncode(targetName)).Append("</font><br>");
        html.Append(BuildHtml(target,viewerPreferences,elapsed,speed,maxVelocity,checkpoints,stages,buttons));
        html.Append("<br><font color='#c8c8c8' size='3'>");
        if(pb is null) html.Append("PB: --");
        else html.Append("PB: ").Append(TimerManager.FormatTime(pb.Time)).Append(" | Rank #").Append(pb.Rank);
        html.Append(" | Spectators: ").Append(spectators).Append("</font>");
        return html.ToString();
    }

    private CachedPb? GetCachedPb(SurfPlayerSession target)
    {
        var map=maps.Current?.Name;
        if(!target.IsAuthorized || string.IsNullOrWhiteSpace(map)) return null;
        var key=(target.SteamId,map);
        if(_pbCache.TryGetValue(key,out var cached) && DateTimeOffset.UtcNow-cached.LoadedAt<TimeSpan.FromSeconds(15)) return cached;
        if(_pbRequests.Add(key)) _=LoadPbAsync(key);
        return cached;
    }

    private async Task LoadPbAsync((ulong SteamId,string Map) key)
    {
        try
        {
            var pb=await records.GetPersonalBestAsync(key.SteamId,key.Map).ConfigureAwait(false);
            core.Scheduler.NextTick(()=>
            {
                if(pb is not null) _pbCache[key]=new(pb.TimeMicroseconds,pb.Rank,DateTimeOffset.UtcNow);
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

    private sealed record CachedPb(long Time,int Rank,DateTimeOffset LoadedAt);

    private static string BuildKeys(ulong buttons) =>
        $"{Key(buttons, 512, 'A')} {Key(buttons, 8, 'W')} {Key(buttons, 1024, 'D')} " +
        $"{Key(buttons, 16, 'S')} {Key(buttons, 2, 'J')} {Key(buttons, 4, 'C')}";

    private static char Key(ulong buttons, ulong flag, char label) => (buttons & flag) != 0 ? label : '_';

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
