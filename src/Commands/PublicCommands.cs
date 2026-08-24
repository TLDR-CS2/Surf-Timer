using Microsoft.Extensions.Logging;
using SurfTimer.Maps;
using SurfTimer.Storage;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class PublicCommands(
    ISwiftlyCore core,
    MapLifecycle maps,
    RecordRepository records,
    ILogger<PublicCommands> logger)
{
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0) return;
        foreach (var name in new[] { "sw_help", "sw_commands", "sw_mapinfo", "sw_stages", "sw_bonuses", "css_help", "css_commands", "css_mapinfo", "css_stages", "css_bonuses" })
            core.Command.UnregisterCommand(name);
        _registrations.Add(core.Command.RegisterCommand("surftimerhelp", OnHelp, registerRaw: false,
            helpText: "Shows SurfTimer player commands."));
        _registrations.Add(core.Command.RegisterCommand("surftimermapinfo", OnMapInfo, registerRaw: false,
            helpText: "Shows tier, checkpoints, records, and WR for the current map."));
        _registrations.Add(core.Command.RegisterCommand("surftimerstages", OnStages, registerRaw: false,
            helpText: "Shows stage support and commands for the current map."));
        _registrations.Add(core.Command.RegisterCommand("surftimerbonuses", OnBonuses, registerRaw: false,
            helpText: "Shows configured bonuses and commands for the current map."));
        core.Command.RegisterCommandAlias("sw_surftimerhelp", "help", false);
        core.Command.RegisterCommandAlias("sw_surftimerhelp", "commands", false);
        core.Command.RegisterCommandAlias("sw_surftimerhelp", "css_help", true);
        core.Command.RegisterCommandAlias("sw_surftimerhelp", "css_commands", true);
        core.Command.RegisterCommandAlias("sw_surftimermapinfo", "mapinfo", false);
        core.Command.RegisterCommandAlias("sw_surftimermapinfo", "css_mapinfo", true);
        core.Command.RegisterCommandAlias("sw_surftimerstages", "stages", false);
        core.Command.RegisterCommandAlias("sw_surftimerstages", "css_stages", true);
        core.Command.RegisterCommandAlias("sw_surftimerbonuses", "bonuses", false);
        core.Command.RegisterCommandAlias("sw_surftimerbonuses", "css_bonuses", true);
    }

    public void Unregister()
    {
        foreach (var name in new[] { "sw_help", "sw_commands", "css_help", "css_commands", "sw_mapinfo", "css_mapinfo", "sw_stages", "css_stages", "sw_bonuses", "css_bonuses" })
            core.Command.UnregisterCommand(name);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    private static void OnHelp(ICommandContext context)
    {
        context.Reply("[SurfTimer] Core — !r/!restart | !pb [player] | !rank [player] | !top/!top10 | !wr | !mapinfo");
        context.Reply("[SurfTimer] Overall — !points [player] | !ranks | !profile [player] | !mapstats");
        context.Reply("[SurfTimer] Stats — !profile [player] | !mapstats");
        context.Reply("[SurfTimer] Replay — !replay [1-10] | !breplay <bonus> [rank] | !stagereplay <stage> [rank] | !replay stop");
        context.Reply("[SurfTimer] Routes — !stages | !bonuses | !s <stage> | !rs | !b <bonus> | !rb");
        context.Reply("[SurfTimer] Practice — !saveloc | !tele | !teleprev | !telenext | !noclip | !ncspeed");
        context.Reply("[SurfTimer] Settings — !settings | !hud | !speed | !status | !keys | !sounds | !replayhud");
        context.Reply("[SurfTimer] Add 'on' or 'off' to a setting command, or omit it to toggle.");
    }

    private void OnStages(ICommandContext context)
    {
        if (maps.Current is null) { context.Reply("[SurfTimer] No map is active."); return; }
        if (maps.StageCount <= 0)
        { context.Reply($"[SurfTimer] {maps.Current.Name} is linear and has no timed stages."); return; }
        context.Reply($"[SurfTimer] {maps.Current.Name} has {maps.StageCount} stages.");
        context.Reply("[SurfTimer] !s <stage> · !rs · !stagepb <stage> · !stagewr <stage> · !stagetop <stage> · !stagereplay <stage> [rank]");
    }

    private void OnBonuses(ICommandContext context)
    {
        if (maps.Current is null) { context.Reply("[SurfTimer] No map is active."); return; }
        if (maps.BonusCount <= 0)
        { context.Reply($"[SurfTimer] {maps.Current.Name} has no configured bonuses."); return; }
        context.Reply($"[SurfTimer] {maps.Current.Name} has {maps.BonusCount} bonuses: {string.Join(", ", Enumerable.Range(1, maps.BonusCount).Select(value => $"B{value}"))}.");
        context.Reply("[SurfTimer] !b <bonus> · !rb · !bonuspb <bonus> · !bonuswr <bonus> · !bonustop <bonus> · !breplay <bonus> [rank]");
    }

    private void OnMapInfo(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null)
        {
            context.Reply("This command requires a player caller.");
            return;
        }
        var map = maps.Current;
        if (map is null)
        {
            context.Reply("[SurfTimer] No map is active.");
            return;
        }
        var playerId = context.Sender.PlayerID;
        var sessionId = context.Sender.SessionId;
        _ = ShowMapInfoAsync(map.Name, map.Configuration.Tier, maps.CheckpointCount, maps.StageCount,
            map.Configuration.MaxVelocity, playerId, sessionId);
    }

    private async Task ShowMapInfoAsync(string map, int tier, int checkpoints, int stages, int maxVelocity, int playerId, ulong sessionId)
    {
        try
        {
            var summary = await records.GetMapSummaryAsync(map).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                var route = stages > 0 ? $"{stages} stages" : $"{checkpoints} checkpoints";
                player.SendChat($"[SurfTimer] {map} — Tier {tier} | {route} | max velocity {maxVelocity} u/s | {summary.RecordCount} records");
                if (summary.WorldRecord is { } wr)
                    player.SendChat($"[SurfTimer] WR — {wr.PlayerName} | {TimerManager.FormatTime(wr.TimeMicroseconds)}");
                else
                    player.SendChat("[SurfTimer] No world record has been set yet.");
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Map info command failed for {Map}.", map);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is not null && player.SessionId == sessionId)
                    player.SendChat("[SurfTimer] Map record information is temporarily unavailable.");
            });
        }
    }
}
