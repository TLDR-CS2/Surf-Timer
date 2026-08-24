using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Maps;
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
    ILogger<MapVoteManager> logger)
{
    private const int ExtendMapChoice = 6;
    private const int ExtendMapBallot = ExtendMapChoice - 1;
    private readonly List<Guid> _registrations = [];
    private readonly HashSet<ulong> _rtv = [];
    private readonly Dictionary<ulong, string> _nominations = [];
    private readonly Dictionary<ulong, int> _ballots = [];
    private readonly Queue<string> _recentMaps = [];
    private IReadOnlyList<MapPoolEntry> _candidates = [];
    private CancellationTokenSource? _voteTimer;
    private DateTimeOffset _rtvLockedUntil;
    private bool _started;

    public void Start()
    {
        if (_started || !options.MapVoting.Enabled) return;
        _started = true;
        Register("maps", OnMaps, "Lists maps available for voting.");
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
        logger.LogInformation("SurfTimer map voting enabled; RTV={Rtv}, nominations={Nominations}.",
            options.MapVoting.RockTheVoteEnabled, options.MapVoting.NominationEnabled);
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnMapLoad -= OnMapLoad;
        core.Event.OnMapUnload -= OnMapUnload;
        CancelVote();
        foreach (var alias in new[] { "sw_vote", "sw_rockthevote", "sw_nom" }) core.Command.UnregisterCommand(alias);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear(); _rtv.Clear(); _nominations.Clear(); _started = false;
    }

    private void Register(string name, ICommandService.CommandListener callback, string help) =>
        _registrations.Add(core.Command.RegisterCommand(name, callback, false, helpText: help));

    private void OnMapLoad(IOnMapLoadEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.MapName))
        {
            _recentMaps.Enqueue(e.MapName);
            while (_recentMaps.Count > options.MapVoting.RecentMapExclusionCount) _recentMaps.Dequeue();
        }
        _rtvLockedUntil = default;
        _rtv.Clear(); _nominations.Clear(); CancelVote();
    }

    private void OnMapUnload(IOnMapUnloadEvent _) { _rtvLockedUntil = default; _rtv.Clear(); _nominations.Clear(); CancelVote(); }

    private void OnMaps(ICommandContext context)
    {
        context.Reply($"[SurfTimer] Tier {options.MapVoting.MinimumTier}-{options.MapVoting.MaximumTier} maps: " +
                      string.Join(", ", EligibleMaps().Select(map => map.Name)));
    }

    private void OnNominate(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        if (_voteTimer is not null) { context.Reply("[SurfTimer] A map vote is already running."); return; }
        if (context.Args.Length == 0) { OpenNominationMenu(context.Sender); return; }
        if (context.Args.Length != 1) { context.Reply("[SurfTimer] Usage: !nominate [map]"); return; }
        var query = context.Args[0];
        var matches = EligibleMaps().Where(map => map.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) { context.Reply(matches.Length == 0 ? "[SurfTimer] Map not found." : "[SurfTimer] Be more specific."); return; }
        if (string.Equals(matches[0].Name, maps.Current?.Name, StringComparison.OrdinalIgnoreCase))
        { context.Reply("[SurfTimer] That map is currently running."); return; }
        Nominate(context.Sender, matches[0]);
    }

    private void OpenNominationMenu(SwiftlyS2.Shared.Players.IPlayer player)
    {
        var available = EligibleMaps()
            .Where(map => !map.Name.Equals(maps.Current?.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (available.Length == 0) { player.SendChat("[SurfTimer] No maps are available to nominate."); return; }
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
        core.Scheduler.NextTick(() =>
        {
            _nominations[steamId] = map.Name;
            Broadcast($"[SurfTimer] {playerName} nominated {map.Name} (Tier {configuration.Tier}, {routeType}).");
        });
    }

    private void OnRtv(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        if (_rtvLockedUntil > DateTimeOffset.UtcNow)
        {
            var remaining = (int)Math.Ceiling((_rtvLockedUntil - DateTimeOffset.UtcNow).TotalMinutes);
            context.Reply($"[SurfTimer] This map was extended; RTV is available again in {remaining} minute{(remaining == 1 ? "" : "s")}.");
            return;
        }
        if (_voteTimer is not null) { context.Reply("[SurfTimer] A map vote is already running."); return; }
        if (!_rtv.Add(context.Sender.SteamID)) { context.Reply("[SurfTimer] You have already rocked the vote."); return; }
        var needed = RequiredRtvVotes();
        Broadcast($"[SurfTimer] {context.Sender.Name} rocked the vote ({_rtv.Count}/{needed}).");
        if (_rtv.Count >= needed) StartVote();
    }

    private int RequiredRtvVotes()
    {
        var humans = core.PlayerManager.GetAllValidPlayers().Count(player => !player.IsFakeClient);
        return Math.Max(options.MapVoting.MinimumRtvVotes, (int)Math.Ceiling(humans * options.MapVoting.RtvThreshold));
    }

    private void StartVote()
    {
        var excluded = _recentMaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (maps.Current is { } current) excluded.Add(current.Name);
        var pool = EligibleMaps().Where(map => !excluded.Contains(map.Name)).ToList();
        var nominated = _nominations.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => pool.FirstOrDefault(map => map.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(map => map is not null).Cast<MapPoolEntry>().ToList();
        var remaining = pool.Where(map => nominated.All(value => !value.Name.Equals(map.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(_ => Random.Shared.Next()).ToList();
        _candidates = nominated.Concat(remaining).Take(options.MapVoting.CandidateCount).ToArray();
        if (_candidates.Count < 2) { Broadcast("[SurfTimer] Not enough eligible maps to start a vote."); _rtv.Clear(); return; }
        _ballots.Clear();
        Broadcast($"[SurfTimer] Map vote started for {options.MapVoting.VoteDurationSeconds} seconds:");
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];
            var tier = mapConfigurations.Load(candidate.Name).Value.Tier;
            Broadcast($"  {i + 1}. {candidate.Name} (Tier {tier}) - !{i + 1}");
        }
        Broadcast($"  {ExtendMapChoice}. Extend Map ({options.MapVoting.ExtendMapMinutes} minutes) - !{ExtendMapChoice}");
        _voteTimer = core.Scheduler.DelayBySeconds(options.MapVoting.VoteDurationSeconds, () => FinishVote(fromTimeout: true));
    }

    private void OnVote(ICommandContext context)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out var choice) ||
            (choice != ExtendMapChoice && (choice < 1 || choice > _candidates.Count)))
        { context.Reply($"[SurfTimer] Usage: !mapvote <1-{_candidates.Count}|{ExtendMapChoice}>"); return; }
        SubmitVote(context, choice);
    }

    private void OnNumberVote(ICommandContext context, int choice)
    {
        if (context.Sender is null) { context.Reply("This command requires a player caller."); return; }
        SubmitVote(context, choice);
    }

    private void SubmitVote(ICommandContext context, int choice)
    {
        if (_voteTimer is null) { context.Reply("[SurfTimer] No map vote is running."); return; }
        var mapChoice = choice >= 1 && choice <= _candidates.Count;
        if (!mapChoice && choice != ExtendMapChoice)
        { context.Reply($"[SurfTimer] Choose a listed map with !1 through !{_candidates.Count}, or !{ExtendMapChoice} to extend."); return; }
        _ballots[context.Sender!.SteamID] = choice - 1;
        context.Reply(mapChoice
            ? $"[SurfTimer] Voted for {_candidates[choice - 1].Name}."
            : $"[SurfTimer] Voted to extend the map by {options.MapVoting.ExtendMapMinutes} minutes.");
        var humans = core.PlayerManager.GetAllValidPlayers().Where(player => !player.IsFakeClient).ToArray();
        if (humans.Length > 0 && humans.All(player => _ballots.ContainsKey(player.SteamID)))
        {
            Broadcast("[SurfTimer] Every connected player has voted — closing the vote early.");
            FinishVote(fromTimeout: false);
        }
    }

    private void FinishVote(bool fromTimeout)
    {
        var completedTimer = _voteTimer;
        _voteTimer = null;
        if (!fromTimeout) completedTimer?.Cancel();
        completedTimer?.Dispose();
        if (_ballots.Count == 0) { Broadcast("[SurfTimer] Map vote ended with no votes."); ResetVoteState(); return; }
        var winner = _ballots.Values.GroupBy(value => value).OrderByDescending(group => group.Count())
            .ThenBy(_ => Random.Shared.Next()).First();
        if (winner.Key == ExtendMapBallot)
        {
            _rtvLockedUntil = DateTimeOffset.UtcNow.AddMinutes(options.MapVoting.ExtendMapMinutes);
            Broadcast($"[SurfTimer] Extend Map won with {winner.Count()} vote(s). RTV is locked for {options.MapVoting.ExtendMapMinutes} minutes.");
            ResetVoteState();
            return;
        }
        var map = _candidates[winner.Key];
        Broadcast($"[SurfTimer] Vote won by {map.Name} with {winner.Count()} vote(s). Changing map in 8 seconds...");
        logger.LogInformation("Map vote selected {Map} (Workshop {WorkshopId}).", map.Name, map.WorkshopId);
        ResetVoteState();
        core.Scheduler.DelayBySeconds(8f, () => core.Engine.ExecuteCommand($"host_workshop_map {map.WorkshopId}"));
    }

    private void ResetVoteState() { _rtv.Clear(); _nominations.Clear(); _ballots.Clear(); _candidates = []; }
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

    private IReadOnlyList<MapPoolEntry> EligibleMaps()
    {
        var voting = options.MapVoting;
        return voting.Maps.Where(map =>
        {
            var configuration = mapConfigurations.Load(map.Name).Value;
            return configuration.Enabled && configuration.Tier >= voting.MinimumTier &&
                   configuration.Tier <= voting.MaximumTier;
        }).ToArray();
    }
}
