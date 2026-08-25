using Microsoft.Extensions.Logging;
using SurfTimer.Chat;
using SurfTimer.Maps;
using SurfTimer.Storage;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class ProfileCommands(ISwiftlyCore core, MapLifecycle maps, RecordRepository records, ILogger<ProfileCommands> logger)
{
    private readonly List<Guid> _registrations=[];
    public void Register()
    {
        if (_registrations.Count!=0) return;
        foreach (var name in new[] { "sw_profile", "sw_mapstats", "sw_points", "sw_ranks", "css_profile", "css_mapstats", "css_points", "css_ranks" })
            core.Command.UnregisterCommand(name);
        _registrations.Add(core.Command.RegisterCommand("surftimerprofile", OnProfile, registerRaw: false,
            helpText: "Shows a global SurfTimer profile: !profile [player]."));
        _registrations.Add(core.Command.RegisterCommand("surftimermapstats", OnMapStats, registerRaw: false,
            helpText: "Shows global statistics for the current map."));
        _registrations.Add(core.Command.RegisterCommand("surftimerpoints",OnPoints,registerRaw:false,helpText:"Shows overall points and rank: !points [player]."));
        _registrations.Add(core.Command.RegisterCommand("surftimerranks",OnRanks,registerRaw:false,helpText:"Shows the global points top ten."));
        core.Command.RegisterCommandAlias("sw_surftimerprofile","profile",false);
        core.Command.RegisterCommandAlias("sw_surftimermapstats","mapstats",false);
        core.Command.RegisterCommandAlias("sw_surftimerprofile","css_profile",true);
        core.Command.RegisterCommandAlias("sw_surftimermapstats","css_mapstats",true);
        core.Command.RegisterCommandAlias("sw_surftimerpoints","points",false);
        core.Command.RegisterCommandAlias("sw_surftimerranks","ranks",false);
        core.Command.RegisterCommandAlias("sw_surftimerpoints","css_points",true);
        core.Command.RegisterCommandAlias("sw_surftimerranks","css_ranks",true);
    }
    public void Unregister()
    {
        foreach (var name in new[] { "sw_profile", "sw_mapstats", "sw_points", "sw_ranks", "css_profile", "css_mapstats", "css_points", "css_ranks" })
            core.Command.UnregisterCommand(name);
        foreach(var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    private void OnProfile(ICommandContext context)
    {
        if (!Capture(context,out var playerId,out var sessionId)) return;
        _=ShowProfileAsync(playerId,sessionId,context.Sender!.SteamID,string.Join(' ',context.Args));
    }
    private void OnMapStats(ICommandContext context)
    {
        if (!Capture(context,out var playerId,out var sessionId)) return;
        var map=maps.Current?.Name;
        if (map is null) { context.Reply(ChatFormat.Error("No map is active.")); return; }
        _=ShowMapStatsAsync(playerId,sessionId,map);
    }
    private void OnPoints(ICommandContext context){if(!Capture(context,out var playerId,out var sessionId))return;_=ShowPointsAsync(playerId,sessionId,context.Sender!.SteamID,string.Join(' ',context.Args));}
    private void OnRanks(ICommandContext context){if(!Capture(context,out var playerId,out var sessionId))return;_=ShowRanksAsync(playerId,sessionId);}
    private bool Capture(ICommandContext context,out int playerId,out ulong sessionId)
    {
        playerId=0; sessionId=0;
        if (!context.IsSentByPlayer || context.Sender is null) { context.Reply("This command requires a player caller."); return false; }
        playerId=context.Sender.PlayerID; sessionId=context.Sender.SessionId; return true;
    }
    private async Task ShowProfileAsync(int playerId,ulong sessionId,ulong caller,string query)
    {
        try
        {
            ulong target=caller;
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matches=await records.FindPlayersAsync(query).ConfigureAwait(false);
                if (matches.Count!=1) { Reply(playerId,sessionId,matches.Count==0?ChatFormat.Error("Player not found."):ChatFormat.Warning("Multiple players matched; use a fuller name or SteamID64.")); return; }
                target=matches[0].SteamId;
            }
            var p=await records.GetGlobalPlayerProfileAsync(target).ConfigureAwait(false);
            if (p is null) { Reply(playerId,sessionId,ChatFormat.Error("Player profile not found.")); return; }
            var tracked=p.TrackingStartedAt is null?"not started":$"{FormatDuration(p.TrackedTimeMicroseconds)} across {p.TrackedCompletions} finishes (since {p.TrackingStartedAt:yyyy-MM-dd})";
            var overall=await records.GetPlayerOverallRankingAsync(target).ConfigureAwait(false);
            var lines = new List<string> { ChatFormat.Header($"Profile · {p.PlayerName}") };
            lines.Add(overall is null
                ? ChatFormat.Row("RANK ·", "Unranked", ChatFormat.MutedColor)
                : $"{ChatFormat.Rank(overall.Rank)} {overall.Title} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.HighlightColor}{overall.Points:N0} Points{ChatFormat.Reset}");
            lines.Add(ChatFormat.Row("ACTIVITY ·",
                $"{p.UniqueMaps} maps · {p.Completions} completions · {p.WorldRecords} WRs"));
            if (overall is not null)
            {
                lines.Add(ChatFormat.Row("POINTS ·",
                    $"Map {overall.MapPoints:N0} · Bonus {overall.BonusPoints:N0} · Stage {overall.StagePoints:N0}"));
                lines.Add(ChatFormat.Row("GROUPS ·",
                    $"G1 {overall.Group1} · G2 {overall.Group2} · G3 {overall.Group3} · G4 {overall.Group4} · G5 {overall.Group5}"));
            }
            lines.Add(ChatFormat.Row("RECORDS ·",
                $"Main {p.MainRecords} · Bonus {p.BonusRecords} · Stage {p.StageRecords} · Replays {p.Replays}"));
            lines.Add(ChatFormat.Row("SURF TIME ·", tracked));
            if (p.RecentPersonalBests.Count>0)
            {
                lines.Add(ChatFormat.Header("Recent PBs"));
                lines.AddRange(p.RecentPersonalBests.Select(x =>
                    $"{ChatFormat.MutedColor}•{ChatFormat.Reset} {x.MapName}{(x.RouteType=="bonus"?$" B{x.RouteIndex}":"")} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.SuccessColor}{TimerManager.FormatTime(x.TimeMicroseconds)}{ChatFormat.Reset}"));
            }
            ReplyLines(playerId,sessionId,lines);
        }
        catch(Exception e) { logger.LogError(e,"Profile command failed."); Reply(playerId,sessionId,ChatFormat.Error("Profile is temporarily unavailable.")); }
    }
    private async Task ShowPointsAsync(int playerId,ulong sessionId,ulong caller,string query)
    {
        try{ulong target=caller;if(!string.IsNullOrWhiteSpace(query)){var matches=await records.FindPlayersAsync(query).ConfigureAwait(false);if(matches.Count!=1){Reply(playerId,sessionId,matches.Count==0?ChatFormat.Error("Player not found."):ChatFormat.Warning("Multiple players matched; use a fuller name or SteamID64."));return;}target=matches[0].SteamId;}var value=await records.GetPlayerOverallRankingAsync(target).ConfigureAwait(false);if(value is null){Reply(playerId,sessionId,ChatFormat.Message("No Points yet."));return;}ReplyLines(playerId,sessionId,new[]{ChatFormat.Header($"Points · {value.PlayerName}"),$"{ChatFormat.Rank(value.Rank)} {value.Title} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.HighlightColor}{value.Points:N0} Points{ChatFormat.Reset} {ChatFormat.MutedColor}· {value.CompletedMaps} maps{ChatFormat.Reset}",ChatFormat.Row("BREAKDOWN ·",$"Map {value.MapPoints:N0} · Bonus {value.BonusPoints:N0} · Stage {value.StagePoints:N0}"),ChatFormat.Row("GROUPS ·",$"G1 {value.Group1} · G2 {value.Group2} · G3 {value.Group3} · G4 {value.Group4} · G5 {value.Group5}")});}
        catch(Exception e){logger.LogError(e,"Points command failed.");Reply(playerId,sessionId,ChatFormat.Error("Overall rankings are temporarily unavailable."));}
    }
    private async Task ShowRanksAsync(int playerId,ulong sessionId)
    {
        try{var top=await records.GetOverallRankingsAsync().ConfigureAwait(false);var lines=new List<string>{ChatFormat.Header("Global Points · Top 10")};lines.AddRange(top.Select(x=>$"{ChatFormat.Rank(x.Rank)} {x.PlayerName} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.HighlightColor}{x.Points:N0}{ChatFormat.Reset} Points {ChatFormat.MutedColor}· {x.Title} · {x.CompletedMaps} maps{ChatFormat.Reset}"));if(top.Count==0)lines.Add(ChatFormat.Row("", "No ranked players yet.",ChatFormat.MutedColor));ReplyLines(playerId,sessionId,lines);}
        catch(Exception e){logger.LogError(e,"Rankings command failed.");Reply(playerId,sessionId,ChatFormat.Error("Overall rankings are temporarily unavailable."));}
    }
    private async Task ShowMapStatsAsync(int playerId,ulong sessionId,string map)
    {
        try
        {
            var s=await records.GetGlobalMapStatisticsAsync(map).ConfigureAwait(false);
            if (s is null || s.UniquePlayers==0) { Reply(playerId,sessionId,ChatFormat.Message($"{map} has no completed main-map runs.")); return; }
            ReplyLines(playerId,sessionId,new[]{ChatFormat.Header($"Map Stats · {map}"),ChatFormat.Row("ACTIVITY ·",$"{s.Completions} completions · {s.UniquePlayers} players"),$"{ChatFormat.HighlightColor}WR{ChatFormat.Reset} · {s.WorldRecordPlayer} · {ChatFormat.HighlightColor}{TimerManager.FormatTime(s.WorldRecordMicroseconds!.Value)}{ChatFormat.Reset}",ChatFormat.Row("PB TIMES ·",$"Average {TimerManager.FormatTime(s.AveragePersonalBestMicroseconds!.Value)} · Median {TimerManager.FormatTime(s.MedianPersonalBestMicroseconds!.Value)}")});
        }
        catch(Exception e) { logger.LogError(e,"Map stats command failed for {Map}.",map); Reply(playerId,sessionId,ChatFormat.Error("Map statistics are temporarily unavailable.")); }
    }
    private void Reply(int playerId,ulong sessionId,string message)=>core.Scheduler.NextTick(()=>{var p=core.PlayerManager.GetPlayer(playerId);if(p is not null&&p.SessionId==sessionId)p.SendChat(message);});
    private void ReplyLines(int playerId, ulong sessionId, IReadOnlyList<string> lines) => core.Scheduler.NextTick(() =>
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is null || player.SessionId != sessionId) return;
        foreach (var line in lines) player.SendChat(line);
    });
    private static string FormatDuration(long us)=>TimeSpan.FromTicks(us*10) is var t ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : "0:00:00";
}
