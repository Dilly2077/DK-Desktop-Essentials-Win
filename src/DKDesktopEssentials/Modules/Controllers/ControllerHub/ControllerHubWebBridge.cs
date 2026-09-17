using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerHubWebBridge
{
    private readonly ControllerHubService _service;
    private readonly JsonSerializerOptions _jsonOptions;

    public ControllerHubWebBridge(ControllerHubService service)
    {
        _service = service;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<string?> HandleAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        message = message.TrimStart();
        if (message.Length == 0 || message[0] != '{')
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var command = ReadString(root, "type");
            if (command is null || !command.StartsWith("controllerHub:", StringComparison.Ordinal))
                return null;

            var requestId = ReadString(root, "requestId");
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : default;

            try
            {
                var result = await ExecuteAsync(command, payload, cancellationToken);
                return SerializeResponse(command, requestId, true, result, null);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or JsonException or Win32Exception)
            {
                return SerializeResponse(command, requestId, false, null, ex.Message);
            }
        }
    }

    private async Task<object?> ExecuteAsync(
        string command,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "controllerHub:listDevices":
            {
                var preferences = await _service.LoadPreferencesAsync(cancellationToken);
                return _service.GetConnectedDevices().Select(device => new
                {
                    device.DeviceId,
                    nativeDisplayName = device.DisplayName,
                    displayName = preferences.DeviceNames.TryGetValue(device.DeviceId, out var alias) ? alias : device.DisplayName,
                    device.Family,
                    device.VendorId,
                    device.ProductId,
                    device.IsWireless,
                    device.SupportsStandardGamepad,
                    device.ButtonCount,
                    device.AxisCount,
                    device.SwitchCount,
                    isDefault = string.Equals(preferences.DefaultDeviceId, device.DeviceId, StringComparison.Ordinal),
                    defaultProfileId = preferences.DefaultProfileByDevice.TryGetValue(device.DeviceId, out var profileId)
                        ? profileId
                        : (Guid?)null
                }).ToArray();
            }

            case "controllerHub:readState":
            {
                var deviceId = RequireString(payload, "deviceId");
                if (!_service.TryReadRawState(deviceId, out var snapshot) || snapshot is null)
                    throw new InvalidOperationException("The controller is no longer available.");

                return new
                {
                    snapshot.DeviceId,
                    snapshot.Timestamp,
                    standardControls = snapshot.StandardControls.ToDictionary(
                        pair => pair.Key.ToString(),
                        pair => pair.Value,
                        StringComparer.Ordinal),
                    snapshot.RawButtons,
                    snapshot.RawAxes,
                    snapshot.RawSwitches
                };
            }

            case "controllerHub:listProfiles":
                return await _service.LoadProfilesDetailedAsync(cancellationToken);

            case "controllerHub:createProfile":
                return await _service.CreateProfileAsync(
                    ReadOptionalString(payload, "deviceId"),
                    ReadOptionalString(payload, "name"),
                    cancellationToken);

            case "controllerHub:duplicateProfile":
                return await _service.DuplicateProfileAsync(
                    RequireGuid(payload, "profileId"),
                    ReadOptionalString(payload, "name"),
                    cancellationToken);

            case "controllerHub:deleteProfile":
            {
                var profileId = RequireGuid(payload, "profileId");
                await _service.DeleteProfileAsync(profileId, cancellationToken);
                return new { profileId };
            }

            case "controllerHub:exportProfile":
            {
                var profileId = RequireGuid(payload, "profileId");
                var json = await _service.ExportProfileJsonAsync(profileId, cancellationToken);
                return new { profileId, json };
            }

            case "controllerHub:importProfile":
            {
                var json = RequireString(payload, "json");
                var preserveId = ReadBoolean(payload, "preserveId");
                return await _service.ImportProfileJsonAsync(json, preserveId, cancellationToken);
            }

            case "controllerHub:validateProfile":
            {
                var profile = ReadProfile(payload);
                return new
                {
                    errors = _service.GetProfileValidationErrors(profile),
                    conflicts = ControllerMappingAnalyzer.Analyze(profile)
                };
            }

            case "controllerHub:analyzeProfile":
            {
                var profile = ReadProfile(payload);
                return new { conflicts = ControllerMappingAnalyzer.Analyze(profile) };
            }

            case "controllerHub:getPreferences":
                return await _service.LoadPreferencesAsync(cancellationToken);

            case "controllerHub:setDeviceName":
            {
                var deviceId = RequireString(payload, "deviceId");
                var name = ReadOptionalString(payload, "name");
                await _service.SetDeviceNameAsync(deviceId, name, cancellationToken);
                return new { deviceId, name };
            }

            case "controllerHub:setDefaultDevice":
            {
                var deviceId = ReadOptionalString(payload, "deviceId");
                await _service.SetDefaultDeviceAsync(deviceId, cancellationToken);
                return new { deviceId };
            }

            case "controllerHub:setDefaultProfile":
            {
                var deviceId = RequireString(payload, "deviceId");
                var profileId = ReadOptionalGuid(payload, "profileId");
                await _service.SetDefaultProfileAsync(deviceId, profileId, cancellationToken);
                return new { deviceId, profileId };
            }

            case "controllerHub:getAdvancedFeatureStatus":
                return _service.GetAdvancedFeatureBackendStatus();

            case "controllerHub:installAdvancedFeatures":
                return await _service.InstallAdvancedFeatureBackendAsync(cancellationToken);

            case "controllerHub:removeAdvancedFeatures":
                await _service.RemoveAdvancedFeatureBackendAsync(cancellationToken);
                return _service.GetAdvancedFeatureBackendStatus();

            case "controllerHub:listAdvancedDevices":
                return _service.GetAdvancedFeatureDevices();

            case "controllerHub:readAdvancedState":
            {
                var backendId = RequireString(payload, "backendId");
                if (!_service.TryReadAdvancedState(backendId, out var state) || state is null)
                    throw new InvalidOperationException("Advanced controller state is not available for this device.");
                return state;
            }

            case "controllerHub:setProfileAdvancedDevice":
                return await _service.SetProfileAdvancedDeviceAsync(
                    RequireGuid(payload, "profileId"),
                    ReadOptionalString(payload, "backendId"),
                    cancellationToken);

            case "controllerHub:rumble":
            {
                var ok = _service.TryRumbleAdvancedDevice(
                    RequireString(payload, "backendId"),
                    RequireUnitDouble(payload, "lowFrequency"),
                    RequireUnitDouble(payload, "highFrequency"),
                    TimeSpan.FromMilliseconds(ReadInt32(payload, "durationMs") ?? 250));
                return new { ok };
            }

            case "controllerHub:rumbleTriggers":
            {
                var ok = _service.TryRumbleAdvancedTriggers(
                    RequireString(payload, "backendId"),
                    RequireUnitDouble(payload, "left"),
                    RequireUnitDouble(payload, "right"),
                    TimeSpan.FromMilliseconds(ReadInt32(payload, "durationMs") ?? 250));
                return new { ok };
            }

            case "controllerHub:setLed":
            {
                var ok = _service.TrySetAdvancedDeviceLed(
                    RequireString(payload, "backendId"),
                    RequireByte(payload, "red"),
                    RequireByte(payload, "green"),
                    RequireByte(payload, "blue"));
                return new { ok };
            }

            case "controllerHub:setPlayerLed":
            {
                var playerIndex = ReadInt32(payload, "playerIndex")
                    ?? throw new ArgumentException("'playerIndex' is required.");
                var ok = _service.TrySetAdvancedDevicePlayerLed(RequireString(payload, "backendId"), playerIndex);
                return new { ok };
            }

            case "controllerHub:setAdaptiveTriggers":
            {
                var backendId = RequireString(payload, "backendId");
                var state = new ControllerAdaptiveTriggerState(
                    ReadAdaptiveTrigger(payload, "left"),
                    ReadAdaptiveTrigger(payload, "right"));
                return new { ok = _service.TrySetAdvancedAdaptiveTriggers(backendId, state) };
            }

            case "controllerHub:getOutputStatus":
                return _service.GetOutputSessionStatus();

            case "controllerHub:installOutputProvider":
                return await _service.InstallOutputProviderPackageAsync(cancellationToken);

            case "controllerHub:installOutputDriver":
                await _service.InstallOutputDriverAsync(cancellationToken);
                return _service.GetOutputSessionStatus();

            case "controllerHub:repairOutputBackend":
                await _service.RepairOutputBackendAsync(cancellationToken);
                return _service.GetOutputSessionStatus();

            case "controllerHub:removeOutputBackend":
            {
                var removePackage = ReadBoolean(payload, "removeProviderPackage");
                await _service.RemoveOutputBackendAsync(removePackage, cancellationToken);
                return _service.GetOutputSessionStatus();
            }

            case "controllerHub:startOutputSession":
            {
                var deviceId = RequireString(payload, "deviceId");
                var profileId = RequireGuid(payload, "profileId");
                var updateHz = ReadInt32(payload, "updateHz") ?? 250;
                await _service.StartOutputSessionAsync(deviceId, profileId, updateHz, cancellationToken);
                return _service.GetOutputSessionStatus();
            }

            case "controllerHub:stopOutputSession":
                await _service.StopOutputSessionAsync();
                return _service.GetOutputSessionStatus();

            default:
                throw new ArgumentException($"Unknown Controller Hub command '{command}'.");
        }
    }

    private ControllerProfile ReadProfile(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("profile", out var profileElement))
            throw new ArgumentException("A profile payload is required.");

        return JsonSerializer.Deserialize<ControllerProfile>(profileElement.GetRawText(), _jsonOptions)
            ?? throw new ArgumentException("The profile payload could not be read.");
    }

    private static ControllerAdaptiveTriggerEffect? ReadAdaptiveTrigger(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"'{propertyName}' must be an object or null.");

        var modeText = ReadOptionalString(element, "mode") ?? "Off";
        if (!Enum.TryParse<ControllerAdaptiveTriggerMode>(modeText, ignoreCase: true, out var mode))
            throw new ArgumentException($"'{propertyName}.mode' must be Off, Resistance, or Vibration.");

        return new ControllerAdaptiveTriggerEffect(
            mode,
            ReadUnitDouble(element, "startPosition") ?? 0d,
            ReadUnitDouble(element, "strength") ?? 0.5d,
            ReadUnitDouble(element, "frequency") ?? 0.5d);
    }

    private string SerializeResponse(
        string command,
        string? requestId,
        bool ok,
        object? result,
        string? error) =>
        JsonSerializer.Serialize(new
        {
            type = "controllerHub:response",
            requestId,
            command,
            ok,
            result,
            error
        }, _jsonOptions);

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string RequireString(JsonElement payload, string propertyName) =>
        ReadOptionalString(payload, propertyName)
        ?? throw new ArgumentException($"'{propertyName}' is required.");

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Guid RequireGuid(JsonElement payload, string propertyName) =>
        ReadOptionalGuid(payload, propertyName)
        ?? throw new ArgumentException($"'{propertyName}' must be a valid GUID.");

    private static Guid? ReadOptionalGuid(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var parsed))
            throw new ArgumentException($"'{propertyName}' must be a valid GUID or null.");

        return parsed;
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind == JsonValueKind.True;
    }

    private static int? ReadInt32(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw new ArgumentException($"'{propertyName}' must be an integer.");

        return parsed;
    }

    private static double RequireUnitDouble(JsonElement payload, string propertyName) =>
        ReadUnitDouble(payload, propertyName)
        ?? throw new ArgumentException($"'{propertyName}' is required.");

    private static double? ReadUnitDouble(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var parsed) || parsed is < 0d or > 1d)
            throw new ArgumentException($"'{propertyName}' must be a number between 0 and 1.");

        return parsed;
    }

    private static byte RequireByte(JsonElement payload, string propertyName)
    {
        var value = ReadInt32(payload, propertyName)
            ?? throw new ArgumentException($"'{propertyName}' is required.");
        if (value is < byte.MinValue or > byte.MaxValue)
            throw new ArgumentException($"'{propertyName}' must be between 0 and 255.");
        return (byte)value;
    }
}
