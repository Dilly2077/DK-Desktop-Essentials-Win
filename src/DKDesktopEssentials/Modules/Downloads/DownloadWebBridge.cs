using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;

namespace DKDesktopEssentials.Modules.Downloads;

public sealed class DownloadWebBridge
{
    private readonly IDownloadCoordinator _coordinator;
    private readonly Func<Task<string?>> _chooseDirectory;
    private readonly Dictionary<string, string> _destinations = new();
    private bool _picking;
    public bool UpdatesRequested { get; private set; }
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() } };

    public DownloadWebBridge(IDownloadCoordinator coordinator, Func<Task<string?>> chooseDirectory)
    { _coordinator = coordinator; _chooseDirectory = chooseDirectory; }

    public async Task<string?> HandleAsync(string message, string origin, CancellationToken token = default)
    {
        if (!ControllerHubOriginPolicy.IsTrustedDocumentUri(origin) || string.IsNullOrWhiteSpace(message) || message.Length > 32 * 1024) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(message); } catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || !type.GetString()!.StartsWith("downloads:", StringComparison.Ordinal)) return null;
            var command = type.GetString()!;
            if (command.Length is <= 10 or > 80 || command[10..].Any(c => !char.IsAsciiLetter(c))) return null;
            var id = root.TryGetProperty("requestId", out var requestId) && requestId.ValueKind == JsonValueKind.String
                ? requestId.GetString() : null;
            if (id is null || id.Length is 0 or > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) return null;
            var payload = root.TryGetProperty("payload", out var p) ? p : default;
            try
            {
                object? result;
                switch (command)
                {
                    case "downloads:capabilities":
                        var available = _coordinator is not DownloadCoordinator native || !native.RecoveryBlocked;
                        result = new { protocolVersion = 1, mode = "native", available, browseDestination = available, openFolder = available,
                            start = available, pause = available, resume = available, retry = available, cancel = available, remove = available,
                            enqueue = available, events = true, protectedDestinations = true,
                            safeMessage = available ? null : "Local queue data is unavailable or another instance owns it. Downloads are blocked to protect retained files." };
                        break;
                    case "downloads:subscribe": UpdatesRequested = payload.GetProperty("enabled").GetBoolean(); result = new { enabled = UpdatesRequested }; break;
                    case "downloads:chooseFolder":
                    case "downloads:chooseDestination":
                        if (_picking) throw new DownloadEngineException(DownloadErrorCode.EngineUnavailable);
                        _picking = true;
                        string? path;
                        try { path = await _chooseDirectory(); }
                        finally { _picking = false; }
                        if (path is null) { result = null; break; }
                        using (DownloadStagingFile.LockDirectory(path)) { }
                        var grant = Guid.NewGuid().ToString("N");
                        lock (_destinations)
                        {
                            if (_destinations.Count >= 32) _destinations.Clear();
                            _destinations.Add(grant, path);
                        }
                        result = new { destinationToken = grant, displayName = new DirectoryInfo(path).Name };
                        break;
                    case "downloads:enqueue":
                        var destinationToken = Text(payload, "destinationToken", 64);
                        string destination;
                        lock (_destinations)
                            if (!_destinations.TryGetValue(destinationToken, out destination!))
                                throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
                        if (!Uri.TryCreate(Text(payload, "url", 16 * 1024), UriKind.Absolute, out var url))
                            throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
                        var filename = Text(payload, "fileName", 255);
                        var hash = payload.TryGetProperty("expectedSha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                        var segments = payload.TryGetProperty("parallelSegments", out var s) ? s.GetInt32() : 1;
                        var priority = DownloadPriority.Normal;
                        if (payload.TryGetProperty("priority", out var level) &&
                            (level.ValueKind != JsonValueKind.String || !Enum.TryParse(level.GetString(), out priority) || !Enum.IsDefined(priority)))
                            throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
                        result = await _coordinator.EnqueueAsync(new(url, destination, filename, Priority: priority, ExpectedSha256: string.IsNullOrWhiteSpace(hash) ? null : hash,
                            ParallelSegments: segments, StartNow: !payload.TryGetProperty("startNow", out var startNow) || startNow.GetBoolean()), token);
                        break;
                    case "downloads:list": result = await _coordinator.ListAsync(token); break;
                    case "downloads:get": result = await _coordinator.GetAsync(JobId(payload), token); break;
                    case "downloads:cancel": result = await _coordinator.CancelAsync(JobId(payload), token); break;
                    case "downloads:pause": result = await _coordinator.PauseAsync(JobId(payload), token); break;
                    case "downloads:resume": result = await _coordinator.ResumeAsync(JobId(payload), token); break;
                    case "downloads:start": result = await _coordinator.StartAsync(JobId(payload), token); break;
                    case "downloads:retry": result = await _coordinator.RetryAsync(JobId(payload), token); break;
                    case "downloads:remove": await _coordinator.RemoveAsync(JobId(payload), token); result = null; break;
                    case "downloads:openFolder": await _coordinator.OpenFolderAsync(JobId(payload), token); result = null; break;
                    case "downloads:getSettings": result = await _coordinator.GetSettingsAsync(token); break;
                    case "downloads:setSettings":
                        var settings = new DownloadQueueSettings(payload.GetProperty("maxConcurrentDownloads").GetInt32(),
                            payload.GetProperty("maxAttempts").GetInt32(), payload.GetProperty("autoStartOnLaunch").GetBoolean());
                        DownloadQueuePolicy.Validate(settings);
                        await _coordinator.SetSettingsAsync(settings, token); result = await _coordinator.GetSettingsAsync(token); break;
                    case "downloads:status": result = new { recoveryBlocked = _coordinator is DownloadCoordinator c && c.RecoveryBlocked }; break;
                    default: throw new NotSupportedException();
                }
                return Reply(command, id, true, result, null, null);
            }
            catch (DownloadEngineException ex) { return Reply(command, id, false, null, ex.Message, ex.Code); }
            catch (NotSupportedException) { return Reply(command, id, false, null, "This action is not available in this version.", null); }
            catch (OperationCanceledException) { return Reply(command, id, false, null, "The operation was cancelled.", null); }
            catch { return Reply(command, id, false, null, "The download request could not be completed.", DownloadErrorCode.UnexpectedFailure); }
        }
    }

    private string Reply(string command, string id, bool ok, object? result, string? error, DownloadErrorCode? errorCode)
        => JsonSerializer.Serialize(new { type = "downloads:response", command, requestId = id, ok, result, error, errorCode }, _json);
    private static string Text(JsonElement payload, string name, int limit)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > limit)
            throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
        return value.GetString()!;
    }
    private static Guid JobId(JsonElement payload) => Guid.TryParse(Text(payload, "id", 40), out var id)
        ? id : throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
}
