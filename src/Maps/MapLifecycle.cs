using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.SchemaDefinitions;
using SurfTimer.Storage;
using SwiftlyS2.Shared.Natives;

namespace SurfTimer.Maps;

public sealed record MapSnapshot(
    string Name,
    string WorkshopId,
    long Generation,
    DateTimeOffset LoadedAt,
    int MultipleTriggers,
    int OnceTriggers,
    int TeleportTriggers,
    MapConfiguration Configuration,
    string ConfigurationSource);

public sealed record MapTriggerSnapshot(uint EntityIndex, string DesignerName, string TargetName);

public sealed class MapLifecycle(
    ISwiftlyCore core,
    RecordRepository records,
    MapConfigurationProvider configurations,
    ILogger<MapLifecycle> logger)
{
    private bool _started;
    private long _generation;
    private readonly List<MapTriggerSnapshot> _triggers = [];
    private CancellationTokenSource? _compatibilityTimer;

    public MapSnapshot? Current { get; private set; }
    public IReadOnlyList<MapTriggerSnapshot> Triggers => _triggers;
    public MapValidation Validation => ValidateCurrent();
    public MapCompatibilityReport Compatibility => Current is null
        ? new MapCompatibilityReport("none", true, [new(MapCompatibilitySeverity.Error, "no active map")])
        : MapCompatibilityAnalyzer.AnalyzeRuntime(Current.Name, Current.Configuration, _triggers);
    public IReadOnlyList<CatalogMapCompatibility> AuditCatalog() => configurations.AuditCatalog();
    public int CheckpointCount => Current?.Configuration.CheckpointCount ?? GetDetectedCheckpointCount();
    public int StageCount => Current?.Configuration.StageCount ?? 0;
    public int BonusCount => Current?.Configuration.BonusCount ?? 0;

    public void Start(bool hotReload)
    {
        if (_started) return;
        _started = true;
        core.Event.OnMapLoad += OnMapLoad;
        core.Event.OnMapUnload += OnMapUnload;
        core.Event.OnEntitySpawned += OnEntitySpawned;

        if (hotReload)
        {
            var mapName = core.Engine.GlobalVars.MapName.ToString();
            if (!string.IsNullOrWhiteSpace(mapName)) Load(mapName);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        core.Event.OnMapLoad -= OnMapLoad;
        core.Event.OnMapUnload -= OnMapUnload;
        core.Event.OnEntitySpawned -= OnEntitySpawned;
        Current = null;
        _triggers.Clear();
        _compatibilityTimer?.Cancel();
        _compatibilityTimer = null;
        _started = false;
    }

    private void OnMapLoad(IOnMapLoadEvent gameEvent) => Load(gameEvent.MapName);

    private void OnMapUnload(IOnMapUnloadEvent gameEvent)
    {
        logger.LogInformation("Map unloading: {MapName} (generation {Generation}).",
            gameEvent.MapName, Current?.Generation ?? _generation);
        Current = null;
        _triggers.Clear();
        _compatibilityTimer?.Cancel();
        _compatibilityTimer = null;
    }

    private void OnEntitySpawned(IOnEntitySpawnedEvent gameEvent)
    {
        if (Current is null) return;

        var designerName = gameEvent.Entity.DesignerName;
        Current = designerName switch
        {
            "trigger_multiple" => Current with { MultipleTriggers = Current.MultipleTriggers + 1 },
            "trigger_once" => Current with { OnceTriggers = Current.OnceTriggers + 1 },
            "trigger_teleport" => Current with { TeleportTriggers = Current.TeleportTriggers + 1 },
            _ => Current
        };

        if (designerName is "trigger_multiple" or "trigger_once" or "trigger_teleport")
        {
            _triggers.Add(new MapTriggerSnapshot(
                gameEvent.Entity.Index,
                designerName,
                gameEvent.Entity.Identity?.Name ?? string.Empty));
            if (Current is { } current && IsConfiguredTimerTrigger(_triggers[^1].TargetName, current.Configuration))
                _ = records.TrackMapMetadataAsync(current.Name, current.WorkshopId, CheckpointCount, StageCount, BonusCount,
                    current.Configuration.Tier, current.Configuration.Enabled);
        }
    }

    private void Load(string mapName)
    {
        _compatibilityTimer?.Cancel();
        _triggers.Clear();
        var loadedConfiguration = configurations.Load(mapName);
        Current = new MapSnapshot(
            mapName,
            core.Engine.WorkshopId,
            ++_generation,
            DateTimeOffset.UtcNow,
            Count("trigger_multiple"),
            Count("trigger_once"),
            Count("trigger_teleport"),
            loadedConfiguration.Value,
            loadedConfiguration.Source);

        logger.LogInformation(
            "Map loaded: {MapName} (Workshop {WorkshopId}, generation {Generation}); triggers: multiple={Multiple}, once={Once}, teleport={Teleport}.",
            Current.Name, Current.WorkshopId, Current.Generation,
            Current.MultipleTriggers, Current.OnceTriggers, Current.TeleportTriggers);
        var generation = Current.Generation;
        _compatibilityTimer = core.Scheduler.DelayBySeconds(8f, () => LogCompatibility(generation));
        _ = records.TrackMapMetadataAsync(Current.Name, Current.WorkshopId, CheckpointCount, StageCount, BonusCount,
            Current.Configuration.Tier, Current.Configuration.Enabled);
    }

    private void LogCompatibility(long generation)
    {
        if (Current is null || Current.Generation != generation) return;
        var report = Compatibility;
        if (report.IsCompatible)
            logger.LogInformation("Map compatibility certified for {MapName}: {Summary}.", report.MapName, report.Summary);
        else
            logger.LogWarning("Map compatibility failed for {MapName}: {Summary}; {Findings}", report.MapName,
                report.Summary, string.Join("; ", report.Findings
                    .Where(value => value.Severity == MapCompatibilitySeverity.Error)
                    .Select(value => value.Message)));
    }

    public void ReloadConfiguration()
    {
        if (Current is null) return;
        var loaded = configurations.Load(Current.Name);
        Current = Current with { Configuration = loaded.Value, ConfigurationSource = loaded.Source };
        _ = records.TrackMapMetadataAsync(Current.Name, Current.WorkshopId, CheckpointCount, StageCount, BonusCount,
            Current.Configuration.Tier, Current.Configuration.Enabled);
        logger.LogInformation("Reloaded map configuration for {MapName}: tier={Tier}, enabled={Enabled}, validation={Validation}.",
            Current.Name, Current.Configuration.Tier, Current.Configuration.Enabled, Validation.Summary);
    }

    public void UpdateConfiguration(Func<MapConfiguration, MapConfiguration> update)
    {
        if (Current is null) throw new InvalidOperationException("No map is active.");
        configurations.SaveOverride(Current.Name, update(Current.Configuration));
        ReloadConfiguration();
    }

    public bool IsStartTrigger(string? targetName) => targetName is not null && targetName == Current?.Configuration.StartTrigger;
    public bool IsEndTrigger(string? targetName) => targetName is not null && targetName == Current?.Configuration.EndTrigger;

    public bool TryParseStageStart(string? targetName, out int stage)
    {
        stage = 0;
        var config = Current?.Configuration;
        if (targetName is null || config is null || config.StageCount <= 0 || string.IsNullOrEmpty(config.StagePrefix)) return false;
        var prefix = config.StagePrefix;
        if (!targetName.StartsWith(prefix, StringComparison.Ordinal) || !targetName.EndsWith("_start", StringComparison.Ordinal)) return false;
        var number = targetName.AsSpan(prefix.Length, targetName.Length - prefix.Length - "_start".Length);
        return int.TryParse(number, out stage) && stage >= 1 && stage <= config.StageCount;
    }

    public bool TryParseBonusTrigger(string? targetName, string suffix, out int bonus)
    {
        bonus = 0;
        var config = Current?.Configuration;
        if (targetName is null || config is null || config.BonusCount <= 0 || string.IsNullOrEmpty(config.BonusPrefix)) return false;
        var ending = "_" + suffix;
        if (!targetName.StartsWith(config.BonusPrefix, StringComparison.Ordinal) || !targetName.EndsWith(ending, StringComparison.Ordinal)) return false;
        var number = targetName.AsSpan(config.BonusPrefix.Length, targetName.Length - config.BonusPrefix.Length - ending.Length);
        return int.TryParse(number, out bonus) && bonus >= 1 && bonus <= config.BonusCount;
    }

    public bool TryGetBonusStartTransform(int bonus, out Vector position, out QAngle angles)
        => TryGetTriggerTransform(candidate =>
            TryParseBonusTrigger(candidate.TargetName, "start", out var found) && found == bonus,
            out position, out angles);

    public bool TryGetMainStartTransform(out Vector position, out QAngle angles)
        => TryGetTriggerTransform(candidate => IsStartTrigger(candidate.TargetName), out position, out angles);

    private bool TryGetTriggerTransform(
        Func<MapTriggerSnapshot, bool> predicate,
        out Vector position,
        out QAngle angles)
    {
        position = default;
        angles = default;
        // Maps can contain several copies of the same named trigger. Some are
        // stale or inactive by the time commands are used, so prefer the most
        // recently spawned live copy instead of trusting the first match.
        for (var index = _triggers.Count - 1; index >= 0; index--)
        {
            var trigger = _triggers[index];
            if (!predicate(trigger)) continue;
            var entity = core.EntitySystem.GetEntityByIndex<CBaseEntity>(trigger.EntityIndex);
            if (entity is null || !entity.IsValid || entity.AbsOrigin is not { } origin) continue;
            position = new Vector(origin);
            angles = entity.AbsRotation is { } rotation ? new QAngle(rotation) : new QAngle(0f, 0f, 0f);
            return true;
        }
        return false;
    }

    public bool TryParseCheckpoint(string? targetName, out int checkpoint)
    {
        checkpoint = 0;
        var prefix = Current?.Configuration.CheckpointPrefix;
        return targetName is not null && !string.IsNullOrEmpty(prefix) &&
               targetName.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(targetName.AsSpan(prefix.Length), out checkpoint) && checkpoint > 0;
    }

    private int GetDetectedCheckpointCount()
    {
        var maximum = 0;
        foreach (var trigger in _triggers)
        {
            var prefix = Current?.Configuration.CheckpointPrefix ?? "map_cp";
            if (trigger.TargetName.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(trigger.TargetName.AsSpan(prefix.Length), out var checkpoint) && checkpoint > maximum)
                maximum = checkpoint;
        }
        return maximum;
    }

    private MapValidation ValidateCurrent()
    {
        if (Current is null) return new MapValidation(false, ["no active map"]);
        var issues = new List<string>();
        var config = Current.Configuration;
        if (!config.Enabled) issues.Add("map disabled");
        if (!_triggers.Any(trigger => trigger.TargetName == config.StartTrigger)) issues.Add($"missing start trigger '{config.StartTrigger}'");
        if (!_triggers.Any(trigger => trigger.TargetName == config.EndTrigger)) issues.Add($"missing end trigger '{config.EndTrigger}'");
        var detected = _triggers.Select(trigger => TryParseCheckpoint(trigger.TargetName, out var cp) ? cp : 0)
            .Where(cp => cp > 0).Distinct().Order().ToArray();
        var expected = config.CheckpointCount ?? (detected.Length == 0 ? 0 : detected[^1]);
        for (var checkpoint = 1; checkpoint <= expected; checkpoint++)
            if (!detected.Contains(checkpoint)) issues.Add($"missing checkpoint {checkpoint}");
        // Stage 1 is represented by the configured main start trigger. Maps
        // commonly name only subsequent stage boundaries s2_start, s3_start, etc.
        for (var stage = 2; stage <= config.StageCount; stage++)
            if (!_triggers.Any(trigger => TryParseStageStart(trigger.TargetName, out var found) && found == stage))
                issues.Add($"missing stage {stage} start");
        for (var bonus = 1; bonus <= config.BonusCount; bonus++)
        {
            if (!_triggers.Any(trigger => TryParseBonusTrigger(trigger.TargetName, "start", out var found) && found == bonus))
                issues.Add($"missing bonus {bonus} start");
            if (!_triggers.Any(trigger => TryParseBonusTrigger(trigger.TargetName, "end", out var found) && found == bonus))
                issues.Add($"missing bonus {bonus} end");
        }
        return new MapValidation(issues.Count == 0, issues);
    }

    private static bool IsConfiguredTimerTrigger(string targetName, MapConfiguration configuration) =>
        targetName == configuration.StartTrigger || targetName == configuration.EndTrigger ||
        targetName.StartsWith(configuration.CheckpointPrefix, StringComparison.Ordinal) ||
        (configuration.StageCount > 0 && !string.IsNullOrEmpty(configuration.StagePrefix) &&
         targetName.StartsWith(configuration.StagePrefix, StringComparison.Ordinal) && targetName.EndsWith("_start", StringComparison.Ordinal)) ||
        (configuration.BonusCount > 0 && targetName.StartsWith(configuration.BonusPrefix, StringComparison.Ordinal) &&
         (targetName.EndsWith("_start", StringComparison.Ordinal) || targetName.EndsWith("_end", StringComparison.Ordinal)));

    private int Count(string designerName) =>
        core.EntitySystem.GetAllEntitiesByDesignerName<CEntityInstance>(designerName).Count();
}
