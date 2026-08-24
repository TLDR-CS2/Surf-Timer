using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Maps;
using SurfTimer.Players;
using SurfTimer.Replays;
using SurfTimer.Storage;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class AdminCommands(
    ISwiftlyCore core,
    MapLifecycle maps,
    RecordRepository records,
    ReplayPlaybackManager playback,
    SurfPlayerManager players,
    SurfTimerOptions options,
    ILogger<AdminCommands> logger)
{
    private const string Permission = "surftimer.admin";
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0) return;
        Register("stadmin", OnStatus, "Shows SurfTimer administrative status.");
        Register("stmapreload", OnMapReload, "Reloads the current map configuration.");
        Register("stsettier", OnSetTier, "Changes the current map tier: !stsettier <1-7>.");
        Register("stmapenable", OnMapEnable, "Enables or disables the current map: !stmapenable <on|off>.");
        Register("stmapcheck", OnMapCheck, "Checks the loaded map's mapper triggers.");
        Register("stcatalogcheck", OnCatalogCheck, "Checks every map catalog configuration.");
        Register("stplayer", OnPlayer, "Inspects a global player: !stplayer <name|SteamID64>.");
        Register("stdeletepb", OnDeletePb, "Deletes a PB: !stdeletepb <player> confirm.");
        Register("streplayinfo", OnReplayInfo, "Inspects a replay: !streplayinfo <1-10>.");
        Register("stdeletereplay", OnDeleteReplay, "Deletes only a replay: !stdeletereplay <1-10> confirm.");
        Register("ststagereplayinfo", OnStageReplayInfo, "Inspects a stage replay: !ststagereplayinfo <stage> <rank>.");
        Register("stdeletestagereplay", OnDeleteStageReplay, "Deletes a stage replay: !stdeletestagereplay <stage> <rank> confirm.");
        Register("stbonusreplayinfo", OnBonusReplayInfo, "Inspects a bonus replay: !stbonusreplayinfo <bonus> <rank>.");
        Register("stdeletebonusreplay", OnDeleteBonusReplay, "Deletes a bonus replay: !stdeletebonusreplay <bonus> <rank> confirm.");
        Register("stvalidate", OnValidate, "Shows your current run-validation state.");
        Register("strecordcheck", OnRecordCheck, "Inspects stored run telemetry: !strecordcheck <1-10>.");
        foreach (var name in new[] { "stadmin", "stmapreload", "stsettier", "stmapenable", "stmapcheck", "stcatalogcheck", "stplayer", "stdeletepb", "streplayinfo", "stdeletereplay", "ststagereplayinfo", "stdeletestagereplay", "stbonusreplayinfo", "stdeletebonusreplay", "stvalidate", "strecordcheck" })
            core.Command.RegisterCommandAlias("sw_" + name, "css_" + name, registerRaw: true);
    }

    public void Unregister()
    {
        foreach (var name in new[] { "css_stadmin", "css_stmapreload", "css_stsettier", "css_stmapenable", "css_stmapcheck", "css_stcatalogcheck", "css_stplayer", "css_stdeletepb", "css_streplayinfo", "css_stdeletereplay", "css_ststagereplayinfo", "css_stdeletestagereplay", "css_stbonusreplayinfo", "css_stdeletebonusreplay", "css_stvalidate", "css_strecordcheck" })
            core.Command.UnregisterCommand(name);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    private void Register(string name, ICommandService.CommandListener callback, string help) =>
        _registrations.Add(core.Command.RegisterCommand(name, callback, registerRaw: false,
            permission: Permission, helpText: help));

    private void OnStatus(ICommandContext context)
    {
        var map = maps.Current;
        context.Reply($"[SurfTimer Admin] server={options.ServerId} database={records.Status} failures={records.ConsecutiveFailures}");
        context.Reply(map is null
            ? "[SurfTimer Admin] No active map."
            : $"[SurfTimer Admin] map={map.Name} tier={map.Configuration.Tier} enabled={map.Configuration.Enabled} checkpoints={maps.CheckpointCount} validation={maps.Validation.Summary}");
        context.Reply("[SurfTimer Admin] !stmapreload | !stsettier | !stmapenable | !stplayer | !stvalidate | !strecordcheck | !stdeletepb | !streplayinfo | !stdeletereplay");
    }

    private void OnValidate(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        SurfPlayerSession? current;
        if (context.Args.Length == 0) current = players.Get(context.Sender.PlayerID);
        else
        {
            var query = string.Join(' ', context.Args);
            var matches = players.Sessions.Where(value => value.SteamId.ToString() == query ||
                value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                context.Reply(matches.Length == 0
                    ? $"[SurfTimer Admin] No connected player matches '{query}'."
                    : $"[SurfTimer Admin] Connected player match is ambiguous: {string.Join(", ", matches.Select(value => value.Name))}");
                return;
            }
            current = matches[0];
        }
        if (current is null) { context.Reply("[SurfTimer Admin] Player session is unavailable."); return; }
        var invalidation = current.Run.LastInvalidation;
        context.Reply(invalidation is null
            ? $"[SurfTimer Admin] player=\"{current.Name}\" validation=clean state={current.Run.State}"
            : $"[SurfTimer Admin] player=\"{current.Name}\" validation=clean state={current.Run.State} last={invalidation.Reason} details=\"{invalidation.Details}\" at={invalidation.OccurredAt:u}");
    }

    private void OnRecordCheck(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        if (!TryRank(context, out var rank, "strecordcheck")) return;
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer Admin] No map is active."); return; }
        _ = RecordCheckAsync(context.Sender.PlayerID, context.Sender.SessionId, map, rank);
    }

    private async Task RecordCheckAsync(int playerId, ulong sessionId, string map, int rank)
    {
        try
        {
            var value = await records.GetRecordValidationDetailsAsync(map, rank).ConfigureAwait(false);
            if (value is null) { Reply(playerId, sessionId, $"[SurfTimer Admin] No record exists at rank #{rank} on {map}."); return; }
            Reply(playerId, sessionId,
                $"[SurfTimer Admin] #{rank} {value.PlayerName} {TimerManager.FormatTime(value.TimeMicroseconds)} | validation v{value.ValidationVersion} | flags={value.Flags} | max={value.MaximumSpeed:F1} u/s | overspeed={value.OverspeedSamples} | max-step={value.MaximumFrameDistance:F1} | jumps={value.PositionJumpCount}");
        }
        catch (Exception exception) { Fail(playerId, sessionId, exception, "inspect record telemetry"); }
    }

    private void OnMapReload(ICommandContext context)
    {
        try
        {
            maps.ReloadConfiguration();
            context.Reply($"[SurfTimer Admin] Reloaded {maps.Current?.Name}; validation={maps.Validation.Summary}.");
            Audit(context, "map.reload", maps.Current?.Name ?? "none", maps.Validation.Summary);
        }
        catch (Exception exception) { Fail(context, exception, "reload map configuration"); }
    }

    private void OnMapCheck(ICommandContext context)
    {
        var report = maps.Compatibility;
        context.Reply($"[SurfTimer Admin] {report.MapName}: {report.Summary}");
        foreach (var finding in report.Findings.Where(value => value.Severity != MapCompatibilitySeverity.Info))
            context.Reply($"[SurfTimer Admin] {finding.Severity}: {finding.Message}");
        if (report.Errors == 0 && report.Warnings == 0)
            context.Reply("[SurfTimer Admin] All configured mapper triggers are available.");
    }

    private void OnCatalogCheck(ICommandContext context)
    {
        var results = maps.AuditCatalog();
        var incompatible = results.Where(value => !value.Report.IsCompatible).ToArray();
        context.Reply($"[SurfTimer Admin] Catalog: {results.Count} maps, {incompatible.Length} incompatible; inactive maps require runtime checking after load.");
        foreach (var result in incompatible)
            context.Reply($"[SurfTimer Admin] {result.MapName}: {result.Report.Summary}");
    }

    private void OnSetTier(ICommandContext context)
    {
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out var tier) || tier is < 1 or > 7)
        { context.Reply("[SurfTimer Admin] Usage: !stsettier <1-7>"); return; }
        try
        {
            var previous = maps.Current?.Configuration.Tier ?? 0;
            maps.UpdateConfiguration(value => value with { Tier = tier });
            context.Reply($"[SurfTimer Admin] {maps.Current!.Name} tier changed from {previous} to {tier}.");
            Audit(context, "map.set-tier", maps.Current.Name, $"old={previous};new={tier}");
        }
        catch (Exception exception) { Fail(context, exception, "change map tier"); }
    }

    private void OnMapEnable(ICommandContext context)
    {
        if (context.Args.Length != 1 || !TryOnOff(context.Args[0], out var enabled))
        { context.Reply("[SurfTimer Admin] Usage: !stmapenable <on|off>"); return; }
        try
        {
            var previous = maps.Current?.Configuration.Enabled ?? false;
            maps.UpdateConfiguration(value => value with { Enabled = enabled });
            context.Reply($"[SurfTimer Admin] {maps.Current!.Name} is now {(enabled ? "enabled" : "disabled")}.");
            Audit(context, "map.set-enabled", maps.Current.Name, $"old={previous};new={enabled}");
        }
        catch (Exception exception) { Fail(context, exception, "change map enabled status"); }
    }

    private void OnPlayer(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        if (context.Args.Length == 0) { context.Reply("[SurfTimer Admin] Usage: !stplayer <name|SteamID64>"); return; }
        _ = InspectPlayerAsync(context.Sender.PlayerID, context.Sender.SessionId, string.Join(' ', context.Args));
    }

    private async Task InspectPlayerAsync(int playerId, ulong sessionId, string query)
    {
        try
        {
            var target = await ResolvePlayerAsync(query, playerId, sessionId).ConfigureAwait(false);
            if (target is null) return;
            var details = await records.GetAdminPlayerDetailsAsync(target.SteamId).ConfigureAwait(false);
            if (details is null) { Reply(playerId, sessionId, "[SurfTimer Admin] Player record disappeared."); return; }
            Reply(playerId, sessionId,
                $"[SurfTimer Admin] {details.PlayerName} | SteamID {details.SteamId} | connections {details.Connections} | records {details.Records} | last seen {details.LastSeen:u}");
        }
        catch (Exception exception) { Fail(playerId, sessionId, exception, "inspect player"); }
    }

    private void OnDeletePb(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        if (context.Args.Length < 2 || !context.Args[^1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
        {
            context.Reply("[SurfTimer Admin] Destructive command. Usage: !stdeletepb <name|SteamID64> confirm");
            return;
        }
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer Admin] No map is active."); return; }
        var query = string.Join(' ', context.Args[..^1]);
        _ = DeletePbAsync(context.Sender.PlayerID, context.Sender.SessionId,
            context.Sender.SteamID, context.Sender.Name, query, map);
    }

    private async Task DeletePbAsync(
        int playerId, ulong sessionId, ulong actorSteamId, string actorName, string query, string map)
    {
        try
        {
            var target = await ResolvePlayerAsync(query, playerId, sessionId).ConfigureAwait(false);
            if (target is null) return;
            var deleted = await records.DeletePersonalBestAsync(target.SteamId, map).ConfigureAwait(false);
            if (deleted is null)
            { Reply(playerId, sessionId, $"[SurfTimer Admin] {target.PlayerName} has no PB on {map}."); return; }
            await records.AppendAdminAuditAsync(actorSteamId, actorName, "record.delete-pb",
                $"{deleted.SteamId}:{deleted.MapName}",
                $"player={deleted.PlayerName};time_us={deleted.TimeMicroseconds};completions={deleted.Completions}").ConfigureAwait(false);
            Reply(playerId, sessionId,
                $"[SurfTimer Admin] Deleted {deleted.PlayerName}'s {TimerManager.FormatTime(deleted.TimeMicroseconds)} PB on {map} (including replay and splits). This cannot be undone.");
        }
        catch (Exception exception) { Fail(playerId, sessionId, exception, "delete PB"); }
    }

    private void OnReplayInfo(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        if (!TryRank(context, out var rank)) return;
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer Admin] No map is active."); return; }
        _ = ReplayInfoAsync(context.Sender.PlayerID, context.Sender.SessionId, map, rank);
    }

    private async Task ReplayInfoAsync(int playerId, ulong sessionId, string map, int rank)
    {
        try
        {
            var replay = await records.GetReplayAdminDetailsAsync(map, rank).ConfigureAwait(false);
            if (replay is null) { Reply(playerId, sessionId, $"[SurfTimer Admin] No replay exists at rank #{rank} on {map}."); return; }
            Reply(playerId, sessionId, $"[SurfTimer Admin] #{rank} {replay.PlayerName} {TimerManager.FormatTime(replay.TimeMicroseconds)} | format {replay.FormatVersion} | {replay.SampleRateHz} Hz | {replay.FrameCount} ticks | {TimerManager.FormatTime(replay.DurationMicroseconds)} | {replay.CompressedBytes / 1024.0:F1} KiB");
        }
        catch (Exception exception) { Fail(playerId, sessionId, exception, "inspect replay"); }
    }

    private void OnDeleteReplay(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("[SurfTimer Admin] This command requires a player caller."); return; }
        if (context.Args.Length != 2 || !context.Args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(context.Args[0], out var rank) || rank is < 1 or > 10)
        {
            context.Reply("[SurfTimer Admin] Destructive command. Usage: !stdeletereplay <1-10> confirm (PB is preserved)");
            return;
        }
        var map = maps.Current?.Name;
        if (string.IsNullOrWhiteSpace(map)) { context.Reply("[SurfTimer Admin] No map is active."); return; }
        _ = DeleteReplayAsync(context.Sender.PlayerID, context.Sender.SessionId, context.Sender.SteamID, context.Sender.Name, map, rank);
    }

    private void OnStageReplayInfo(ICommandContext context)
    {
        if(context.Sender is null) return;
        if(context.Args.Length!=2 || !int.TryParse(context.Args[0],out var stage) || !int.TryParse(context.Args[1],out var rank) || stage<1 || stage>maps.StageCount || rank is <1 or >10)
        { context.Reply($"[SurfTimer Admin] Usage: !ststagereplayinfo <1-{maps.StageCount}> <1-10>"); return; }
        var map=maps.Current?.Name; if(string.IsNullOrWhiteSpace(map)) return;
        _=StageReplayInfoAsync(context.Sender.PlayerID,context.Sender.SessionId,map,stage,rank);
    }

    private async Task StageReplayInfoAsync(int playerId,ulong sessionId,string map,int stage,int rank)
    {
        try { var replay=await records.GetStageReplayAdminDetailsAsync(map,stage,rank).ConfigureAwait(false);
            Reply(playerId,sessionId,replay is null?$"[SurfTimer Admin] No Stage {stage} replay exists at rank #{rank}."
                :$"[SurfTimer Admin] Stage {stage} #{rank} {replay.PlayerName} {TimerManager.FormatTime(replay.TimeMicroseconds)} | format {replay.FormatVersion} | {replay.SampleRateHz} Hz | {replay.FrameCount} frames | {replay.CompressedBytes/1024.0:F1} KiB"); }
        catch(Exception e){Fail(playerId,sessionId,e,"inspect stage replay");}
    }

    private void OnDeleteStageReplay(ICommandContext context)
    {
        if(context.Sender is null) return;
        if(context.Args.Length!=3 || !int.TryParse(context.Args[0],out var stage) || !int.TryParse(context.Args[1],out var rank) || !context.Args[2].Equals("confirm",StringComparison.OrdinalIgnoreCase) || stage<1 || stage>maps.StageCount || rank is <1 or >10)
        { context.Reply($"[SurfTimer Admin] Usage: !stdeletestagereplay <1-{maps.StageCount}> <1-10> confirm"); return; }
        var map=maps.Current?.Name; if(string.IsNullOrWhiteSpace(map)) return;
        _=DeleteStageReplayAsync(context.Sender.PlayerID,context.Sender.SessionId,context.Sender.SteamID,context.Sender.Name,map,stage,rank);
    }

    private async Task DeleteStageReplayAsync(int playerId,ulong sessionId,ulong actorSteamId,string actorName,string map,int stage,int rank)
    {
        try { var replay=await records.DeleteStageReplayAsync(map,stage,rank).ConfigureAwait(false);
            if(replay is null){Reply(playerId,sessionId,$"[SurfTimer Admin] No Stage {stage} replay exists at rank #{rank}.");return;}
            await records.AppendAdminAuditAsync(actorSteamId,actorName,"stage-replay.delete",$"{replay.SteamId}:{map}:stage-{stage}",$"rank={rank};time_us={replay.TimeMicroseconds}").ConfigureAwait(false);
            playback.InvalidateSelection(); Reply(playerId,sessionId,$"[SurfTimer Admin] Deleted Stage {stage} #{rank} {replay.PlayerName}'s replay; their PB was preserved."); }
        catch(Exception e){Fail(playerId,sessionId,e,"delete stage replay");}
    }

    private void OnBonusReplayInfo(ICommandContext context)
    {
        if(context.Sender is null)return;
        if(context.Args.Length!=2||!int.TryParse(context.Args[0],out var bonus)||!int.TryParse(context.Args[1],out var rank)||bonus<1||bonus>maps.BonusCount||rank is <1 or >10)
        {context.Reply($"[SurfTimer Admin] Usage: !stbonusreplayinfo <1-{maps.BonusCount}> <1-10>");return;}
        var map=maps.Current?.Name;if(string.IsNullOrWhiteSpace(map))return;
        _=BonusReplayInfoAsync(context.Sender.PlayerID,context.Sender.SessionId,map,bonus,rank);
    }

    private async Task BonusReplayInfoAsync(int playerId,ulong sessionId,string map,int bonus,int rank)
    {
        try{var replay=await records.GetBonusReplayAdminDetailsAsync(map,bonus,rank).ConfigureAwait(false);
            Reply(playerId,sessionId,replay is null?$"[SurfTimer Admin] No Bonus {bonus} replay exists at rank #{rank}."
                :$"[SurfTimer Admin] Bonus {bonus} #{rank} {replay.PlayerName} {TimerManager.FormatTime(replay.TimeMicroseconds)} | format {replay.FormatVersion} | {replay.SampleRateHz} Hz | {replay.FrameCount} frames | {replay.CompressedBytes/1024.0:F1} KiB");}
        catch(Exception e){Fail(playerId,sessionId,e,"inspect bonus replay");}
    }

    private void OnDeleteBonusReplay(ICommandContext context)
    {
        if(context.Sender is null)return;
        if(context.Args.Length!=3||!int.TryParse(context.Args[0],out var bonus)||!int.TryParse(context.Args[1],out var rank)||!context.Args[2].Equals("confirm",StringComparison.OrdinalIgnoreCase)||bonus<1||bonus>maps.BonusCount||rank is <1 or >10)
        {context.Reply($"[SurfTimer Admin] Usage: !stdeletebonusreplay <1-{maps.BonusCount}> <1-10> confirm");return;}
        var map=maps.Current?.Name;if(string.IsNullOrWhiteSpace(map))return;
        _=DeleteBonusReplayAsync(context.Sender.PlayerID,context.Sender.SessionId,context.Sender.SteamID,context.Sender.Name,map,bonus,rank);
    }

    private async Task DeleteBonusReplayAsync(int playerId,ulong sessionId,ulong actorSteamId,string actorName,string map,int bonus,int rank)
    {
        try{var replay=await records.DeleteBonusReplayAsync(map,bonus,rank).ConfigureAwait(false);
            if(replay is null){Reply(playerId,sessionId,$"[SurfTimer Admin] No Bonus {bonus} replay exists at rank #{rank}.");return;}
            await records.AppendAdminAuditAsync(actorSteamId,actorName,"bonus-replay.delete",$"{replay.SteamId}:{map}:bonus-{bonus}",$"rank={rank};time_us={replay.TimeMicroseconds}").ConfigureAwait(false);
            playback.InvalidateSelection();Reply(playerId,sessionId,$"[SurfTimer Admin] Deleted Bonus {bonus} #{rank} {replay.PlayerName}'s replay; their PB was preserved.");}
        catch(Exception e){Fail(playerId,sessionId,e,"delete bonus replay");}
    }

    private async Task DeleteReplayAsync(int playerId, ulong sessionId, ulong actorSteamId, string actorName, string map, int rank)
    {
        try
        {
            var deleted = await records.DeleteReplayAsync(map, rank).ConfigureAwait(false);
            if (deleted is null) { Reply(playerId, sessionId, $"[SurfTimer Admin] No replay exists at rank #{rank} on {map}."); return; }
            await records.AppendAdminAuditAsync(actorSteamId, actorName, "replay.delete",
                $"{deleted.SteamId}:{deleted.MapName}", $"rank={rank};player={deleted.PlayerName};time_us={deleted.TimeMicroseconds};bytes={deleted.CompressedBytes}").ConfigureAwait(false);
            playback.InvalidateSelection();
            Reply(playerId, sessionId, $"[SurfTimer Admin] Deleted #{rank} {deleted.PlayerName}'s replay on {map}. Their PB {TimerManager.FormatTime(deleted.TimeMicroseconds)} was preserved.");
        }
        catch (Exception exception) { Fail(playerId, sessionId, exception, "delete replay"); }
    }

    private static bool TryRank(ICommandContext context, out int rank, string command = "streplayinfo")
    {
        if (context.Args.Length == 1 && int.TryParse(context.Args[0], out rank) && rank is >= 1 and <= 10) return true;
        rank = 0;
        context.Reply($"[SurfTimer Admin] Usage: !{command} <1-10>");
        return false;
    }

    private async Task<PlayerIdentity?> ResolvePlayerAsync(string query, int playerId, ulong sessionId)
    {
        var matches = await records.FindPlayersAsync(query).ConfigureAwait(false);
        if (matches.Count == 0) { Reply(playerId, sessionId, $"[SurfTimer Admin] No player matches '{query}'."); return null; }
        var exact = matches.FirstOrDefault(value => value.PlayerName.Equals(query, StringComparison.OrdinalIgnoreCase) || value.SteamId.ToString() == query);
        if (exact is not null) return exact;
        if (matches.Count == 1) return matches[0];
        Reply(playerId, sessionId, $"[SurfTimer Admin] Ambiguous player: {string.Join(", ", matches.Select(value => value.PlayerName))}");
        return null;
    }

    private void Audit(ICommandContext context, string action, string target, string details)
    {
        if (context.Sender is null) return;
        _ = records.AppendAdminAuditAsync(context.Sender.SteamID, context.Sender.Name, action, target, details);
    }

    private void Fail(ICommandContext context, Exception exception, string operation)
    {
        logger.LogError(exception, "Failed to {Operation}.", operation);
        context.Reply($"[SurfTimer Admin] Failed to {operation}: {exception.Message}");
    }

    private void Fail(int playerId, ulong sessionId, Exception exception, string operation)
    {
        logger.LogError(exception, "Failed to {Operation}.", operation);
        Reply(playerId, sessionId, $"[SurfTimer Admin] Failed to {operation}.");
    }

    private void Reply(int playerId, ulong sessionId, string message) => core.Scheduler.NextTick(() =>
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is not null && player.SessionId == sessionId) player.SendChat(message);
    });

    private static bool TryOnOff(string value, out bool enabled)
    {
        enabled = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        return enabled || value.Equals("off", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
