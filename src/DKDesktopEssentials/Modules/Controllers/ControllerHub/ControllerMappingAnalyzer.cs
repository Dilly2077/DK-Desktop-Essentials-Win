namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public static class ControllerMappingAnalyzer
{
    public static IReadOnlyList<ControllerMappingConflict> Analyze(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var conflicts = new List<ControllerMappingConflict>();
        var layers = profile.Layers.ToDictionary(layer => layer.Id, StringComparer.Ordinal);

        foreach (var layer in profile.Layers)
        {
            foreach (var required in layer.RequiredLayerIds)
            {
                if (!layers.ContainsKey(required))
                {
                    conflicts.Add(new ControllerMappingConflict(
                        MappingConflictSeverity.Error,
                        "missing-required-layer",
                        $"Layer '{layer.Name}' requires unknown layer '{required}'.",
                        layer.Id));
                }
            }

            foreach (var blocked in layer.BlockedLayerIds)
            {
                if (!layers.ContainsKey(blocked))
                {
                    conflicts.Add(new ControllerMappingConflict(
                        MappingConflictSeverity.Error,
                        "missing-blocked-layer",
                        $"Layer '{layer.Name}' blocks unknown layer '{blocked}'.",
                        layer.Id));
                }
            }

            if (layer.RequiredLayerIds.Contains(layer.Id, StringComparer.Ordinal))
            {
                conflicts.Add(new ControllerMappingConflict(
                    MappingConflictSeverity.Error,
                    "self-required-layer",
                    $"Layer '{layer.Name}' cannot require itself.",
                    layer.Id));
            }

            if (layer.BlockedLayerIds.Contains(layer.Id, StringComparer.Ordinal))
            {
                conflicts.Add(new ControllerMappingConflict(
                    MappingConflictSeverity.Error,
                    "self-blocked-layer",
                    $"Layer '{layer.Name}' cannot block itself.",
                    layer.Id));
            }

            AnalyzeBindings(layer, conflicts);
            AnalyzeStickMappings(layer, conflicts);
            AnalyzeRadialMappings(layer, conflicts);
        }

        DetectRequiredLayerCycles(profile.Layers, conflicts);
        return conflicts;
    }

    private static void AnalyzeBindings(
        ControllerLayer layer,
        List<ControllerMappingConflict> conflicts)
    {
        var groups = layer.Bindings
            .GroupBy(binding => binding.Source.StableKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            var bindings = group.ToArray();
            for (var i = 0; i < bindings.Length; i++)
            {
                for (var j = i + 1; j < bindings.Length; j++)
                {
                    if (!CanCompete(bindings[i], bindings[j]))
                        continue;

                    conflicts.Add(new ControllerMappingConflict(
                        MappingConflictSeverity.Warning,
                        "competing-bindings",
                        $"Layer '{layer.Name}' has multiple active mappings for source '{group.Key}'. Priority/strongest-value rules will decide the output.",
                        layer.Id,
                        bindings[j].Id));
                }
            }
        }

        foreach (var binding in layer.Bindings)
        {
            var zones = binding.ValueZones.OrderBy(zone => zone.Minimum).ToArray();
            for (var i = 0; i < zones.Length; i++)
            {
                for (var j = i + 1; j < zones.Length; j++)
                {
                    if (zones[i].Maximum < zones[j].Minimum || zones[j].Maximum < zones[i].Minimum)
                        continue;

                    conflicts.Add(new ControllerMappingConflict(
                        MappingConflictSeverity.Error,
                        "overlapping-value-zones",
                        $"Binding '{binding.Id}' contains overlapping analog value zones '{zones[i].Id}' and '{zones[j].Id}'.",
                        layer.Id,
                        binding.Id));
                }
            }
        }
    }

    private static bool CanCompete(ControllerBinding first, ControllerBinding second)
    {
        if (first.PressBehavior != second.PressBehavior)
            return false;

        if (first.RequiredChord is not null || second.RequiredChord is not null)
            return false;

        if (first.ValueZones.Count == 0 || second.ValueZones.Count == 0)
            return true;

        return first.ValueZones.Any(a => second.ValueZones.Any(b =>
            a.Minimum <= b.Maximum && b.Minimum <= a.Maximum));
    }

    private static void AnalyzeStickMappings(
        ControllerLayer layer,
        List<ControllerMappingConflict> conflicts)
    {
        var targetGroups = layer.StickMappings.GroupBy(
            mapping => $"{mapping.TargetX}:{mapping.TargetY}",
            StringComparer.Ordinal);

        foreach (var group in targetGroups.Where(group => group.Count() > 1))
        {
            conflicts.Add(new ControllerMappingConflict(
                MappingConflictSeverity.Warning,
                "competing-stick-mappings",
                $"Layer '{layer.Name}' contains multiple stick transforms targeting '{group.Key}'.",
                layer.Id,
                group.Last().Id));
        }
    }

    private static void AnalyzeRadialMappings(
        ControllerLayer layer,
        List<ControllerMappingConflict> conflicts)
    {
        foreach (var mapping in layer.RadialMappings)
        {
            for (var i = 0; i < mapping.Sectors.Count; i++)
            {
                for (var j = i + 1; j < mapping.Sectors.Count; j++)
                {
                    if (!SectorsOverlap(mapping.Sectors[i], mapping.Sectors[j]))
                        continue;

                    conflicts.Add(new ControllerMappingConflict(
                        MappingConflictSeverity.Error,
                        "overlapping-radial-sectors",
                        $"Radial mapping '{mapping.Id}' contains overlapping sectors '{mapping.Sectors[i].Name}' and '{mapping.Sectors[j].Name}'.",
                        layer.Id,
                        mapping.Id));
                }
            }
        }
    }

    private static bool SectorsOverlap(ControllerRadialSector first, ControllerRadialSector second)
    {
        var firstRanges = ExpandSector(first.StartDegrees, first.EndDegrees);
        var secondRanges = ExpandSector(second.StartDegrees, second.EndDegrees);

        return firstRanges.Any(a => secondRanges.Any(b =>
            a.Start < b.End && b.Start < a.End));
    }

    private static IReadOnlyList<(double Start, double End)> ExpandSector(double start, double end)
    {
        start = NormalizeDegrees(start);
        end = NormalizeDegrees(end);

        if (Math.Abs(start - end) < 0.0001d)
            return new[] { (0d, 360d) };

        return start < end
            ? new[] { (start, end) }
            : new[] { (start, 360d), (0d, end) };
    }

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static void DetectRequiredLayerCycles(
        IReadOnlyList<ControllerLayer> layers,
        List<ControllerMappingConflict> conflicts)
    {
        var byId = layers.ToDictionary(layer => layer.Id, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layer in layers)
            Visit(layer);

        void Visit(ControllerLayer layer)
        {
            if (visited.Contains(layer.Id))
                return;

            if (!visiting.Add(layer.Id))
            {
                conflicts.Add(new ControllerMappingConflict(
                    MappingConflictSeverity.Error,
                    "layer-dependency-cycle",
                    $"Layer dependency cycle detected at '{layer.Name}'.",
                    layer.Id));
                return;
            }

            foreach (var requiredId in layer.RequiredLayerIds)
            {
                if (byId.TryGetValue(requiredId, out var required))
                    Visit(required);
            }

            visiting.Remove(layer.Id);
            visited.Add(layer.Id);
        }
    }
}
