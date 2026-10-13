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
    private readonly SemaphoreSlim _writeGate = new(1, 1);
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
            if (settings is null || settings.DeviceNames is null || settings.DefaultProfileByDevice is null || settings.SchemaVersion != ControllerHubPreferences.CurrentSchemaVersion)
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
        await _writeGate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(settings, cancellationToken); }
        finally { _writeGate.Release(); }
    }

    private async Task UpdateAsync(Action<ControllerHubPreferences> update, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadAsync(cancellationToken);
            update(settings);
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async Task SaveCoreAsync(ControllerHubPreferences settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SchemaVersion = ControllerHubPreferences.CurrentSchemaVersion;

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        var temporary = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task SetDeviceNameAsync(string deviceId, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return UpdateAsync(settings =>
        {
            if (string.IsNullOrWhiteSpace(name)) settings.DeviceNames.Remove(deviceId);
            else settings.DeviceNames[deviceId] = name.Trim();
        }, cancellationToken);
    }

    public Task SetDefaultDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.DefaultDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId, cancellationToken);

    public Task SetDefaultProfileAsync(string deviceId, Guid? profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return UpdateAsync(settings =>
        {
            if (profileId is null || profileId == Guid.Empty) settings.DefaultProfileByDevice.Remove(deviceId);
            else settings.DefaultProfileByDevice[deviceId] = profileId.Value;
        }, cancellationToken);
    }

    public Task RemoveProfileReferencesAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        UpdateAsync(settings =>
        {
            foreach (var device in settings.DefaultProfileByDevice.Where(pair => pair.Value == profileId).Select(pair => pair.Key).ToArray())
                settings.DefaultProfileByDevice.Remove(device);
        }, cancellationToken);
}