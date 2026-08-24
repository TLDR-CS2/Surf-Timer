namespace SurfTimer.Maps;

public static class MapCompatibilityAnalyzer
{
    public static MapCompatibilityReport AnalyzeConfiguration(string mapName, MapConfiguration config)
    {
        var findings = new List<MapCompatibilityFinding>();
        if (!config.Enabled) findings.Add(new(MapCompatibilitySeverity.Warning, "map is disabled in the catalog"));
        if (config.StartTrigger.Equals(config.EndTrigger, StringComparison.Ordinal))
            findings.Add(new(MapCompatibilitySeverity.Error, "start and end trigger names are identical"));
        if (config.CheckpointCount is null)
            findings.Add(new(MapCompatibilitySeverity.Info, "checkpoint count will be detected from the loaded map"));
        findings.Add(new(MapCompatibilitySeverity.Info,
            $"tier={config.Tier} checkpoints={config.CheckpointCount?.ToString() ?? "auto"} stages={config.StageCount} bonuses={config.BonusCount}"));
        return new MapCompatibilityReport(mapName, false, findings);
    }

    public static MapCompatibilityReport AnalyzeRuntime(
        string mapName, MapConfiguration config, IReadOnlyList<MapTriggerSnapshot> triggers)
    {
        var findings = AnalyzeConfiguration(mapName, config).Findings.ToList();
        CheckRequired(findings, triggers, config.StartTrigger, "start");
        CheckRequired(findings, triggers, config.EndTrigger, "end");

        var checkpoints = ParseIndices(triggers, config.CheckpointPrefix, string.Empty);
        var expectedCheckpoints = config.CheckpointCount ?? checkpoints.DefaultIfEmpty(0).Max();
        CheckSequence(findings, checkpoints, 1, expectedCheckpoints, "checkpoint");

        if (config.StageCount > 1 && !string.IsNullOrWhiteSpace(config.StagePrefix))
            CheckSequence(findings, ParseIndices(triggers, config.StagePrefix, "_start"), 2, config.StageCount, "stage start");

        for (var bonus = 1; bonus <= config.BonusCount; bonus++)
        {
            CheckRequired(findings, triggers, $"{config.BonusPrefix}{bonus}_start", $"bonus {bonus} start");
            CheckRequired(findings, triggers, $"{config.BonusPrefix}{bonus}_end", $"bonus {bonus} end");
        }

        var duplicates = triggers.Where(value => !string.IsNullOrWhiteSpace(value.TargetName))
            .GroupBy(value => value.TargetName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var duplicate in duplicates)
            findings.Add(new(MapCompatibilitySeverity.Info,
                $"trigger '{duplicate.Key}' has {duplicate.Count()} entity copies"));

        findings.Add(new(MapCompatibilitySeverity.Info,
            $"discovered {triggers.Count} trigger entities and {triggers.Select(value => value.TargetName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count()} named triggers"));
        return new MapCompatibilityReport(mapName, true, findings);
    }

    private static void CheckRequired(List<MapCompatibilityFinding> findings,
        IReadOnlyList<MapTriggerSnapshot> triggers, string targetName, string label)
    {
        if (!triggers.Any(value => value.TargetName.Equals(targetName, StringComparison.Ordinal)))
            findings.Add(new(MapCompatibilitySeverity.Error, $"missing {label} trigger '{targetName}'"));
    }

    private static SortedSet<int> ParseIndices(IReadOnlyList<MapTriggerSnapshot> triggers, string prefix, string suffix)
    {
        var result = new SortedSet<int>();
        foreach (var trigger in triggers)
        {
            var name = trigger.TargetName;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var length = name.Length - prefix.Length - suffix.Length;
            if (length > 0 && int.TryParse(name.AsSpan(prefix.Length, length), out var index) && index > 0) result.Add(index);
        }
        return result;
    }

    private static void CheckSequence(List<MapCompatibilityFinding> findings, SortedSet<int> found,
        int first, int last, string label)
    {
        for (var index = first; index <= last; index++)
            if (!found.Contains(index)) findings.Add(new(MapCompatibilitySeverity.Error, $"missing {label} {index}"));
        foreach (var index in found.Where(index => index > last))
            findings.Add(new(MapCompatibilitySeverity.Warning, $"unexpected {label} {index} exceeds configured count {last}"));
    }
}
