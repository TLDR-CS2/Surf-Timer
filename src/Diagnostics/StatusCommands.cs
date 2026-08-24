using SurfTimer.Maps;
using SurfTimer.Players;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SurfTimer.Storage;
using SurfTimer.Replays;
using SurfTimer.Configuration;

namespace SurfTimer.Diagnostics;

public sealed class StatusCommands(
    ISwiftlyCore core,
    SurfPlayerManager players,
    MapLifecycle maps,
    RecordRepository records,
    BotControllerBridge botController,
    SurfTimerOptions options)
{
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0)
        {
            return;
        }

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_status",
            OnStatus,
            registerRaw: true,
            helpText: "Displays SurfTimer runtime status."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_version",
            OnVersion,
            registerRaw: true,
            helpText: "Displays the SurfTimer version."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_dump_player",
            OnDumpPlayer,
            registerRaw: true,
            helpText: "Displays the caller's tracked SurfTimer player session."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_dump_map",
            OnDumpMap,
            registerRaw: true,
            helpText: "Displays the current SurfTimer map lifecycle state."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_dump_triggers",
            OnDumpTriggers,
            registerRaw: true,
            helpText: "Lists the current map's timer-relevant triggers."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_db_health",
            OnDatabaseHealth,
            registerRaw: true,
            helpText: "Checks SurfTimer's shared database connection."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_map_info",
            OnMapInfo,
            registerRaw: true,
            helpText: "Displays current map metadata and trigger validation."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_map_check", OnMapCheck, registerRaw: true,
            helpText: "Runs a detailed mapper-trigger compatibility check for the loaded map."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_catalog_check", OnCatalogCheck, registerRaw: true,
            helpText: "Checks all map configuration files; loaded-map triggers are checked separately."));

        _registrations.Add(core.Command.RegisterCommand(
            "surftimer_map_reload",
            OnMapReload,
            registerRaw: true,
            helpText: "Reloads current map metadata from disk (server console only)."));
    }

    public void Unregister()
    {
        foreach (var registration in _registrations)
        {
            core.Command.UnregisterCommand(registration);
        }

        _registrations.Clear();
    }

    private void OnStatus(ICommandContext context)
    {
        context.Reply($"SurfTimer {BuildInfo.Version}: map={maps.Current?.Name ?? "none"}, players={players.Count}, " +
                      $"server={options.ServerId}, database={records.Status}, failures={records.ConsecutiveFailures}, " +
                      $"botController={(botController.IsAvailable ? $"abi-{botController.AbiVersion}" : "unavailable")}");
    }

    private void OnDatabaseHealth(ICommandContext context) => _ = CheckDatabaseHealthAsync(context);

    private async Task CheckDatabaseHealthAsync(ICommandContext context)
    {
        var health = await records.CheckHealthAsync().ConfigureAwait(false);
        core.Scheduler.NextTick(() => context.Reply(
            $"SurfTimer DB: {(health.IsHealthy ? "healthy" : "unhealthy")}, server={health.ServerId}, " +
            $"connection={health.ConnectionName}, latency={health.LatencyMilliseconds}ms, detail={health.Message}"));
    }

    private static void OnVersion(ICommandContext context)
    {
        context.Reply($"SurfTimer {BuildInfo.Version}");
    }

    private void OnDumpPlayer(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is null)
        {
            context.Reply("This command currently requires a player caller.");
            return;
        }

        var session = players.Get(context.Sender.PlayerID);
        if (session is null)
        {
            context.Reply($"No SurfTimer session is tracked for player {context.Sender.PlayerID}.");
            return;
        }

        context.Reply(
            $"player={session.PlayerId} session={session.SessionId} name=\"{session.Name}\" " +
            $"steamid={session.SteamId} authorized={session.IsAuthorized} bot={session.IsBot} " +
            $"alive={session.IsAlive} team={session.Team} run={session.Run.State} " +
            $"checkpoint={session.Run.LastCheckpoint} startDepth={session.Run.StartZoneTouchDepth}");
    }

    private void OnDumpMap(ICommandContext context)
    {
        var map = maps.Current;
        if (map is null)
        {
            context.Reply("SurfTimer has no active map context.");
            return;
        }

        context.Reply(
            $"map={map.Name} workshop={map.WorkshopId} generation={map.Generation} " +
            $"tier={map.Configuration.Tier} enabled={map.Configuration.Enabled} checkpoints={maps.CheckpointCount} stages={maps.StageCount} " +
            $"validation={maps.Validation.Summary} " +
            $"triggers[multiple={map.MultipleTriggers},once={map.OnceTriggers},teleport={map.TeleportTriggers}]");
    }

    private void OnDumpTriggers(ICommandContext context)
    {
        if (maps.Current is null)
        {
            context.Reply("SurfTimer has no active map context.");
            return;
        }

        context.Reply($"SurfTimer trigger inventory for {maps.Current.Name}: {maps.Triggers.Count} entities");
        foreach (var trigger in maps.Triggers.OrderBy(x => x.EntityIndex))
        {
            var targetName = string.IsNullOrWhiteSpace(trigger.TargetName) ? "<unnamed>" : trigger.TargetName;
            context.Reply($"#{trigger.EntityIndex} {trigger.DesignerName} name=\"{targetName}\"");
        }
    }

    private void OnMapInfo(ICommandContext context)
    {
        var map = maps.Current;
        if (map is null)
        {
            context.Reply("SurfTimer has no active map context.");
            return;
        }

        var config = map.Configuration;
        context.Reply(
            $"map={map.Name} enabled={config.Enabled} tier={config.Tier} checkpoints={maps.CheckpointCount} stages={maps.StageCount} " +
            $"start=\"{config.StartTrigger}\" end=\"{config.EndTrigger}\" cpPrefix=\"{config.CheckpointPrefix}\" " +
            $"validation={maps.Validation.Summary} source=\"{map.ConfigurationSource}\"");
    }

    private void OnMapReload(ICommandContext context)
    {
        if (context.IsSentByPlayer)
        {
            context.Reply("This command is restricted to the server console.");
            return;
        }

        try
        {
            maps.ReloadConfiguration();
            OnMapInfo(context);
        }
        catch (Exception exception)
        {
            context.Reply($"SurfTimer map configuration reload failed: {exception.Message}");
        }
    }

    private void OnMapCheck(ICommandContext context)
    {
        var report = maps.Compatibility;
        context.Reply($"SurfTimer map compatibility: map={report.MapName} {report.Summary}");
        foreach (var finding in report.Findings)
            context.Reply($"[{finding.Severity.ToString().ToUpperInvariant()}] {finding.Message}");
    }

    private void OnCatalogCheck(ICommandContext context)
    {
        var results = maps.AuditCatalog();
        context.Reply($"SurfTimer catalog compatibility: maps={results.Count} incompatible={results.Count(value => !value.Report.IsCompatible)} (runtime entities require the map to be loaded)");
        foreach (var result in results)
            context.Reply($"{result.MapName}: {result.Report.Summary} source=\"{result.Source}\"");
    }
}
