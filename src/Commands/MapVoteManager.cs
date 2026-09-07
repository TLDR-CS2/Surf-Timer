using Microsoft.Extensions.Logging;
using SurfTimer.Chat;
using SurfTimer.Configuration;
using SurfTimer.Maps;
using SurfTimer.Players;
using System.Text.Json;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;

namespace SurfTimer.Commands;

public sealed class MapVoteManager(
    ISwiftlyCore core,
    SurfTimerOptions options,
    MapLifecycle maps,
    MapConfigurationProvider mapConfigurations,
    SurfPlayerManager players,
    ILogger<MapVoteManager> logger)
{
    private const int ExtendMapChoice = 6;
    private const int ExtendMapBallot = ExtendMapChoice - 1;
    private readonly List<Guid> _registrations = [];
    private readonly HashSet<ulong> _rtv = [];
    private readonly Dictionary<ulong, string> _nominations = [];
    private readonly Dictionary<ulong, int> _ballots = [];
    private readonly Queue<string> _recentMaps = [];
    private readonly Dictionary<string, int> _compatibilityFailures = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MapPoolEntry> _candidates = [];
    private CancellationTokenSource? _voteTimer;
    private CancellationTokenSource? _forcedVoteTimer;
    private DateTimeOffset _rtvLockedUntil;
    private DateTimeOffset? _forcedVoteAt;
    private int _mapExtensions;
    private bool _voteWasForced;
    private bool _started;
    private MapPoolEntry? _nextMap;
    private static readonly DateTime ProcessStartedAtUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private string StatePath => Path.Combine(core.PluginDataDirectory, "map-voting-state.json");

    public void Start()
    {
        if (_started || !options.MapVoting.Enabled) return;
        _started = true;
        LoadRecentMaps();
        _registrations.Add(core.Command.RegisterCommand("stquarantine", OnQuarantine, false,
            permission: "surftimer.admin", helpText: "Manage quarantine: !stquarantine [retest|reinstate <map>]."));
        Register("maps", OnMaps, "Lists maps available for voting.");
        Register("nextmap", OnNextMap, "Shows the selected next map.");
        Register("mapvote", OnVote, "Votes in an active map vote: !mapvote <number>.");
        core.Command.RegisterCommandAlias("sw_mapvote", "vote", false);
        for (var choice = 1; choice <= 10; choice++)
        {
            var captured = choice;
            Register(choice.ToString(), context => OnNumberVote(context, captured), $"Votes for map option {choice}.");
        }
        if (options.MapVoting.RockTheVoteEnabled)
        {
            Register("rtv", OnRtv, "Votes to start a map vote.");
            core.Command.RegisterCommandAlias("sw_rtv", "rockthevote", false);
        }
        if (options.MapVoting.NominationEnabled)
        {
            Register("nominate", OnNominate, "Nominates a map: !nominate <map>.");
            core.Command.RegisterCommandAlias("sw_nominate", "nom", false);
        }
        core.Event.OnMapLoad += OnMapLoad;
        core.Event.OnMapUnload += OnMapUnload;
        players.Disconnected += OnClientDisconnected;
        maps.CompatibilityEvaluated += OnCompatibilityEvaluated;
        if (maps.Current is not null) ScheduleForcedVote(options.MapVoting.ForceVoteAfterMinutes, _forcedVoteAt);
        logger.LogInformation(
            "SurfTimer map voting enabled; RTV={Rtv}, nominations={Nominations}, forced vote={ForcedVote} after {Minutes} minutes.",
            options.MapVoting.RockTheVoteEnabled, options.MapVoting.NominationEnabled,
            options.MapVoting.EndOfMapVoteEnabled, options.MapVoting.ForceVoteAfterMinutes);
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnMapLoad -= OnMapLoad;
        core.Event.OnMapUnload -= OnMapUnload;
        players.Disconnected -= OnClientDisconnected;
        maps.CompatibilityEvaluated -= OnCompatibilityEvaluated;
        CancelForcedVote();
        CancelVote();
        foreach (var alias in new[] { "sw_vote", "sw_rockthevote", "sw_nom" }) core.Command.UnregisterCommand(alias);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear(); _rtv.Clear(); _nominations.Clear(); _started = false;
    }

    private void Register(string name, ICommandService.CommandListener callback, string help) =>
        _registrations.Add(core.Command.RegisterCommand(name, callback, false, helpText: help));

    private void OnMapLoad(IOnMapLoadEvent e)
    {
        _nextMap = null;
        if (!string.IsNullOrWhiteSpace(e.MapName))
        {
            var retained = _recentMaps.Where(map => !map.Equals(e.MapName, StringComparison.OrdinalIgnoreCase)).ToArray();
            _recentMaps.Clear();
            foreach (var map in retained) _recentMaps.Enqueue(map);
            _recentMaps.Enqueue(e.MapName);
            while (_recentMaps.Count > options.MapVoting.RecentMapExclusionCount) _recentMaps.Dequeue();
            SaveRecentMaps();
        }
        _rtvLockedUntil = default;
        _mapExtensions = 0;
        _rtv.Clear(); _nominations.Clear(); CancelVote(); CancelForcedVote();
        ScheduleForcedVote(options.MapVoting.ForceVoteAfterMinutes);
    }

    private void OnMapUnload(IOnMapUnloadEvent _)
    {
        _rtvLockedUntil = default;
        _rtv.Clear(); _nominations.Clear(); CancelForcedVote(); CancelVote();
        _nextMap = null;
        _forcedVoteAt = null;
        SaveRecentMaps();
    }

    private void OnClientDisconnected(SurfPlayerSession session)
    {
        _rtv.Remove(session.SteamId);
        _nominations.Remove(session.SteamId);
        _ballots.Remove(session.SteamId);
        var generation = maps.Current?.Generation;
        core.Scheduler.NextTick(() =>
        {
            if (!_started || maps.Current?.Generation != generation) return;
            if (_voteTimer is null && _rtv.Count >= RequiredRtvVotes()) StartVote(forced: false);
            else TryFinishVoteWhenEveryoneVoted();
        });
    }
    private void OnQuarantine(ICommandContext context)
    {
        if (context.Args.Length == 0)
        {
            context.Reply(ChatFormat.Header("Map compatibility failures"));
            foreach (var entry in _compatibilityFailures.OrderBy(value => value.Key))
                context.Reply($"{entry.Key}: {entry.Value} failures ({(entry.Value >= options.MapVoting.CompatibilityFailuresBeforeQuarantine ? "quarantined" : "under threshold")})");
            if (_compatibilityFailures.Count == 0) context.Reply("No recorded compatibility failures.");
            return;
        }
        if (context.Args.Length == 1 && context.Args[0].Equals("retest", StringComparison.OrdinalIgnoreCase))
        {
            if (maps.Current is null) { context.Reply("No active map."); return; }
            maps.ReloadConfiguration();
            var report = maps.Compatibility;
            OnCompatibilityEvaluated(report);
            context.Reply($"{report.MapName}: {report.Summary}; {string.Join("; ", report.Findings.Select(value => value.Message))}");
            logger.LogInformation("Administrator {Admin} retested {Map}: {Summary}.", context.Sender?.SteamID ?? 0, report.MapName, report.Summary);
            return;
        }
        if (context.Args.Length == 2 && context.Args[0].Equals("reinstate", StringComparison.OrdinalIgnoreCase))
        {
            var map = options.MapVoting.Maps.FirstOrDefault(value => value.Name.Equals(context.Args[1], StringComparison.OrdinalIgnoreCase));
            if (map is null) { context.Reply("Unknown map; use its exact catalog name."); return; }
            _compatibilityFailures.Remove(map.Name);
            SaveRecentMaps();
            logger.LogInformation("Administrator {Admin} reinstated {Map} in the voting pool.", context.Sender?.SteamID ?? 0, map.Name);
            context.Reply($"Cleared quarantine for {map.Name}; normal map eligibility rules still apply.");
            return;
        }
        context.Reply("Usage: !stquarantine [retest|reinstate <map>]");
    }

    private void OnNextMap(ICommandContext context) => context.Reply(_nextMap is null
        ? ChatFormat.Message("Next map has not been selected yet.")
        : ChatFormat.Row("NEXT MAP ·", $"{_nextMap.Name} · Tier {mapConfigurations.Load(_nextMap.Name).Value.Tier}"));

    private void OnMaps(ICommandContext context)
    {
        context.Reply(ChatFormat.Header($"Map Pool · Tier {options.MapVoting.MinimumTier}-{options.MapVoting.MaximumTier}"));
        context.Reply(ChatFormat.Row("MAPS ·", string.Join(" · ", EligibleMaps().Select(map => map.Name))));
    }

    private void OnNominate(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        players.Get(context.Sender.PlayerID)?.MarkActivity();
        if (_voteTimer is not null) { context.Reply(ChatFormat.Warning("A map vote is already running.")); return; }
        if (context.Args.Length == 0) { OpenNominationMenu(context.Sender); return; }
        if (context.Args.Length != 1) { context.Reply(ChatFormat.Warning("Usage: !nominate [map]")); return; }
        var query = context.Args[0];
        var matches = EligibleMaps().Where(map => map.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) { context.Reply(matches.Length == 0 ? ChatFormat.Error("Map not found.") : ChatFormat.Warning("Be more specific.")); return; }
        if (string.Equals(matches[0].Name, maps.Current?.Name, StringComparison.OrdinalIgnoreCase))
        { context.Reply(ChatFormat.Warning("That map is currently running.")); return; }
        Nominate(context.Sender, matches[0]);
    }

    private void OpenNominationMenu(SwiftlyS2.Shared.Players.IPlayer player)
    {
        var available = EligibleMaps()
            .Where(map => !map.Name.Equals(maps.Current?.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (available.Length == 0) { player.SendChat(ChatFormat.Error("No maps are available to nominate.")); return; }
        var builder = core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle("Nominate a map");
        builder.Design.SetMaxVisibleItems(5);
        builder.SetMoveForwardButton(KeyBind.Mouse1)
            .SetMoveBackwardButton(KeyBind.Mouse2)
            .SetSelectButton(KeyBind.E)
            .SetAutoCloseDelay(30f);
        foreach (var map in available)
        {
            var tier = mapConfigurations.Load(map.Name).Value.Tier;
            var option = new ButtonMenuOption($"{map.Name} (Tier {tier})") { CloseAfterClick = true };
            option.Click += (_, args) =>
            {
                Nominate(args.Player, map);
                return ValueTask.CompletedTask;
            };
            builder.AddOption(option);
        }
        core.MenusAPI.OpenMenuForPlayer(player, builder.Build());
    }

    private void Nominate(SwiftlyS2.Shared.Players.IPlayer player, MapPoolEntry map)
    {
        var configuration = mapConfigurations.Load(map.Name).Value;
        var routeType = configuration.StageCount > 0 ? "Staged" : "Linear";
        var steamId = player.SteamID;
        var playerName = player.Name;
        var generation = maps.Current?.Generation;
        core.Scheduler.NextTick(() =>
        {
            if (!_started || maps.Current?.Generation != generation || !players.Sessions.Any(session => session.SteamId == steamId)) return;
            _nominations[steamId] = map.Name;
            Broadcast($"{ChatFormat.Prefix} {ChatFormat.SuccessColor}{playerName}{ChatFormat.Reset} nominated {map.Name} {ChatFormat.MutedColor}· Tier {configuration.Tier} · {routeType}{ChatFormat.Reset}");
        });
    }

    private void OnRtv(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        players.Get(context.Sender.PlayerID)?.MarkActivity();
        if (_rtvLockedUntil > DateTimeOffset.UtcNow)
        {
            var remaining = (int)Math.Ceiling((_rtvLockedUntil - DateTimeOffset.UtcNow).TotalMinutes);
            context.Reply(ChatFormat.Warning($"This map was extended · RTV returns in {remaining} minute{(remaining == 1 ? "" : "s")}."));
            return;
        }
        if (_voteTimer is not null) { context.Reply(ChatFormat.Warning("A map vote is already running.")); return; }
        if (!_rtv.Add(context.Sender.SteamID)) { context.Reply(ChatFormat.Warning("You have already rocked the vote.")); return; }
        var needed = RequiredRtvVotes();
        Broadcast($"{ChatFormat.Prefix} {context.Sender.Name} rocked the vote {ChatFormat.HighlightColor}{_rtv.Count}/{needed}{ChatFormat.Reset}");
        if (_rtv.Count >= needed) StartVote(forced: false);
    }

    private int RequiredRtvVotes()
    {
        var humans = EligibleHumanPlayers().Count;
        return Math.Max(options.MapVoting.MinimumRtvVotes, (int)Math.Ceiling(humans * options.MapVoting.RtvThreshold));
    }

    private void StartVote(bool forced)
    {
        if (_voteTimer is not null) return;
        CancelForcedVote();
        var excluded = _recentMaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (maps.Current is { } current) excluded.Add(current.Name);
        var pool = EligibleMaps().Where(map => !excluded.Contains(map.Name)).ToList();
        var nominated = _nominations.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => pool.FirstOrDefault(map => map.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(map => map is not null).Cast<MapPoolEntry>().ToList();
        var remaining = BalanceByTier(pool.Where(map => nominated.All(value =>
            !value.Name.Equals(map.Name, StringComparison.OrdinalIgnoreCase))));
        _candidates = nominated.Concat(remaining).Take(options.MapVoting.CandidateCount).ToArray();
        if (_candidates.Count < 2)
        {
            Broadcast(ChatFormat.Error("Not enough eligible maps to start a vote."));
            _rtv.Clear();
            ScheduleForcedVote(options.MapVoting.ForceVoteAfterMinutes);
            return;
        }
        _voteWasForced = forced;
        _ballots.Clear();
        Broadcast(ChatFormat.Header($"Map Vote · {options.MapVoting.VoteDurationSeconds} seconds"));
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];
            var tier = mapConfigurations.Load(candidate.Name).Value.Tier;
            Broadcast($"{ChatFormat.HighlightColor}{i + 1}.{ChatFormat.Reset} {candidate.Name} {ChatFormat.MutedColor}· Tier {tier} · !{i + 1}{ChatFormat.Reset}");
        }
        if (CanExtendMap)
            Broadcast($"{ChatFormat.HighlightColor}{ExtendMapChoice}.{ChatFormat.Reset} Extend Map {ChatFormat.MutedColor}· {options.MapVoting.ExtendMapMinutes} minutes · {_mapExtensions}/{options.MapVoting.MaximumMapExtensions} used · !{ExtendMapChoice}{ChatFormat.Reset}");
        else
            Broadcast(ChatFormat.Warning($"Extension limit reached ({options.MapVoting.MaximumMapExtensions}) · this vote must choose another map."));
        _voteTimer = core.Scheduler.DelayBySeconds(options.MapVoting.VoteDurationSeconds, () => FinishVote(fromTimeout: true));
    }

    private void OnVote(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out var choice) ||
            (choice != ExtendMapChoice && (choice < 1 || choice > _candidates.Count)))
        { context.Reply(ChatFormat.Warning($"Usage: !mapvote <1-{_candidates.Count}|{ExtendMapChoice}>")); return; }
        SubmitVote(context, choice);
    }

    private void OnNumberVote(ICommandContext context, int choice)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        SubmitVote(context, choice);
    }

    private void SubmitVote(ICommandContext context, int choice)
    {
        if (_voteTimer is null) { context.Reply(ChatFormat.Warning("No map vote is running.")); return; }
        players.Get(context.Sender!.PlayerID)?.MarkActivity();
        if (choice == ExtendMapChoice && !CanExtendMap)
        { context.Reply(ChatFormat.Warning($"This map has already been extended {options.MapVoting.MaximumMapExtensions} times · choose another map.")); return; }
        var mapChoice = choice >= 1 && choice <= _candidates.Count;
        if (!mapChoice && choice != ExtendMapChoice)
        { context.Reply(ChatFormat.Warning($"Choose !1–!{_candidates.Count}, or !{ExtendMapChoice} to extend.")); return; }
        _ballots[context.Sender!.SteamID] = choice - 1;
        context.Reply(mapChoice
            ? ChatFormat.Success($"Voted for {_candidates[choice - 1].Name}.")
            : ChatFormat.Success($"Voted to extend by {options.MapVoting.ExtendMapMinutes} minutes."));
        TryFinishVoteWhenEveryoneVoted();
    }

    private void FinishVote(bool fromTimeout)
    {
        var completedTimer = _voteTimer;
        _voteTimer = null;
        // A scheduler owns and disposes a timer while invoking its callback.
        // Only cancel/dispose when the vote is closed early from another path.
        if (!fromTimeout && completedTimer is not null)
        {
            completedTimer.Cancel();
            completedTimer.Dispose();
        }
        if (_ballots.Count == 0)
        {
            if (_voteWasForced && _candidates.Count > 0)
            {
                var fallback = _candidates[Random.Shared.Next(_candidates.Count)];
                Broadcast(ChatFormat.Warning($"No votes cast · selected {fallback.Name} · changing in 8 seconds."));
                logger.LogInformation("Forced map vote selected fallback {Map} (Workshop {WorkshopId}).",
                    fallback.Name, fallback.WorkshopId);
                ResetVoteState();
                ChangeMapAfterDelay(fallback);
                return;
            }
            Broadcast(ChatFormat.Warning("Map vote ended with no votes."));
            ResetVoteState();
            ScheduleForcedVote(options.MapVoting.ForceVoteAfterMinutes);
            return;
        }
        var winner = _ballots.Values.GroupBy(value => value).OrderByDescending(group => group.Count())
            .ThenBy(_ => Random.Shared.Next()).First();
        if (winner.Key == ExtendMapBallot)
        {
            // Ballots cannot normally contain this choice after the limit,
            // but retain a defensive fallback for hot reloads/config changes.
            if (!CanExtendMap)
            {
                var fallback = _candidates[Random.Shared.Next(_candidates.Count)];
                Broadcast(ChatFormat.Warning($"Extension limit reached · selected {fallback.Name} · changing in 8 seconds."));
                ResetVoteState();
                ChangeMapAfterDelay(fallback);
                return;
            }
            _mapExtensions++;
            _rtvLockedUntil = DateTimeOffset.UtcNow.AddMinutes(options.MapVoting.ExtendMapMinutes);
            SaveRecentMaps();
            Broadcast(ChatFormat.Success($"Extend Map won with {winner.Count()} vote(s) · +{options.MapVoting.ExtendMapMinutes} minutes · {_mapExtensions}/{options.MapVoting.MaximumMapExtensions} extensions used."));
            ResetVoteState();
            ScheduleForcedVote(options.MapVoting.ExtendMapMinutes);
            return;
        }
        var map = _candidates[winner.Key];
        Broadcast(ChatFormat.Success($"{map.Name} won with {winner.Count()} vote(s) · changing in 8 seconds."));
        logger.LogInformation("Map vote selected {Map} (Workshop {WorkshopId}).", map.Name, map.WorkshopId);
        ResetVoteState();
        ChangeMapAfterDelay(map);
    }

    private void ChangeMapAfterDelay(MapPoolEntry map)
    {
        _nextMap = map;
        var generation = maps.Current?.Generation;
        core.Scheduler.DelayBySeconds(8f, () =>
        {
            if (_started && maps.Current?.Generation == generation && ReferenceEquals(_nextMap, map))
                core.Engine.ExecuteCommand($"host_workshop_map {map.WorkshopId}");
        });
    }

    private void ScheduleForcedVote(int minutes, DateTimeOffset? restoredDeadline = null)
    {
        if (!options.MapVoting.EndOfMapVoteEnabled) return;
        CancelForcedVote();
        _forcedVoteAt = restoredDeadline ?? DateTimeOffset.UtcNow.AddMinutes(minutes);
        SaveRecentMaps();
        var generation = maps.Current?.Generation;
        _forcedVoteTimer = core.Scheduler.DelayBySeconds((float)Math.Max(1, (_forcedVoteAt.Value - DateTimeOffset.UtcNow).TotalSeconds), () =>
        {
            _forcedVoteTimer = null;
            if (!_started || maps.Current?.Generation != generation) return;
            Broadcast(ChatFormat.Warning("Map time expired · starting the map vote."));
            StartVote(forced: true);
        });
        logger.LogInformation("Forced map vote scheduled in {Minutes} minutes.", minutes);
    }

    private void CancelForcedVote()
    {
        var timer = _forcedVoteTimer;
        _forcedVoteTimer = null;
        if (timer is not null) { timer.Cancel(); timer.Dispose(); }
    }

    private void ResetVoteState()
    {
        _rtv.Clear(); _nominations.Clear(); _ballots.Clear(); _candidates = []; _voteWasForced = false;
    }
    private void CancelVote()
    {
        var timer = _voteTimer;
        _voteTimer = null;
        if (timer is not null) { timer.Cancel(); timer.Dispose(); }
        ResetVoteState();
    }
    private void Broadcast(string message)
    {
        foreach (var player in core.PlayerManager.GetAllValidPlayers()) if (!player.IsFakeClient) player.SendChat(message);
    }

    private bool CanExtendMap => _mapExtensions < options.MapVoting.MaximumMapExtensions;

    private void LoadRecentMaps()
    {
        _recentMaps.Clear();
        try
        {
            if (!File.Exists(StatePath)) return;
            var knownMaps = options.MapVoting.Maps.Select(map => map.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var state = JsonSerializer.Deserialize<MapVotingState>(File.ReadAllText(StatePath));
            if (state?.ActiveMap == maps.Current?.Name && state?.ActiveMap is not null && state.ProcessStartedAtUtc == ProcessStartedAtUtc)
            {
                _forcedVoteAt = state.ForcedVoteAt;
                _mapExtensions = state.MapExtensions;
                _rtvLockedUntil = state.RtvLockedUntil;
            }
            foreach (var map in state?.RecentMaps ?? [])
            {
                if (!knownMaps.Contains(map) || _recentMaps.Contains(map, StringComparer.OrdinalIgnoreCase)) continue;
                _recentMaps.Enqueue(map);
            }
            while (_recentMaps.Count > options.MapVoting.RecentMapExclusionCount) _recentMaps.Dequeue();
            foreach (var (map, failures) in state?.CompatibilityFailures ?? new Dictionary<string, int>())
                if (knownMaps.Contains(map) && failures > 0) _compatibilityFailures[map] = failures;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not restore recent-map voting history from {Path}.", StatePath);
            _recentMaps.Clear();
        }
    }

    private void SaveRecentMaps()
    {
        try
        {
            Directory.CreateDirectory(core.PluginDataDirectory);
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new MapVotingState(_recentMaps.ToArray(), _compatibilityFailures, maps.Current?.Name, _forcedVoteAt, _mapExtensions, _rtvLockedUntil, ProcessStartedAtUtc),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not persist recent-map voting history to {Path}.", StatePath);
        }
    }

    private sealed record MapVotingState(string[] RecentMaps, IReadOnlyDictionary<string, int>? CompatibilityFailures = null,
        string? ActiveMap = null, DateTimeOffset? ForcedVoteAt = null, int MapExtensions = 0,
        DateTimeOffset RtvLockedUntil = default, DateTime ProcessStartedAtUtc = default);

    private void OnCompatibilityEvaluated(MapCompatibilityReport report)
    {
        if (!options.MapVoting.AutomaticQuarantineEnabled) return;
        if (report.IsCompatible)
        {
            if (_compatibilityFailures.Remove(report.MapName)) SaveRecentMaps();
            return;
        }
        var failures = _compatibilityFailures.GetValueOrDefault(report.MapName) + 1;
        _compatibilityFailures[report.MapName] = failures;
        SaveRecentMaps();
        if (failures >= options.MapVoting.CompatibilityFailuresBeforeQuarantine)
        {
            Broadcast(ChatFormat.Warning($"{report.MapName} was removed from voting after {failures} compatibility failures."));
            logger.LogError("Automatically quarantined {Map} after {Failures} compatibility failures: {Summary}.",
                report.MapName, failures, report.Summary);
        }
    }

    private IReadOnlyList<SwiftlyS2.Shared.Players.IPlayer> EligibleHumanPlayers() =>
        core.PlayerManager.GetAllValidPlayers().Where(player => !player.IsFakeClient &&
            (!options.MapVoting.ExcludeAfkPlayers || players.Get(player.PlayerID)?.IsAfk(
                TimeSpan.FromSeconds(options.MapVoting.AfkAfterSeconds)) != true)).ToArray();

    private void TryFinishVoteWhenEveryoneVoted()
    {
        if (_voteTimer is null) return;
        var humans = EligibleHumanPlayers();
        if (humans.Count == 0 || !humans.All(player => _ballots.ContainsKey(player.SteamID))) return;
        Broadcast(ChatFormat.Message("Every active player has voted · closing early."));
        FinishVote(fromTimeout: false);
    }

    private IReadOnlyList<MapPoolEntry> BalanceByTier(IEnumerable<MapPoolEntry> source)
    {
        var groups = source.GroupBy(map => mapConfigurations.Load(map.Name).Value.Tier)
            .Select(group => new Queue<MapPoolEntry>(group.OrderBy(_ => Random.Shared.Next())))
            .OrderBy(_ => Random.Shared.Next()).ToList();
        var result = new List<MapPoolEntry>();
        while (groups.Any(group => group.Count > 0))
            foreach (var group in groups)
                if (group.Count > 0) result.Add(group.Dequeue());
        return result;
    }

    private IReadOnlyList<MapPoolEntry> EligibleMaps()
    {
        var voting = options.MapVoting;
        return voting.Maps.Where(map =>
        {
            var configuration = mapConfigurations.Load(map.Name).Value;
            var quarantined = voting.AutomaticQuarantineEnabled &&
                _compatibilityFailures.GetValueOrDefault(map.Name) >= voting.CompatibilityFailuresBeforeQuarantine;
            return !quarantined && configuration.Enabled && configuration.Tier >= voting.MinimumTier &&
                   configuration.Tier <= voting.MaximumTier;
        }).ToArray();
    }
}





