using Microsoft.Extensions.Logging;
using SurfTimer.Maps;
using SurfTimer.Replays;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class ReplayCommands(
    ISwiftlyCore core,
    MapLifecycle maps,
    ReplayPlaybackManager playback,
    ILogger<ReplayCommands> logger)
{
    private Guid? _registration;
    private Guid? _bonusRegistration;
    private Guid? _stageRegistration;

    public void Register()
    {
        if (_registration is not null) return;
        // BotController exposes developer test commands under the same public
        // name. SurfTimer owns !replay on a surf server.
        core.Command.UnregisterCommand("sw_replay");
        _registration = core.Command.RegisterCommand("replay", OnReplay, registerRaw: false,
            helpText: "Plays a global top-10 replay: !replay [1-10].");
        core.Command.RegisterCommandAlias("sw_replay", "css_replay", registerRaw: true);
        _bonusRegistration = core.Command.RegisterCommand("bonusreplay", OnBonusReplay, registerRaw: false,
            helpText: "Plays a bonus top-10 replay: !breplay <bonus> [rank].");
        core.Command.RegisterCommandAlias("sw_bonusreplay", "breplay", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_bonusreplay", "css_bonusreplay", registerRaw: true);
        _stageRegistration = core.Command.RegisterCommand("stagereplay", OnStageReplay, registerRaw: false,
            helpText: "Plays a stage top-10 replay: !stagereplay <stage> [rank].");
        core.Command.RegisterCommandAlias("sw_stagereplay", "sreplay", registerRaw: false);
        core.Command.RegisterCommandAlias("sw_stagereplay", "css_stagereplay", registerRaw: true);
    }

    public void Unregister()
    {
        core.Command.UnregisterCommand("css_replay");
        core.Command.UnregisterCommand("sw_breplay");
        core.Command.UnregisterCommand("css_bonusreplay");
        core.Command.UnregisterCommand("sw_sreplay");
        core.Command.UnregisterCommand("css_stagereplay");
        if (_registration is { } registration) core.Command.UnregisterCommand(registration);
        if (_bonusRegistration is { } bonus) core.Command.UnregisterCommand(bonus);
        if (_stageRegistration is { } stage) core.Command.UnregisterCommand(stage);
        _registration = null;
        _bonusRegistration = null;
        _stageRegistration = null;
    }

    private void OnStageReplay(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        var map=maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer] No map is active."); return; }
        if (context.Args.Length is < 1 or > 2 || !int.TryParse(context.Args[0],out var stage) || stage<1 || stage>maps.StageCount)
        { context.Reply($"[SurfTimer] Usage: !stagereplay <1-{maps.StageCount}> [1-10]"); return; }
        var rank=1;
        if (context.Args.Length==2 && (!int.TryParse(context.Args[1],out rank) || rank is <1 or >10))
        { context.Reply($"[SurfTimer] Usage: !stagereplay <1-{maps.StageCount}> [1-10]"); return; }
        _=SelectStageAsync(map,stage,rank,context.Sender.PlayerID,context.Sender.SessionId);
    }

    private async Task SelectStageAsync(string map,int stage,int rank,int playerId,ulong sessionId)
    {
        try
        {
            var replay=await playback.SelectStageAsync(map,stage,rank).ConfigureAwait(false);
            if (replay is not null) playback.Watch(playerId,sessionId);
            Reply(playerId,sessionId,replay is null
                ? $"[SurfTimer] Stage {stage} rank #{rank} has no PB replay."
                : $"[SurfTimer] Playing Stage {stage} #{rank} {replay.PlayerName} — {TimerManager.FormatTime(replay.TimeMicroseconds)}");
        }
        catch(Exception exception)
        {
            logger.LogError(exception,"Failed to select Stage {Stage} replay rank {Rank}.",stage,rank);
            Reply(playerId,sessionId,"[SurfTimer] Could not load that stage replay.");
        }
    }

    private void OnBonusReplay(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer] No map is active."); return; }
        if (context.Args.Length is < 1 or > 2 || !int.TryParse(context.Args[0], out var bonus) ||
            bonus < 1 || bonus > maps.BonusCount)
        { context.Reply($"[SurfTimer] Usage: !breplay <1-{maps.BonusCount}> [1-10]"); return; }
        var rank = 1;
        if (context.Args.Length == 2 && (!int.TryParse(context.Args[1], out rank) || rank is < 1 or > 10))
        { context.Reply($"[SurfTimer] Usage: !breplay <1-{maps.BonusCount}> [1-10]"); return; }
        _ = SelectBonusAsync(map, bonus, rank, context.Sender.PlayerID, context.Sender.SessionId);
    }

    private async Task SelectBonusAsync(string map, int bonus, int rank, int playerId, ulong sessionId)
    {
        try
        {
            var replay = await playback.SelectBonusAsync(map, bonus, rank).ConfigureAwait(false);
            if (replay is not null) playback.Watch(playerId, sessionId);
            Reply(playerId, sessionId, replay is null
                ? $"[SurfTimer] Bonus {bonus} rank #{rank} has no PB replay."
                : $"[SurfTimer] Playing Bonus {bonus} #{rank} {replay.PlayerName} — {TimerManager.FormatTime(replay.TimeMicroseconds)}");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to select Bonus {Bonus} replay rank {Rank}.", bonus, rank);
            Reply(playerId, sessionId, "[SurfTimer] Could not load that bonus replay.");
        }
    }

    private void OnReplay(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer] No map is active."); return; }
        if (context.Args.Length > 0 && context.Args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            playback.StopWatching(context.Sender.SessionId);
            context.Reply("[SurfTimer] Replay viewing stopped.");
            return;
        }
        var rank = 1;
        if (context.Args.Length > 0 && (!int.TryParse(context.Args[0], out rank) || rank is < 1 or > 10))
        { context.Reply("[SurfTimer] Usage: !replay [1-10]"); return; }
        var playerId = context.Sender.PlayerID; var sessionId = context.Sender.SessionId;
        _ = SelectAsync(map, rank, playerId, sessionId);
    }

    private async Task SelectAsync(string map, int rank, int playerId, ulong sessionId)
    {
        try
        {
            var replay = await playback.SelectAsync(map, rank).ConfigureAwait(false);
            if (replay is not null) playback.Watch(playerId, sessionId);
            Reply(playerId, sessionId, replay is null
                ? $"[SurfTimer] Global rank #{rank} has no PB replay."
                : $"[SurfTimer] Playing #{rank} {replay.PlayerName} — {TimerManager.FormatTime(replay.TimeMicroseconds)}");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to select replay rank {Rank}.", rank);
            Reply(playerId, sessionId, "[SurfTimer] Could not load that replay.");
        }
    }

    private void Reply(int playerId, ulong sessionId, string text) => core.Scheduler.NextTick(() =>
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is not null && player.SessionId == sessionId) player.SendChat(text);
    });
}
