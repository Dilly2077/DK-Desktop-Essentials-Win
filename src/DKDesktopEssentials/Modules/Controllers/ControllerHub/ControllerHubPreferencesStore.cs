using System.IO;
using System.Text.Json;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerHubPreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? DefaultDeviceId { get; set; }
    public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Guid> DefaultProfileByDevice { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ControllerHubPreferencesStore
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ControllerHubPreferencesStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DKDesktopEssentials",
            "Controllers",
            "controller-hub.json");
    }

    public string SettingsPath => _settingsPath;

    public async Task<ControllerHubPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
            return new ControllerHubPreferences();

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            var settings = JsonSerializer.Deserialize<ControllerHubPreferences>(json, _jsonOptions);
            if (settings is null || settings.SchemaVersion != ControllerHubPreferences.CurrentSchemaVersion)
                return new ControllerHubPreferences();

            settings.DeviceNames = new Dictionary<string, string>(settings.DeviceNames, StringComparer.Ordinal);
            settings.DefaultProfileByDevice = new Dictionary<string, Guid>(settings.DefaultProfileByDevice, StringComparer.Ordinal);
            return settings;
        }
        catch (JsonException)
        {
            // Leave an unreadable user file untouched and fall back to safe defaults.
            return new ControllerHubPreferences();
        }
        catch (IOException)
        {
            return new ControllerHubPreferences();
        }
    }

    public async Task SaveAsync(ControllerHubPreferences settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SchemaVersion = ControllerHubPreferences.CurrentSchemaVersion;

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        var temporary = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, _settingsPath, overwrite: true);
    }

    public async Task SetDeviceNameAsync(string deviceId, string? name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));

        var settings = await LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(name))
            settings.DeviceNames.Remove(deviceId);
        else
            settings.DeviceNames[deviceId] = name.Trim();

        await SaveAsync(settings, cancellationToken);
    }

    public async Task SetDefaultDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken);
        settings.DefaultDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        await SaveAsync(settings, cancellationToken);
    }

    public async Task SetDefaultProfileAsync(string deviceId, Guid? profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));

        var settings = await LoadAsync(cancellationToken);
        if (profileId is null || profileId == Guid.Empty)
            settings.DefaultProfileByDevice.Remove(deviceId);
        else
            settings.DefaultProfileByDevice[deviceId] = profileId.Value;

        await SaveAsync(settings, cancellationToken);
    }

    public async Task RemoveProfileReferencesAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken);
        var affectedDevices = settings.DefaultProfileByDevice
            .Where(pair => pair.Value == profileId)
            .Select(pair => pair.Key)
            .ToArray();

        if (affectedDevices.Length == 0)
            return;

        foreach (var deviceId in affectedDevices)
            settings.DefaultProfileByDevice.Remove(deviceId);

        await SaveAsync(settings, cancellationToken);
    }
}
