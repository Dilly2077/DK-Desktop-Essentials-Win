using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed record ControllerProfileLoadIssue(string FileName, string Message);

public sealed class ControllerProfileLoadResult
{
    public IReadOnlyList<ControllerProfile> Profiles { get; init; } = Array.Empty<ControllerProfile>();
    public IReadOnlyList<ControllerProfileLoadIssue> Issues { get; init; } = Array.Empty<ControllerProfileLoadIssue>();
}

public sealed class ControllerProfileStore
{
    private readonly string _profileDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public ControllerProfileStore(string? profileDirectory = null)
    {
        _profileDirectory = profileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DKDesktopEssentials",
            "Controllers",
            "Profiles");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public string ProfileDirectory => _profileDirectory;

    public async Task SaveAsync(ControllerProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_profileDirectory);
        Validate(profile);

        var destination = GetProfilePath(profile.Id);
        var temporary = destination + ".tmp";
        var json = JsonSerializer.Serialize(profile, _jsonOptions);

        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    public async Task<ControllerProfile?> LoadAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var path = GetProfilePath(profileId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return DeserializeValidated(json);
    }

    public async Task<IReadOnlyList<ControllerProfile>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        (await LoadAllDetailedAsync(cancellationToken)).Profiles;

    public async Task<ControllerProfileLoadResult> LoadAllDetailedAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_profileDirectory))
            return new ControllerProfileLoadResult();

        var profiles = new List<ControllerProfile>();
        var issues = new List<ControllerProfileLoadIssue>();

        foreach (var path in Directory.EnumerateFiles(_profileDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var profile = DeserializeValidated(json);
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                issues.Add(new ControllerProfileLoadIssue(Path.GetFileName(path), ex.Message));
            }
        }

        return new ControllerProfileLoadResult
        {
            Profiles = profiles
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id)
                .ToArray(),
            Issues = issues
        };
    }

    public async Task<ControllerProfile> DuplicateAsync(
        Guid profileId,
        string? newName = null,
        CancellationToken cancellationToken = default)
    {
        var source = await LoadAsync(profileId, cancellationToken)
            ?? throw new FileNotFoundException($"Controller profile {profileId} was not found.");

        var json = JsonSerializer.Serialize(source, _jsonOptions);
        var duplicate = DeserializeValidated(json);
        duplicate.Id = Guid.NewGuid();
        duplicate.Name = string.IsNullOrWhiteSpace(newName)
            ? $"{source.Name} Copy"
            : newName.Trim();

        await SaveAsync(duplicate, cancellationToken);
        return duplicate;
    }

    public async Task<string> ExportJsonAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await LoadAsync(profileId, cancellationToken)
            ?? throw new FileNotFoundException($"Controller profile {profileId} was not found.");

        return JsonSerializer.Serialize(profile, _jsonOptions);
    }

    public async Task<ControllerProfile> ImportJsonAsync(
        string json,
        bool preserveId = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Imported controller profile JSON cannot be empty.");

        var profile = DeserializeValidated(json);
        if (!preserveId || File.Exists(GetProfilePath(profile.Id)))
            profile.Id = Guid.NewGuid();

        await SaveAsync(profile, cancellationToken);
        return profile;
    }

    public IReadOnlyList<string> GetValidationErrors(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CollectValidationErrors(profile);
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetProfilePath(profileId);

        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    private string GetProfilePath(Guid profileId) =>
        Path.Combine(_profileDirectory, $"{profileId:N}.json");

    private ControllerProfile DeserializeValidated(string json)
    {
        var profile = JsonSerializer.Deserialize<ControllerProfile>(json, _jsonOptions)
            ?? throw new InvalidDataException("Controller profile JSON did not contain a profile.");

        Validate(profile);
        return profile;
    }

    private static void Validate(ControllerProfile profile)
    {
        var errors = CollectValidationErrors(profile);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));

        if (profile.Layers.Count == 0)
            profile.Layers.Add(ControllerLayer.CreateBaseLayer());

        if (!profile.Layers.Any(x => x.IsBaseLayer))
            profile.Layers.Insert(0, ControllerLayer.CreateBaseLayer());
    }

    private static List<string> CollectValidationErrors(ControllerProfile profile)
    {
        var errors = new List<string>();

        if (profile.SchemaVersion != ControllerProfile.CurrentSchemaVersion)
        {
            errors.Add(
                $"Unsupported controller profile schema {profile.SchemaVersion}; expected {ControllerProfile.CurrentSchemaVersion}.");
        }

        if (profile.Id == Guid.Empty)
            errors.Add("Controller profile ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add("Controller profile name cannot be empty.");

        if (profile.Tuning.Any(tuning => tuning.Deadzone is < 0d or > 1d))
            errors.Add("Deadzone values must be between 0 and 1.");

        if (profile.Tuning.Any(tuning => tuning.AntiDeadzone is < 0d or > 1d))
            errors.Add("Anti-deadzone values must be between 0 and 1.");

        if (profile.Tuning.Any(tuning => tuning.OuterDeadzone is < 0d or > 1d))
            errors.Add("Outer-deadzone values must be between 0 and 1.");

        if (profile.Tuning.Any(tuning => tuning.CurveExponent <= 0d))
            errors.Add("Response-curve exponents must be greater than zero.");

        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in profile.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id))
                errors.Add("Layer IDs cannot be empty.");
            else if (!layerIds.Add(layer.Id))
                errors.Add($"Layer ID '{layer.Id}' is duplicated.");

            var bindingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in layer.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Id))
                    errors.Add($"Layer '{layer.Name}' contains a binding with an empty ID.");
                else if (!bindingIds.Add(binding.Id))
                    errors.Add($"Layer '{layer.Name}' contains duplicate binding ID '{binding.Id}'.");

                if (binding.ActivationThreshold is < 0d or > 1d)
                    errors.Add($"Binding '{binding.Id}' activation threshold must be between 0 and 1.");

                if (binding.Mode == ControllerBindingMode.Turbo && binding.TurboHz is < 1d or > 30d)
                    errors.Add($"Binding '{binding.Id}' turbo frequency must be between 1 and 30 Hz.");

                if (binding.Target.Kind == ControllerActionKind.ControllerControl && binding.Target.ControllerControl is null)
                    errors.Add($"Binding '{binding.Id}' has no controller target.");

                if (binding.Target.Kind == ControllerActionKind.MacroReference && string.IsNullOrWhiteSpace(binding.Target.MacroId))
                    errors.Add($"Binding '{binding.Id}' has no macro reference.");
            }
        }

        errors.AddRange(AdvancedMappingValidator.CollectErrors(profile));
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }
}
