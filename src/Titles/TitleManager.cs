using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Storage;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace SurfTimer.Titles;

public sealed class TitleManager(ISwiftlyCore core, RecordRepository records, PlayerTitleRepository repository,
    SurfTimerOptions options, ILogger<TitleManager> logger)
{
    private readonly Dictionary<ulong,PlayerTitleDisplay> _cache=[];
    private Guid? _chatHook;
    private Guid? _activateHook;

    public void Start(){_chatHook??=core.GameEvent.HookPre<EventPlayerChat>(OnPlayerChat);_activateHook??=core.GameEvent.HookPost<EventPlayerActivate>(OnPlayerActivate);}
    public void Stop(){if(_chatHook is{} chat)core.GameEvent.Unhook(chat);if(_activateHook is{} activate)core.GameEvent.Unhook(activate);_chatHook=null;_activateHook=null;_cache.Clear();}

    public async Task<PlayerTitleDisplay> ResolveAsync(ulong steamId)
    {
        var rankingTask = records.GetPlayerOverallRankingAsync(steamId);
        var settingsTask = repository.LoadAsync(steamId);
        await Task.WhenAll(rankingTask, settingsTask).ConfigureAwait(false);
        var competitive = rankingTask.Result?.Title ?? "Provisional";
        var settings = settingsTask.Result;
        var custom = options.Titles.Enabled && settings.IsVip && !string.IsNullOrWhiteSpace(settings.CustomTitle);
        var text = custom ? settings.CustomTitle! : competitive;
        var colors = custom && !string.IsNullOrWhiteSpace(settings.ColorPattern)
            ? settings.ColorPattern! : TitlePalette.DefaultFor(competitive);
        return new(text, colors, settings.NameColor, custom, competitive);
    }

    public async Task ApplyAsync(int playerId, ulong sessionId, ulong steamId)
    {
        if (!options.Titles.Enabled || steamId == 0) return;
        try
        {
            var display = await ResolveAsync(steamId).ConfigureAwait(false);
            core.Scheduler.NextWorldUpdate(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId || !player.IsValid) return;
                player.Controller.Clan = display.ScoreboardText;
                player.Controller.ClanUpdated();
                _cache[steamId]=display;
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to apply title for {SteamId}.", steamId);
        }
    }

    private HookResult OnPlayerActivate(EventPlayerActivate gameEvent)
    {
        var player=core.PlayerManager.GetPlayer(gameEvent.UserId);
        if(player is null||player.IsFakeClient||player.SteamID==0)return HookResult.Continue;
        _=ApplyAsync(player.PlayerID,player.SessionId,player.SteamID);
        return HookResult.Continue;
    }

    private HookResult OnPlayerChat(EventPlayerChat gameEvent)
    {
        var player=gameEvent.UserIdPlayer;
        if(player is null||player.SteamID==0||string.IsNullOrWhiteSpace(gameEvent.Text)||gameEvent.Text[0] is '!' or '/')return HookResult.Continue;
        if(!_cache.TryGetValue(player.SteamID,out var display))return HookResult.Continue;
        var line=$"[default][{TitlePalette.RenderChat(display.Text,display.ColorPattern)}[default]] [{display.NameColor}]{player.Name}[/][default]: {gameEvent.Text}[/]";
        if(gameEvent.TeamOnly)
        {
            foreach(var recipient in core.PlayerManager.GetAllPlayers().Where(value=>value.IsValid&&value.Controller.TeamNum==player.Controller.TeamNum))recipient.SendChat(line);
        }
        else core.PlayerManager.SendChat(line);
        return HookResult.Stop;
    }
}
