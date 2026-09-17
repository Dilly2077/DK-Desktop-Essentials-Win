using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

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
        var profile = JsonSerializer.Deserialize<ControllerProfile>(json, _jsonOptions);

        if (profile is not null)
            Validate(profile);

        return profile;
    }

    public async Task<IReadOnlyList<ControllerProfile>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_profileDirectory))
            return Array.Empty<ControllerProfile>();

        var profiles = new List<ControllerProfile>();

        foreach (var path in Directory.EnumerateFiles(_profileDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var profile = JsonSerializer.Deserialize<ControllerProfile>(json, _jsonOptions);
                if (profile is null)
                    continue;

                Validate(profile);
                profiles.Add(profile);
            }
            catch (JsonException)
            {
                // A malformed user profile must not prevent the remaining profiles from loading.
            }
            catch (InvalidDataException)
            {
                // Unsupported schema or invalid user profile; leave the file untouched for recovery/editing.
            }
        }

        return profiles
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToArray();
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

    private static void Validate(ControllerProfile profile)
    {
        if (profile.SchemaVersion != ControllerProfile.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported controller profile schema {profile.SchemaVersion}; expected {ControllerProfile.CurrentSchemaVersion}.");
        }

        if (profile.Id == Guid.Empty)
            throw new InvalidDataException("Controller profile ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidDataException("Controller profile name cannot be empty.");

        if (profile.Layers.Count == 0)
            profile.Layers.Add(ControllerLayer.CreateBaseLayer());

        if (!profile.Layers.Any(x => x.IsBaseLayer))
            profile.Layers.Insert(0, ControllerLayer.CreateBaseLayer());
    }
}
