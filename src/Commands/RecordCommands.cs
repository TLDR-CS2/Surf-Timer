using Microsoft.Extensions.Logging;
using SurfTimer.Chat;
using SurfTimer.Maps;
using SurfTimer.Storage;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class RecordCommands(
    ISwiftlyCore core,
    MapLifecycle maps,
    RecordRepository records,
    ILogger<RecordCommands> logger)
{
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0) return;
        _registrations.Add(core.Command.RegisterCommand(
            "pb", OnPb, registerRaw: false, helpText: "Shows your PB on the current map."));
        _registrations.Add(core.Command.RegisterCommand(
            "top10", OnTop10, registerRaw: false, helpText: "Shows the global top ten for the current map."));
        _registrations.Add(core.Command.RegisterCommand(
            "wr", OnWr, registerRaw: false, helpText: "Shows the world record for the current map."));
        _registrations.Add(core.Command.RegisterCommand(
            "rank", OnRank, registerRaw: false, helpText: "Shows your global rank on the current map."));
        _registrations.Add(core.Command.RegisterCommand(
            "stagepb", OnStagePb, registerRaw: false, helpText: "Shows your stage PB: !stagepb <stage>."));
        _registrations.Add(core.Command.RegisterCommand(
            "stagetop", OnStageTop, registerRaw: false, helpText: "Shows the global stage top ten: !stagetop <stage>."));
        _registrations.Add(core.Command.RegisterCommand(
            "stagewr", OnStageWr, registerRaw: false, helpText: "Shows the stage world record: !stagewr <stage>."));
        _registrations.Add(core.Command.RegisterCommand(
            "bonuspb", OnBonusPb, registerRaw: false, helpText: "Shows your bonus PB: !bonuspb <bonus>."));
        _registrations.Add(core.Command.RegisterCommand(
            "bonustop", OnBonusTop, registerRaw: false, helpText: "Shows the global bonus top ten: !bonustop <bonus>."));
        _registrations.Add(core.Command.RegisterCommand(
            "bonuswr", OnBonusWr, registerRaw: false, helpText: "Shows the bonus world record: !bonuswr <bonus>."));
        core.Command.RegisterCommandAlias("sw_top10", "top", false);
        core.Command.RegisterCommandAlias("sw_pb", "css_pb", true);
        core.Command.RegisterCommandAlias("sw_top10", "css_top10", true);
        core.Command.RegisterCommandAlias("sw_wr", "css_wr", true);
        core.Command.RegisterCommandAlias("sw_rank", "css_rank", true);
        core.Command.RegisterCommandAlias("sw_stagepb", "css_stagepb", true);
        core.Command.RegisterCommandAlias("sw_stagetop", "css_stagetop", true);
        core.Command.RegisterCommandAlias("sw_stagewr", "css_stagewr", true);
        core.Command.RegisterCommandAlias("sw_bonuspb", "bpb", false);
        core.Command.RegisterCommandAlias("sw_bonustop", "btop", false);
        core.Command.RegisterCommandAlias("sw_bonuswr", "bwr", false);
        core.Command.RegisterCommandAlias("sw_bonuspb", "css_bonuspb", true);
        core.Command.RegisterCommandAlias("sw_bonustop", "css_bonustop", true);
        core.Command.RegisterCommandAlias("sw_bonuswr", "css_bonuswr", true);
    }

    public void Unregister()
    {
        core.Command.UnregisterCommand("sw_top");
        core.Command.UnregisterCommand("css_pb");
        core.Command.UnregisterCommand("css_top10");
        core.Command.UnregisterCommand("css_wr");
        core.Command.UnregisterCommand("css_rank");
        core.Command.UnregisterCommand("css_stagepb");
        core.Command.UnregisterCommand("css_stagetop");
        core.Command.UnregisterCommand("css_stagewr");
        foreach (var name in new[] { "sw_bpb", "sw_btop", "sw_bwr", "css_bonuspb", "css_bonustop", "css_bonuswr" })
            core.Command.UnregisterCommand(name);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    private void OnPb(ICommandContext context)
    {
        if (!TryCapturePlayer(context, out var playerId, out var sessionId, out var steamId, out var map)) return;
        _ = ResolveAndShowPbAsync(context.Args, playerId, sessionId, steamId, context.Sender!.Name, map);
    }

    private void OnTop10(ICommandContext context)
    {
        if (!TryCapturePlayer(context, out var playerId, out var sessionId, out _, out var map)) return;
        _ = ShowTopAsync(playerId, sessionId, steamId: context.Sender!.SteamID, map);
    }

    private void OnWr(ICommandContext context)
    {
        if (!TryCapturePlayer(context, out var playerId, out var sessionId, out _, out var map)) return;
        _ = ShowWrAsync(playerId, sessionId, map);
    }

    private void OnRank(ICommandContext context)
    {
        if (!TryCapturePlayer(context, out var playerId, out var sessionId, out var steamId, out var map)) return;
        _ = ResolveAndShowRankAsync(context.Args, playerId, sessionId, steamId, context.Sender!.Name, map);
    }

    private void OnStagePb(ICommandContext context)
    {
        if (!TryCaptureStage(context, out var playerId, out var sessionId, out var steamId, out var map, out var stage)) return;
        _ = ShowStagePbAsync(playerId, sessionId, steamId, map, stage);
    }

    private void OnStageTop(ICommandContext context)
    {
        if (!TryCaptureStage(context, out var playerId, out var sessionId, out var steamId, out var map, out var stage)) return;
        _ = ShowStageTopAsync(playerId, sessionId, steamId, map, stage);
    }

    private void OnStageWr(ICommandContext context)
    {
        if (!TryCaptureStage(context, out var playerId, out var sessionId, out _, out var map, out var stage)) return;
        _ = ShowStageWrAsync(playerId, sessionId, map, stage);
    }

    private void OnBonusPb(ICommandContext context)
    {
        if (!TryCaptureBonus(context, out var playerId, out var sessionId, out var steamId, out var map, out var bonus)) return;
        _ = ShowBonusPbAsync(playerId, sessionId, steamId, map, bonus);
    }

    private void OnBonusTop(ICommandContext context)
    {
        if (!TryCaptureBonus(context, out var playerId, out var sessionId, out var steamId, out var map, out var bonus)) return;
        _ = ShowBonusTopAsync(playerId, sessionId, steamId, map, bonus);
    }

    private void OnBonusWr(ICommandContext context)
    {
        if (!TryCaptureBonus(context, out var playerId, out var sessionId, out _, out var map, out var bonus)) return;
        _ = ShowBonusWrAsync(playerId, sessionId, map, bonus);
    }

    private bool TryCaptureBonus(ICommandContext context, out int playerId, out ulong sessionId, out ulong steamId, out string map, out int bonus)
    {
        bonus = 0;
        if (!TryCapturePlayer(context, out playerId, out sessionId, out steamId, out map)) return false;
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out bonus) || bonus < 1 || bonus > maps.BonusCount)
        { context.Reply(ChatFormat.Warning($"Usage: {context.CommandName} <1-{maps.BonusCount}>")); return false; }
        return true;
    }

    private async Task ShowBonusPbAsync(int playerId, ulong sessionId, ulong steamId, string map, int bonus)
    {
        try
        {
            var pb = await records.GetBonusPersonalBestAsync(steamId, map, bonus).ConfigureAwait(false);
            if (pb is null) { ReplyNextTick(playerId, sessionId, ChatFormat.Message($"No Bonus {bonus} PB on {map} yet.")); return; }
            var top = await records.GetBonusTopAsync(map, bonus, 1).ConfigureAwait(false);
            var delta = top.Count > 0 && pb.Rank != 1 ? $" {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.ErrorColor}WR +{FormatDelta(pb.TimeMicroseconds - top[0].TimeMicroseconds)}{ChatFormat.Reset}" : string.Empty;
            ReplyNextTick(playerId, sessionId, $"{ChatFormat.Prefix} {ChatFormat.RouteColor}BONUS {bonus} PB{ChatFormat.Reset} · {ChatFormat.SuccessColor}{TimerManager.FormatTime(pb.TimeMicroseconds)}{ChatFormat.Reset} · {ChatFormat.Rank(pb.Rank)}/{pb.TotalRecords} · {pb.Completions} finishes{delta}");
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowBonusTopAsync(int playerId, ulong sessionId, ulong steamId, string map, int bonus)
    {
        try
        {
            var top = await records.GetBonusTopAsync(map, bonus).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                player.SendChat(ChatFormat.Header($"Bonus {bonus} Top 10 · {map}"));
                if (top.Count == 0) player.SendChat(ChatFormat.Row("", "No bonus records yet.", ChatFormat.MutedColor));
                foreach (var entry in top)
                    player.SendChat($"{ChatFormat.Rank(entry.Rank)} {entry.PlayerName}{(entry.SteamId == steamId ? $" {ChatFormat.SuccessColor}YOU{ChatFormat.Reset}" : string.Empty)} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.RouteColor}{TimerManager.FormatTime(entry.TimeMicroseconds)}{ChatFormat.Reset}");
            });
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowBonusWrAsync(int playerId, ulong sessionId, string map, int bonus)
    {
        try
        {
            var top = await records.GetBonusTopAsync(map, bonus, 1).ConfigureAwait(false);
            if (top.Count == 0) { ReplyNextTick(playerId, sessionId, ChatFormat.Message($"Bonus {bonus} has no world record yet.")); return; }
            var wr = top[0];
            ReplyNextTick(playerId, sessionId, $"{ChatFormat.Prefix} {ChatFormat.HighlightColor}BONUS {bonus} WR{ChatFormat.Reset} · {wr.PlayerName} · {ChatFormat.HighlightColor}{TimerManager.FormatTime(wr.TimeMicroseconds)}{ChatFormat.Reset}");
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private bool TryCaptureStage(ICommandContext context, out int playerId, out ulong sessionId, out ulong steamId, out string map, out int stage)
    {
        stage = 0;
        if (!TryCapturePlayer(context, out playerId, out sessionId, out steamId, out map)) return false;
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out stage) || stage < 1 || stage > maps.StageCount)
        { context.Reply(ChatFormat.Warning($"Usage: {context.CommandName} <1-{maps.StageCount}>")); return false; }
        return true;
    }

    private async Task ShowStagePbAsync(int playerId, ulong sessionId, ulong steamId, string map, int stage)
    {
        try
        {
            var pb = await records.GetStagePersonalBestAsync(steamId, map, stage).ConfigureAwait(false);
            if (pb is null) { ReplyNextTick(playerId, sessionId, ChatFormat.Message($"No Stage {stage} PB on {map} yet.")); return; }
            var top = await records.GetStageTopAsync(map, stage, 1).ConfigureAwait(false);
            var delta = top.Count > 0 && pb.Rank != 1 ? $" {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.ErrorColor}WR +{FormatDelta(pb.TimeMicroseconds - top[0].TimeMicroseconds)}{ChatFormat.Reset}" : string.Empty;
            ReplyNextTick(playerId, sessionId, $"{ChatFormat.Prefix} {ChatFormat.RouteColor}STAGE {stage} PB{ChatFormat.Reset} · {ChatFormat.SuccessColor}{TimerManager.FormatTime(pb.TimeMicroseconds)}{ChatFormat.Reset} · {ChatFormat.Rank(pb.Rank)}/{pb.TotalRecords} · {pb.Completions} finishes{delta}");
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowStageTopAsync(int playerId, ulong sessionId, ulong steamId, string map, int stage)
    {
        try
        {
            var top = await records.GetStageTopAsync(map, stage).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                player.SendChat(ChatFormat.Header($"Stage {stage} Top 10 · {map}"));
                if (top.Count == 0) player.SendChat(ChatFormat.Row("", "No stage records yet.", ChatFormat.MutedColor));
                foreach (var entry in top)
                    player.SendChat($"{ChatFormat.Rank(entry.Rank)} {entry.PlayerName}{(entry.SteamId == steamId ? $" {ChatFormat.SuccessColor}YOU{ChatFormat.Reset}" : string.Empty)} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.RouteColor}{TimerManager.FormatTime(entry.TimeMicroseconds)}{ChatFormat.Reset}");
            });
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowStageWrAsync(int playerId, ulong sessionId, string map, int stage)
    {
        try
        {
            var top = await records.GetStageTopAsync(map, stage, 1).ConfigureAwait(false);
            if (top.Count == 0) { ReplyNextTick(playerId, sessionId, ChatFormat.Message($"Stage {stage} has no world record yet.")); return; }
            var wr = top[0];
            ReplyNextTick(playerId, sessionId, $"{ChatFormat.Prefix} {ChatFormat.HighlightColor}STAGE {stage} WR{ChatFormat.Reset} · {wr.PlayerName} · {ChatFormat.HighlightColor}{TimerManager.FormatTime(wr.TimeMicroseconds)}{ChatFormat.Reset}");
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private bool TryCapturePlayer(ICommandContext context, out int playerId, out ulong sessionId, out ulong steamId, out string map)
    {
        playerId = -1; sessionId = 0; steamId = 0; map = maps.Current?.Name ?? string.Empty;
        if (!context.IsSentByPlayer || context.Sender is null) { context.Reply("This command requires a player caller."); return false; }
        if (string.IsNullOrWhiteSpace(map)) { context.Reply(ChatFormat.Error("No map is active.")); return false; }
        playerId = context.Sender.PlayerID; sessionId = context.Sender.SessionId; steamId = context.Sender.SteamID;
        return true;
    }

    private async Task ResolveAndShowPbAsync(string[] args, int playerId, ulong sessionId, ulong ownSteamId, string ownName, string map)
    {
        var target = await ResolveTargetAsync(args, playerId, sessionId, ownSteamId, ownName).ConfigureAwait(false);
        if (target is not null) await ShowPbAsync(playerId, sessionId, target, map, target.SteamId == ownSteamId).ConfigureAwait(false);
    }

    private async Task ShowPbAsync(int playerId, ulong sessionId, PlayerIdentity target, string map, bool self)
    {
        try
        {
            var pb = await records.GetPersonalBestDetailsAsync(target.SteamId, map).ConfigureAwait(false);
            if (pb is null)
            {
                ReplyNextTick(playerId, sessionId, self
                    ? ChatFormat.Message($"No PB on {map} yet · finish the map to set one.")
                    : ChatFormat.Message($"{target.PlayerName} has no PB on {map}."));
                return;
            }
            var top = await records.GetTopAsync(map, 1).ConfigureAwait(false);
            var points = SurfPointsPolicy.ForMainMap(CurrentTier, pb.Rank, pb.TotalRecords);
            var overall = await records.GetPlayerOverallRankingAsync(target.SteamId).ConfigureAwait(false);
            PersonalBestDetails? wr = top.Count == 0 ? null :
                await records.GetPersonalBestDetailsAsync(top[0].SteamId, map).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                var wrDelta = top.Count > 0 && pb.Rank != 1 ? $" · {ChatFormat.ErrorColor}WR +{FormatDelta(pb.TimeMicroseconds - top[0].TimeMicroseconds)}{ChatFormat.Reset}" : $" · {ChatFormat.HighlightColor}WR{ChatFormat.Reset}";
                var owner = self ? "PB" : $"{target.PlayerName}'s PB";
                var group = points.Group is null ? "No group" : points.Group;
                player.SendChat(ChatFormat.Header($"{owner} · {map}"));
                player.SendChat($"{ChatFormat.SuccessColor}{TimerManager.FormatTime(pb.TimeMicroseconds)}{ChatFormat.Reset} · {ChatFormat.Rank(pb.Rank)}/{pb.TotalRecords} · {points.Percentile:P1} · {group}");
                player.SendChat(ChatFormat.Row("POINTS ·", $"Map {points.Points:N0} · Global {overall?.Points ?? 0:N0} · {pb.Completions} finishes{wrDelta}"));
                SendSplits(player, pb.Splits, wr?.Splits);
            });
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowTopAsync(int playerId, ulong sessionId, ulong steamId, string map)
    {
        try
        {
            var top = await records.GetTopAsync(map).ConfigureAwait(false);
            var summary = await records.GetMapSummaryAsync(map).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                player.SendChat(ChatFormat.Header($"Top 10 · {map} · Tier {CurrentTier}"));
                if (top.Count == 0) player.SendChat(ChatFormat.Row("", "No records yet.", ChatFormat.MutedColor));
                foreach (var entry in top)
                {
                    var group = SurfPointsPolicy.ForMainMap(CurrentTier, entry.Rank, summary.RecordCount).Group;
                    player.SendChat($"{ChatFormat.Rank(entry.Rank)} {entry.PlayerName}{(entry.SteamId == steamId ? $" {ChatFormat.SuccessColor}YOU{ChatFormat.Reset}" : string.Empty)} {ChatFormat.MutedColor}·{ChatFormat.Reset} {ChatFormat.SuccessColor}{TimerManager.FormatTime(entry.TimeMicroseconds)}{ChatFormat.Reset}{(group is null ? string.Empty : $" · {group}")} {ChatFormat.MutedColor}· {entry.Completions} finishes{ChatFormat.Reset}");
                }
            });
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ShowWrAsync(int playerId, ulong sessionId, string map)
    {
        try
        {
            var top = await records.GetTopAsync(map, 1).ConfigureAwait(false);
            if (top.Count == 0)
            {
                ReplyNextTick(playerId, sessionId, ChatFormat.Message($"{map} has no world record yet."));
                return;
            }
            var wr = top[0];
            var details = await records.GetPersonalBestDetailsAsync(wr.SteamId, map).ConfigureAwait(false);
            core.Scheduler.NextTick(() =>
            {
                var player = core.PlayerManager.GetPlayer(playerId);
                if (player is null || player.SessionId != sessionId) return;
                player.SendChat(ChatFormat.Header($"World Record · {map}"));
                player.SendChat($"{ChatFormat.HighlightColor}{TimerManager.FormatTime(wr.TimeMicroseconds)}{ChatFormat.Reset} · {wr.PlayerName} {ChatFormat.MutedColor}· {wr.Completions} finishes{ChatFormat.Reset}");
                if (details is not null) SendSplits(player, details.Splits, null);
            });
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private async Task ResolveAndShowRankAsync(string[] args, int playerId, ulong sessionId, ulong ownSteamId, string ownName, string map)
    {
        var target = await ResolveTargetAsync(args, playerId, sessionId, ownSteamId, ownName).ConfigureAwait(false);
        if (target is not null) await ShowRankAsync(playerId, sessionId, target, map, target.SteamId == ownSteamId).ConfigureAwait(false);
    }

    private async Task ShowRankAsync(int playerId, ulong sessionId, PlayerIdentity target, string map, bool self)
    {
        try
        {
            var pb = await records.GetPersonalBestDetailsAsync(target.SteamId, map).ConfigureAwait(false);
            if (pb is null)
            {
                ReplyNextTick(playerId, sessionId, self
                    ? ChatFormat.Message($"You are unranked on {map}.")
                    : ChatFormat.Message($"{target.PlayerName} is unranked on {map}."));
                return;
            }
            var top = await records.GetTopAsync(map, 1).ConfigureAwait(false);
            var behind = top.Count > 0 && pb.Rank != 1
                ? $" · {ChatFormat.ErrorColor}+{FormatDelta(pb.TimeMicroseconds - top[0].TimeMicroseconds)} behind WR{ChatFormat.Reset}"
                : $" · {ChatFormat.HighlightColor}world record{ChatFormat.Reset}";
            var owner = self ? "RANK" : target.PlayerName;
            ReplyNextTick(playerId, sessionId,
                $"{ChatFormat.Prefix} {owner} · {ChatFormat.Rank(pb.Rank)}/{pb.TotalRecords} · {ChatFormat.SuccessColor}{TimerManager.FormatTime(pb.TimeMicroseconds)}{ChatFormat.Reset}{behind} {ChatFormat.MutedColor}· {pb.Completions} finishes{ChatFormat.Reset}");
        }
        catch (Exception ex) { LogAndReply(ex, playerId, sessionId); }
    }

    private static void SendSplits(SwiftlyS2.Shared.Players.IPlayer player, IReadOnlyList<RecordSplit> splits, IReadOnlyList<RecordSplit>? comparison)
    {
        long previous = 0;
        foreach (var split in splits)
        {
            var segment = split.TimeMicroseconds - previous;
            previous = split.TimeMicroseconds;
            var reference = comparison?.FirstOrDefault(item => item.Checkpoint == split.Checkpoint);
            var slower = reference is not null && split.TimeMicroseconds >= reference.TimeMicroseconds;
            var delta = reference is null ? string.Empty : $" · {(slower ? ChatFormat.ErrorColor : ChatFormat.SuccessColor)}WR {(slower ? "+" : "-")}{FormatDelta(Math.Abs(split.TimeMicroseconds - reference.TimeMicroseconds))}{ChatFormat.Reset}";
            player.SendChat($"{ChatFormat.MutedColor}CP{split.Checkpoint} ·{ChatFormat.Reset} {TimerManager.FormatTime(split.TimeMicroseconds)} {ChatFormat.MutedColor}· segment {TimerManager.FormatTime(segment)}{ChatFormat.Reset}{delta}");
        }
    }

    private static string FormatDelta(long microseconds) => TimerManager.FormatTime(Math.Max(0, microseconds));

    private async Task<PlayerIdentity?> ResolveTargetAsync(
        string[] args, int playerId, ulong sessionId, ulong ownSteamId, string ownName)
    {
        if (args.Length == 0) return new PlayerIdentity(ownSteamId, ownName);
        var query = string.Join(' ', args).Trim();
        if (query.Length == 0) return new PlayerIdentity(ownSteamId, ownName);
        try
        {
            var matches = await records.FindPlayersAsync(query).ConfigureAwait(false);
            if (matches.Count == 0)
            {
                ReplyNextTick(playerId, sessionId, ChatFormat.Error($"No player found matching '{query}'."));
                return null;
            }
            var exact = matches.FirstOrDefault(match =>
                match.PlayerName.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                match.SteamId.ToString() == query);
            if (exact is not null) return exact;
            if (matches.Count == 1) return matches[0];
            ReplyNextTick(playerId, sessionId,
                ChatFormat.Warning($"Multiple players match '{query}': {string.Join(", ", matches.Select(match => match.PlayerName))}"));
            return null;
        }
        catch (Exception exception)
        {
            LogAndReply(exception, playerId, sessionId);
            return null;
        }
    }

    private int CurrentTier => maps.Current?.Configuration.Tier ?? 1;

    private void LogAndReply(Exception exception, int playerId, ulong sessionId)
    {
        logger.LogError(exception, "Record command failed.");
        ReplyNextTick(playerId, sessionId, ChatFormat.Error("The records database is unavailable."));
    }

    private void ReplyNextTick(int playerId, ulong sessionId, string message) => core.Scheduler.NextTick(() =>
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is not null && player.SessionId == sessionId) player.SendChat(message);
    });
}
