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
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or JsonException)
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
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("profile", out var profileElement))
                    throw new ArgumentException("A profile payload is required.");

                var profile = JsonSerializer.Deserialize<ControllerProfile>(profileElement.GetRawText(), _jsonOptions)
                    ?? throw new ArgumentException("The profile payload could not be read.");
                return new { errors = _service.GetProfileValidationErrors(profile) };
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

            default:
                throw new ArgumentException($"Unknown Controller Hub command '{command}'.");
        }
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
}
