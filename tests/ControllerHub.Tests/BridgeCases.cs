using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class BridgeCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Bridge handles correlated profile CRUD and persistent aliases/defaults", Profiles),
        ("Bridge rejects malformed commands and invalid profiles predictably", Errors),
        ("Native origin policy refuses non-local senders and URLs", OriginPolicy),
        ("Bridge reports unsupported advanced effects instead of false success", Features),
        ("Bridge drives two independent profile-to-mapping-to-output sessions", Sessions),
        ("Missing advanced input neutralizes output and resumes only after release", AdvancedDisconnect)
    };
    private static async Task Profiles()
    {
        await using var f = new Fixture(); f.Input.Set("a", 0);
        var created = await f.Command("createProfile", new { deviceId = "a", name = "Bridge profile" });
        var id = created.GetProperty("result").GetProperty("id").GetGuid();
        await f.Command("setDeviceName", new { deviceId = "a", name = "Living room" });
        await f.Command("setDefaultDevice", new { deviceId = "a" }); await f.Command("setDefaultProfile", new { deviceId = "a", profileId = id });
        var devices = (await f.Command("listDevices")).GetProperty("result");
        Check(devices[0].GetProperty("displayName").GetString() == "Living room" && devices[0].GetProperty("isDefault").GetBoolean(), "Bridge preferences not reflected");
        // Native device IDs and SDL serial/path identifiers must not leak by default.
        var stored = (await f.Profiles.LoadAsync(id))!;
        stored.AdvancedDeviceId = "sdl:054c:0ce6:serial:private-controller-serial";
        await f.Profiles.SaveAsync(stored);
        var exported = (await f.Command("exportProfile", new { profileId = id })).GetProperty("result").GetProperty("json").GetString();
        using (var portable = System.Text.Json.JsonDocument.Parse(exported!))
        {
            var root = portable.RootElement;
            Check(root.GetProperty("deviceMatch").GetProperty("deviceId").ValueKind == System.Text.Json.JsonValueKind.Null,
                "Portable profile export leaked the Windows device ID");
            Check(root.GetProperty("advancedDeviceId").ValueKind == System.Text.Json.JsonValueKind.Null,
                "Portable profile export leaked the SDL serial/path device identifier");
        }
        var boundExport = (await f.Command("exportProfile", new { profileId = id, includeDeviceIdentifiers = true }))
            .GetProperty("result").GetProperty("json").GetString();
        using (var bound = System.Text.Json.JsonDocument.Parse(boundExport!))
        {
            Check(bound.RootElement.GetProperty("deviceMatch").GetProperty("deviceId").GetString() == "a",
                "Explicit device-bound export was unexpectedly anonymized");
            Check(bound.RootElement.GetProperty("advancedDeviceId").GetString() == stored.AdvancedDeviceId,
                "Explicit device-bound export did not preserve advanced controller identity");
        }
        Check((await f.Profiles.LoadAsync(id))!.AdvancedDeviceId == stored.AdvancedDeviceId,
            "Portable export unexpectedly altered stored profile");
        var imported = (await f.Command("importProfile", new { json = exported, preserveId = true })).GetProperty("result").GetProperty("id").GetGuid();
        Check(imported != id, "Bridge import overwrote profile");
        await f.Command("duplicateProfile", new { profileId = id, name = "Duplicate" });
        await f.Command("deleteProfile", new { profileId = id });
        Check((await f.Service.LoadPreferencesAsync()).DefaultProfileByDevice.Count == 0, "Deleted profile remained default");
    }
    private static async Task Errors()
    {
        await using var f = new Fixture();
        foreach (var input in new[] { "", "[1]", "{bad", "{\"type\":\"other:command\"}" })
            Check(await f.Bridge.HandleAsync(input) is null, "Unrelated/malformed message consumed");
        await f.Command("unknown", ok: false); await f.Command("readState", new { deviceId = "absent" }, ok: false);
        var globalCleanup = await f.Command("removeOutputBackend", ok: false);
        Check(globalCleanup.GetProperty("error").GetString()!.Contains("global-cleanup"),
            "Global driver cleanup was not blocked without explicit acknowledgment");
        await f.Command("repairOutputBackend", ok: false);
        await f.Command("startOutputSession", new { deviceId = "a", profileId = "invalid" }, ok: false);
        await f.Command("importProfile", new { json = "{\"layers\":null}" }, ok: false);
        var invalid = new ControllerProfile { Layers = null! };
        var validated = (await f.Command("validateProfile", new { profile = invalid })).GetProperty("result");
        Check(validated.GetProperty("errors").GetArrayLength() > 0 && validated.GetProperty("conflicts").GetArrayLength() > 0, "Invalid profile was not explained");
        f.Input.EnumerationFailure = new HttpRequestException("Sensitive internal URL");
        var offline = await f.Command("listDevices", ok: false);
        Check(!offline.GetProperty("error").GetString()!.Contains("Sensitive internal URL"),
            "Network exception leaked a sensitive internal URL");
        f.Input.EnumerationFailure = new NullReferenceException("Private device context");
        var unexpected = await f.Command("listDevices", ok: false);
        Check(!unexpected.GetProperty("error").GetString()!.Contains("Private device context"),
            "Unexpected native exception details leaked into the UI");
        f.Input.EnumerationFailure = null;

        await f.Service.DisposeAsync();
        await f.Command("startOutputSession", new { deviceId = "a", profileId = Guid.NewGuid() }, ok: false);
    }

    private static Task OriginPolicy()
    {
        Check(ControllerHubOriginPolicy.IsTrustedDocumentUri("https://dk.local/index.html"),
            "Local SPA was refused");
        Check(ControllerHubOriginPolicy.IsTrustedDocumentUri("https://dk.local/index.html#controllers"),
            "Local SPA fragment was refused");
        Check(ControllerHubOriginPolicy.IsTrustedResourceUri("https://dk.local/styles.css"),
            "Local CSS was refused");
        foreach (var rejected in new[] {
            "http://dk.local/index.html", "https://dk.local:444/index.html",
            "https://dk.local.evil.example/index.html", "https://user@dk.local/index.html",
            "file:///C:/temp/index.html", "about:blank", "https://example.com/index.html"
        })
        {
            Check(!ControllerHubOriginPolicy.IsTrustedDocumentUri(rejected),
                "Untrusted document allowed: " + rejected);
            Check(!ControllerHubOriginPolicy.IsTrustedResourceUri(rejected),
                "Untrusted resource allowed: " + rejected);
        }
        Check(!ControllerHubOriginPolicy.IsTrustedDocumentUri("https://dk.local/other.html"),
            "Unexpected top-level page allowed");
        return Task.CompletedTask;
    }

    private static async Task Features()
    {
        await using var f = new Fixture();
        await f.Command("listAdvancedDevices"); await f.Command("readAdvancedState", new { backendId = "advanced-a" });
        var unsupported = await f.Command("rumble", new { backendId = "advanced-a", lowFrequency = 0.5, highFrequency = 0.5, durationMs = 100 });
        Check(!unsupported.GetProperty("result").GetProperty("ok").GetBoolean(), "Unsupported rumble claimed success");
        await f.Command("rumble", new { backendId = "advanced-a", lowFrequency = -1, highFrequency = 0.5 }, ok: false);
        var profile = Profile(); await f.Profiles.SaveAsync(profile);
        await f.Command("setProfileAdvancedDevice", new { profileId = profile.Id, backendId = "advanced-a" });
        Check((await f.Profiles.LoadAsync(profile.Id))!.AdvancedDeviceId == "advanced-a", "Advanced device association not persisted");
        await f.Command("setProfileAdvancedDevice", new { profileId = profile.Id, backendId = "missing" }, ok: false);
    }
    private static async Task Sessions()
    {
        await using var f = new Fixture(); f.Input.Set("a", 0); f.Input.Set("b", 0);
        var profile = Profile(Binding(StandardControl.South, StandardControl.East)); profile.OutputMode = ControllerOutputMode.VirtualXbox360;
        await f.Profiles.SaveAsync(profile);
        await Task.WhenAll(f.Command("startOutputSession", new { deviceId = "a", profileId = profile.Id }),
            f.Command("startOutputSession", new { deviceId = "b", profileId = profile.Id }));
        var a = f.Outputs[ControllerOutputSession.Identity("a", profile.Id)]; var b = f.Outputs[ControllerOutputSession.Identity("b", profile.Id)];
        await Until(() => a.Frames >= 3 && b.Frames >= 3); f.Input.Set("a", 1);
        await Until(() => a.Last[StandardControl.East] == 1);
        Near(0, b.Last[StandardControl.East], "Input leaked to B");
        var status = (await f.Command("getOutputStatus")).GetProperty("result");
        Check(status.GetProperty("sessions").GetArrayLength() == 2 && !status.GetProperty("compatibility").GetProperty("deviceHidingAvailable").GetBoolean(), "Session/compatibility status incorrect");
        await f.Command("stopOutputSession", new { deviceId = 123 }, ok: false);
        Check(!a.Disposed && !b.Disposed, "Malformed stop caused stop-all");
        await f.Command("stopOutputSession", new { deviceId = "a" });
        Check(a.Disposed && !b.Disposed, "Targeted stop leaked to B"); Near(0, a.Last[StandardControl.East], "Stop neutralization");
        await f.Command("stopOutputSession"); Check(b.Disposed, "Stop-all did not stop B");
        Check((await f.Command("getOutputStatus")).GetProperty("result").GetProperty("sessions").GetArrayLength() == 0, "Stopped sessions retained");
    }
    private static async Task AdvancedDisconnect()
    {
        await using var f = new Fixture(); f.Input.Set("a", 0);
        var profile = Profile(Binding(StandardControl.South, StandardControl.East));
        profile.OutputMode = ControllerOutputMode.VirtualXbox360; profile.AdvancedDeviceId = "advanced-a";
        await f.Profiles.SaveAsync(profile); await f.Service.StartOutputSessionAsync("a", profile.Id);
        var output = f.Outputs[ControllerOutputSession.Identity("a", profile.Id)];
        await Until(() => output.Frames >= 3); f.Input.Set("a", 1); await Until(() => output.Last[StandardControl.East] == 1);
        f.Advanced.Connected = false; await Until(() => output.Last[StandardControl.East] == 0);
        var frame = output.Frames;
        f.Advanced.Connected = true; await Until(() => output.Frames >= frame + 3); Near(0, output.Last[StandardControl.East], "Held input replayed after advanced reconnect");
        frame = output.Frames; f.Input.Set("a", 0); await Until(() => output.Frames >= frame + 3);
        f.Input.Set("a", 1); await Until(() => output.Last[StandardControl.East] == 1);
    }
}
