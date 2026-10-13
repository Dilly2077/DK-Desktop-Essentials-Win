using System.Text.Json;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class ProfileCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Profiles create, export, duplicate, import and delete without collisions", RoundTrip),
        ("Earlier schema-one profiles load with current defaults", Legacy),
        ("Malformed profiles are reported without hiding valid profiles", Malformed),
        ("Validation rejects null collections, unknown enums and non-finite tuning", Validation),
        ("Conflict analysis reports duplicate IDs, dependency cycles and overlap", Conflicts),
        ("Concurrent profile writes leave one valid file and no temporary files", ConcurrentProfiles),
        ("Concurrent preference edits persist every independent device alias", ConcurrentPreferences),
        ("Canceled saves preserve the previous profile and preferences", CanceledWrites),
        ("Preferences recover from malformed/null data without rewriting it", PreferencesRecovery)
    };
    private static async Task RoundTrip()
    {
        await using var f = new Fixture();
        var profile = Profile(Binding(StandardControl.South, StandardControl.East)); profile.Name = "Round trip";
        await f.Profiles.SaveAsync(profile);
        var exported = await f.Profiles.ExportJsonAsync(profile.Id);
        var imported = await f.Profiles.ImportJsonAsync(exported, preserveId: true);
        var duplicate = await f.Profiles.DuplicateAsync(profile.Id, "Copy");
        Check(imported.Id != profile.Id && duplicate.Id != profile.Id && duplicate.Id != imported.Id, "Profile ID collision");
        var loaded = await f.Profiles.LoadAsync(profile.Id);
        Check(loaded!.Layers[0].Bindings[0].Target.ControllerControl == StandardControl.East, "Mapping changed during export/import");
        var concurrentJson = JsonSerializer.Serialize(Profile(), Json);
        var simultaneous = await Task.WhenAll(f.Profiles.ImportJsonAsync(concurrentJson, true), f.Profiles.ImportJsonAsync(concurrentJson, true));
        Check(simultaneous[0].Id != simultaneous[1].Id, "Concurrent imports overwrote the same profile");
        await f.Profiles.DeleteAsync(profile.Id);
        Check(await f.Profiles.LoadAsync(profile.Id) is null && (await f.Profiles.LoadAllAsync()).Count == 4, "Delete damaged other profiles");
    }
    private static async Task Legacy()
    {
        await using var f = new Fixture();
        var id = Guid.NewGuid();
        var legacy = $$"""{"schemaVersion":1,"id":"{{id}}","name":"Earlier profile","outputMode":"None","layers":[{"id":"base","isBaseLayer":true,"bindings":[]}]}""";
        var imported = await f.Profiles.ImportJsonAsync(legacy, preserveId: true);
        Check(imported.Id == id && imported.AdvancedDeviceId is null && imported.Layers[0].StickMappings.Count == 0, "Schema-one defaults changed");
        var loaded = await f.Profiles.LoadAsync(id);
        Check(loaded is not null && loaded.SchemaVersion == 1, "Earlier profile cannot be reloaded");
    }
    private static async Task Malformed()
    {
        await using var f = new Fixture(); await f.Profiles.SaveAsync(Profile());
        var broken = Path.Combine(f.Profiles.ProfileDirectory, "broken.json");
        var nulls = Path.Combine(f.Profiles.ProfileDirectory, "nulls.json");
        await File.WriteAllTextAsync(broken, "{broken"); await File.WriteAllTextAsync(nulls, "{\"layers\":null}");
        var result = await f.Profiles.LoadAllDetailedAsync();
        Check(result.Profiles.Count == 1 && result.Issues.Count == 2, "Malformed profiles blocked valid profiles or were silently ignored");
        Check(await File.ReadAllTextAsync(broken) == "{broken", "Corrupt source was modified");
    }
    private static async Task Validation()
    {
        await using var f = new Fixture();
        foreach (var input in new[]
        {
            "{\"layers\":null}", "{\"tuning\":null}", "{\"deviceMatch\":null}",
            "{\"layers\":[null]}", "{\"layers\":[{\"id\":\"base\",\"bindings\":null}]}",
            "{\"outputMode\":999}", "{\"layers\":[{\"id\":\"base\",\"bindings\":[{\"source\":null}]}]}",
            "{\"layers\":[{\"id\":\"base\",\"bindings\":[{\"mode\":999}]}]}",
            "{\"layers\":[{\"id\":\"base\"},{\"id\":\"base\"}]}"
        }) await Reject(() => f.Profiles.ImportJsonAsync(input));
        var profile = Profile(); profile.Tuning.Add(new() { CurveExponent = double.NaN });
        Check(f.Profiles.GetValidationErrors(profile).Count > 0, "Non-finite tuning accepted");
        profile.Tuning[0].CurveExponent = 1;
        var binding = Binding(StandardControl.South, StandardControl.East); binding.Source = new() { Kind = ControllerControlKind.RawButton, Index = -1 };
        profile.Layers[0].Bindings.Add(binding);
        Check(f.Profiles.GetValidationErrors(profile).Count > 0, "Negative raw index accepted");
    }
    private static Task Conflicts()
    {
        var duplicates = Profile(); duplicates.Layers.Add(new() { Id = "base" });
        Check(ControllerMappingAnalyzer.Analyze(duplicates).Any(c => c.Code == "duplicate-layer-id"), "Duplicate layer analysis threw or failed");
        var profile = Profile(); profile.Layers.Add(new() { Id = "one", RequiredLayerIds = new() { "two" } });
        profile.Layers.Add(new() { Id = "two", RequiredLayerIds = new() { "one" } });
        var binding = Binding(StandardControl.LeftTrigger, StandardControl.East);
        binding.ValueZones.Add(new() { Minimum = 0.2, Maximum = 0.7, Target = ControllerAction.ToController(StandardControl.East) });
        binding.ValueZones.Add(new() { Minimum = 0.4, Maximum = 0.9, Target = ControllerAction.ToController(StandardControl.South) });
        profile.Layers[0].Bindings.Add(binding);
        var conflicts = ControllerMappingAnalyzer.Analyze(profile);
        Check(conflicts.Any(c => c.Severity == MappingConflictSeverity.Error) && conflicts.Any(c => c.Code == "overlapping-value-zones"), "Missing cycle/overlap conflicts");
        return Task.CompletedTask;
    }
    private static async Task ConcurrentProfiles()
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => f.Profiles.SaveAsync(new() { Id = id, Name = $"Writer {i}" })));
        var result = await f.Profiles.LoadAllDetailedAsync();
        Check(result.Profiles.Count == 1 && result.Issues.Count == 0 && result.Profiles[0].Id == id, "Concurrent profile writes corrupted storage");
        Check(!Directory.EnumerateFiles(f.Profiles.ProfileDirectory, "*.tmp").Any(), "Temporary files leaked");
    }
    private static async Task ConcurrentPreferences()
    {
        await using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => f.Preferences.SetDeviceNameAsync($"device-{i}", $" Alias {i} ")));
        var result = await new ControllerHubPreferencesStore(f.Preferences.SettingsPath).LoadAsync();
        Check(result.DeviceNames.Count == 16 && result.DeviceNames["device-3"] == "Alias 3", "Overlapping preference updates lost aliases");
        var profile = Guid.NewGuid();
        await Task.WhenAll(f.Preferences.SetDefaultDeviceAsync("device-3"), f.Preferences.SetDefaultProfileAsync("device-3", profile));
        result = await f.Preferences.LoadAsync();
        Check(result.DefaultDeviceId == "device-3" && result.DefaultProfileByDevice["device-3"] == profile, "Defaults lost during update");
        await f.Preferences.RemoveProfileReferencesAsync(profile);
        Check((await f.Preferences.LoadAsync()).DefaultProfileByDevice.Count == 0, "Deleted-profile preference references retained");
    }
    private static async Task CanceledWrites()
    {
        await using var f = new Fixture(); var profile = Profile(); profile.Name = "Preserve";
        await f.Profiles.SaveAsync(profile); await f.Preferences.SetDeviceNameAsync("a", "Preserve");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        profile.Name = "Canceled";
        try { await f.Profiles.SaveAsync(profile, cancellation.Token); throw new Exception("Canceled save succeeded"); }
        catch (OperationCanceledException) { }
        try { await f.Preferences.SetDeviceNameAsync("a", "Canceled", cancellation.Token); throw new Exception("Canceled preference update succeeded"); }
        catch (OperationCanceledException) { }
        Check((await f.Profiles.LoadAsync(profile.Id))!.Name == "Preserve" && (await f.Preferences.LoadAsync()).DeviceNames["a"] == "Preserve", "Cancellation replaced existing files");
        Check(!Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any(), "Cancellation leaked temporary files");
    }
    private static async Task PreferencesRecovery()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        foreach (var json in new[] { "{broken", "{\"schemaVersion\":99}", "{\"deviceNames\":null,\"defaultProfileByDevice\":null}" })
        {
            await File.WriteAllTextAsync(f.Preferences.SettingsPath, json);
            Check((await f.Preferences.LoadAsync()).DeviceNames.Count == 0, "Invalid preferences did not fall back");
            Check(await File.ReadAllTextAsync(f.Preferences.SettingsPath) == json, "Fallback rewrote user data");
        }
    }
}
