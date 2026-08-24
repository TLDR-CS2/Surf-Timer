using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var mapDirectory = Path.Combine(workspace, "resources", "configs", "maps");
var localCfgDirectory = Path.Combine(workspace, "tools", "local-server", "maps");
var files = Directory.GetFiles(mapDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray();
Check(files.Length >= 16, $"Expected at least 16 catalog maps, found {files.Length}.");

var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var workshopIds = new HashSet<string>(StringComparer.Ordinal);
foreach (var path in files)
{
    var name = Path.GetFileNameWithoutExtension(path);
    Check(names.Add(name), $"Duplicate map configuration: {name}.");
    using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    var root = document.RootElement;
    var tier = root.GetProperty("Tier").GetInt32();
    Check(tier is >= 1 and <= 7, $"{name}: tier {tier} is outside 1-7.");
    Check(!string.IsNullOrWhiteSpace(root.GetProperty("StartTrigger").GetString()), $"{name}: start trigger is empty.");
    Check(!string.IsNullOrWhiteSpace(root.GetProperty("EndTrigger").GetString()), $"{name}: end trigger is empty.");
    var stages = root.TryGetProperty("StageCount", out var stageValue) ? stageValue.GetInt32() : 0;
    var bonuses = root.TryGetProperty("BonusCount", out var bonusValue) ? bonusValue.GetInt32() : 0;
    Check(stages == 0 || root.TryGetProperty("StagePrefix", out var stagePrefix) && !string.IsNullOrWhiteSpace(stagePrefix.GetString()),
        $"{name}: staged map lacks StagePrefix.");
    Check(bonuses == 0 || root.TryGetProperty("BonusPrefix", out var bonusPrefix) && !string.IsNullOrWhiteSpace(bonusPrefix.GetString()),
        $"{name}: bonus map lacks BonusPrefix.");
    var cfgPath = Path.Combine(localCfgDirectory, name + ".cfg");
    Check(File.Exists(cfgPath), $"{name}: local server cfg is missing.");
    var firstLine = File.ReadLines(cfgPath).FirstOrDefault() ?? string.Empty;
    var marker = "Workshop ";
    var markerIndex = firstLine.IndexOf(marker, StringComparison.Ordinal);
    Check(markerIndex >= 0, $"{name}: local cfg does not document its Workshop ID.");
    var workshopId = new string(firstLine[(markerIndex + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
    Check(workshopId.Length > 0, $"{name}: Workshop ID is invalid.");
    Check(workshopIds.Add(workshopId), $"{name}: duplicate Workshop ID {workshopId}.");
}

Console.WriteLine($"Catalog regression checks passed for {files.Length} maps.");
