using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace SurfTimer.Maps;

public sealed record MapConfiguration
{
    public bool Enabled { get; init; } = true;
    public int Tier { get; init; } = 1;
    public string StartTrigger { get; init; } = "map_start";
    public string EndTrigger { get; init; } = "map_end";
    public string CheckpointPrefix { get; init; } = "map_cp";
    public int? CheckpointCount { get; init; }
    public string? StagePrefix { get; init; }
    public int StageCount { get; init; }
    public string BonusPrefix { get; init; } = "bonus";
    public int BonusCount { get; init; }
    public int MaxVelocity { get; init; } = 3500;
}

public sealed record MapValidation(bool IsValid, IReadOnlyList<string> Issues)
{
    public string Summary => IsValid ? "valid" : string.Join("; ", Issues);
}

public enum MapCompatibilitySeverity { Info, Warning, Error }

public sealed record MapCompatibilityFinding(MapCompatibilitySeverity Severity, string Message);

public sealed record MapCompatibilityReport(
    string MapName,
    bool IsRuntime,
    IReadOnlyList<MapCompatibilityFinding> Findings)
{
    public int Errors => Findings.Count(value => value.Severity == MapCompatibilitySeverity.Error);
    public int Warnings => Findings.Count(value => value.Severity == MapCompatibilitySeverity.Warning);
    public int Information => Findings.Count(value => value.Severity == MapCompatibilitySeverity.Info);
    public bool IsCompatible => Errors == 0;
    public string Summary => $"{(IsCompatible ? "compatible" : "incompatible")} errors={Errors} warnings={Warnings} info={Information}";
}

public sealed record CatalogMapCompatibility(string MapName, string Source, MapCompatibilityReport Report);
public sealed record CatalogMapConfiguration(string MapName, MapConfiguration Configuration, string Source);

public sealed record LoadedMapConfiguration(MapConfiguration Value, string Source);

public sealed class MapConfigurationProvider(ISwiftlyCore core, ILogger<MapConfigurationProvider> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public LoadedMapConfiguration Load(string mapName)
    {
        var safeName = Path.GetFileName(mapName);
        if (!string.Equals(safeName, mapName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException($"Unsafe map name '{mapName}'.");

        var overridePath = Path.Combine(core.PluginDataDirectory, "maps", safeName + ".json");
        var bundledPath = Path.Combine(core.PluginPath, "resources", "configs", "maps", safeName + ".json");
        var path = File.Exists(overridePath) ? overridePath : File.Exists(bundledPath) ? bundledPath : null;
        if (path is null) return new LoadedMapConfiguration(new MapConfiguration(), "defaults");

        var value = JsonSerializer.Deserialize<MapConfiguration>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"Map configuration '{path}' is empty.");
        Validate(value, path);
        logger.LogInformation("Loaded map configuration for {MapName} from {Source}.", mapName, path);
        return new LoadedMapConfiguration(value, path);
    }

    public string SaveOverride(string mapName, MapConfiguration value)
    {
        Validate(value, mapName);
        var safeName = Path.GetFileName(mapName);
        if (!string.Equals(safeName, mapName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException($"Unsafe map name '{mapName}'.");
        var directory = Path.Combine(core.PluginDataDirectory, "maps");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, safeName + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        logger.LogInformation("Saved map configuration override for {MapName} to {Path}.", mapName, path);
        return path;
    }

    public IReadOnlyList<CatalogMapCompatibility> AuditCatalog()
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSources(Path.Combine(core.PluginPath, "resources", "configs", "maps"), sources, overwrite: false);
        AddSources(Path.Combine(core.PluginDataDirectory, "maps"), sources, overwrite: true);
        var results = new List<CatalogMapCompatibility>();
        foreach (var (mapName, path) in sources.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var value = JsonSerializer.Deserialize<MapConfiguration>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("configuration is empty");
                Validate(value, path);
                results.Add(new CatalogMapCompatibility(mapName, path,
                    MapCompatibilityAnalyzer.AnalyzeConfiguration(mapName, value)));
            }
            catch (Exception exception)
            {
                results.Add(new CatalogMapCompatibility(mapName, path,
                    new MapCompatibilityReport(mapName, false,
                    [new(MapCompatibilitySeverity.Error, exception.Message)])));
            }
        }
        return results;
    }

    public IReadOnlyList<CatalogMapConfiguration> LoadCatalog()
    {
        var sources = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        AddSources(Path.Combine(core.PluginPath,"resources","configs","maps"),sources,overwrite:false);
        AddSources(Path.Combine(core.PluginDataDirectory,"maps"),sources,overwrite:true);
        var results=new List<CatalogMapConfiguration>(sources.Count);
        foreach(var (mapName,path) in sources.OrderBy(value=>value.Key,StringComparer.OrdinalIgnoreCase))
        {
            var value=JsonSerializer.Deserialize<MapConfiguration>(File.ReadAllText(path),JsonOptions)
                ?? throw new InvalidDataException($"Map configuration '{path}' is empty.");
            Validate(value,path);
            results.Add(new(mapName,value,path));
        }
        return results;
    }

    private static void AddSources(string directory, IDictionary<string, string> sources, bool overwrite)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var mapName = Path.GetFileNameWithoutExtension(path);
            if (overwrite || !sources.ContainsKey(mapName)) sources[mapName] = path;
        }
    }

    private static void Validate(MapConfiguration value, string path)
    {
        if (value.Tier is < 1 or > 7) throw new InvalidDataException($"Tier in '{path}' must be between 1 and 7.");
        if (string.IsNullOrWhiteSpace(value.StartTrigger) || string.IsNullOrWhiteSpace(value.EndTrigger) ||
            string.IsNullOrWhiteSpace(value.CheckpointPrefix))
            throw new InvalidDataException($"Trigger names in '{path}' must not be empty.");
        if (value.CheckpointCount is < 0 or > 255)
            throw new InvalidDataException($"CheckpointCount in '{path}' must be between 0 and 255.");
        if (value.StageCount is < 0 or > 255)
            throw new InvalidDataException($"StageCount in '{path}' must be between 0 and 255.");
        if (value.StageCount > 0 && string.IsNullOrWhiteSpace(value.StagePrefix))
            throw new InvalidDataException($"StagePrefix in '{path}' is required when StageCount is greater than zero.");
        if (value.BonusCount is < 0 or > 255)
            throw new InvalidDataException($"BonusCount in '{path}' must be between 0 and 255.");
        if (value.BonusCount > 0 && string.IsNullOrWhiteSpace(value.BonusPrefix))
            throw new InvalidDataException($"BonusPrefix in '{path}' is required when BonusCount is greater than zero.");
        if (value.MaxVelocity is < 1 or > 10000)
            throw new InvalidDataException($"MaxVelocity in '{path}' must be between 1 and 10000.");
    }
}
