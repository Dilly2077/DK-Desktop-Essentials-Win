# Module 1 Controller Hub — static code, security and privacy audit

Date: 2026-10-14 (UK). Reviewed main commit: `41899eeee2a9fb22bc31f1ecfdc4aeb416312df7`.

## Scope and limitations

Manual source review of the .NET 8 WPF/WebView2 host, Controller Hub module, optional SDL3 provider, HIDMaestro package/virtual-output helper, local JSON persistence, repository workflows, and Slice 6/7 automated-test source. This is **not** a penetration test, malware scan, physical device certification, independent binary analysis, or proof of exploitability. No device/driver was installed or tested during this review, no dynamic Windows execution was carried out, and no working code was changed.

Findings below distinguish a directly demonstrable risky code pattern from an attack that would require additional preconditions.

## Findings (sorted by priority)

### SEC-01 — HIGH: Unprivileged SDK DLL loaded by elevated maintenance process
- **Evidence:** `HidMaestroVirtualOutput.cs` uses a provider directory under `%LOCALAPPDATA%\DKDesktopEssentials\Controllers\Providers\HIDMaestro\1.8.0` (approximately lines 28–40), extracts `HIDMaestro.Core.dll` there (approximately 78–100), and launches `DKControllerOutputHost.exe --mode install/cleanup/remove --sdk <that path>` using `Verb="runas"` (approximately 146–162). `src/DKControllerOutputHost/Program.cs` loads the supplied DLL through `Assembly.LoadFrom` before executing those modes (lines 15–23, 136–143).
- **Risk:** The release archive is SHA-256 checked at download time, but the DLL remains writable by the current user and is not reverified by the elevated process. A local attacker able to replace that file could cause modified code to run **if the user later authorizes elevation**. This is an elevated code-loading trust-boundary issue, not a claim of bypassing the UAC prompt.
- **Remediation:** Prefer a minimal, independently packaged/elevated driver-maintenance executable that never loads arbitrary user-writable assemblies. If the upstream SDK must execute elevated, stage it in an admin-protected location under a trusted installer and validate immutable known-good content **inside** the elevated trust boundary before loading. Do not rely on a mutable `source.json` manifest or on verification performed solely by the non-elevated caller.
- **Tests:** Tamper with the user-local SDK after installation and confirm elevated maintenance refuses to execute it; test path substitution, races and rollback.

### SEC-02 — HIGH, defence-in-depth: Native WebView commands lack full origin checks
- **Evidence:** `MainWindow.xaml.cs` navigation handling allows any URI whose **host** equals `dk.local` without constraining the scheme to `https` (approximately lines 63–70). `OnWebMessageReceived` does not validate `e.Source` before invoking privileged `ControllerHubWebBridge.HandleAsync` (approximately lines 95–110). The bridge exposes installation, driver repair/removal, native feedback and sessions (`ControllerHubWebBridge.cs`, approximately lines 190–300).
- **Risk:** An unintended page able to reach the WebView, including a same-host but different-scheme address, could potentially call native operations. A working exploit route from the current bundled UI has **not** been demonstrated. Host-only navigation checking and absent origin checks nonetheless leave a sensitive boundary needlessly weak.
- **Remediation:** Allow only the exact trusted `https://dk.local` origin and expected routes; validate the source of **every** web message; restrict frame navigation; consider blocking all network/subresource requests except the mapped local UI, with optional downloads handled by the existing native package manager. Require native confirmation for destructive or elevated operations. Consider a restrictive CSP for the local document.
- **Tests:** Negative tests for external origin, `http://dk.local`, `file:` URLs, injected frames, malformed JSON and unauthorised maintenance commands.

### REL-01 — HIGH: Asynchronous message-handler exception can terminate WPF
- **Evidence:** `MainWindow.xaml.cs` has `async void OnWebMessageReceived` and awaits `_controllerHubBridge.HandleAsync(message)` without a top-level catch (approximately lines 95–112). The bridge catch filter is limited to certain exceptions (`ControllerHubWebBridge.cs`, approximately lines 54–61). Installing providers calls `HttpClient.GetAsync`, which can throw `HttpRequestException` or cancellation-related exceptions (`HidMaestroVirtualOutput.cs` around line 77; `Sdl3AdvancedControllerProvider.cs` around line 52), neither covered by that filter.
- **Risk:** A failed network download (e.g. offline/timeout) or unexpected backend exception can escape the `async void` event callback and cause an unhandled WPF dispatcher exception / app termination.
- **Remediation:** Catch and translate exceptions at the event-handler boundary; distinguish cancellations, network failures and fatal errors; keep the UI alive and return a structured error response tied to the command/request ID. Avoid exposing exception internals to UI/exports in release builds.
- **Tests:** Offline provider installation, cancelled request, failing fake backend and controlled IO/network exceptions must return errors without crashing.

### SAFE-01 — MEDIUM–HIGH: Driver repair/removal uses global upstream cleanup
- **Evidence:** `HidMaestroPackageManager.RepairAsync/RemoveDriverAsync` invoke helper `cleanup/remove` (`HidMaestroVirtualOutput.cs`, approximately lines 125–145). `Program.cs` uses `HMContext.RemoveAllVirtualControllers(bool)` (lines 35–42, 155–164) with no DK-specific ownership filtering.
- **Risk:** Using the repair/uninstall feature may remove devices belonging to other HIDMaestro clients, not just DK Desktop Essentials. The acceptance document already warns that maintenance is global.
- **Remediation:** Prefer scoped cleanup for DK-owned virtual identities, if supported by upstream; otherwise label maintenance explicitly as **global**, require secondary confirmation, list likely impact and provide manual recovery/rollback. Never execute it automatically in a reconnect loop.
- **Tests:** Exercise maintenance alongside another client's virtual controller in a controlled test environment.

### PRIV-01 — MEDIUM: Exported profiles can reveal controller identifiers
- **Evidence:** `ControllerProfile.cs` persists `DeviceMatch.DeviceId` and `AdvancedDeviceId`; the SDL backend constructs the latter from serial number, device path, or instance ID (`Sdl3AdvancedControllerProvider.cs`, `BuildBackendId`, approximately lines 489–498). `ControllerProfileStore.ExportJsonAsync` serializes the full object (approximately lines 132–138); `ControllerHubWebBridge` returns that JSON to the local UI.
- **Risk:** Shared/exported profiles can contain stable device IDs, serials or physical device paths. This is a user-mediated **information disclosure**, not evidence of automatic telemetry.
- **Remediation:** Add `Export portable / remove device identifiers` as the default and explicit opt-in for device-bound exports. Warn before sharing; consider a local-only hashed identifier for display/reporting while preserving safe controller identity matching.
- **Tests:** Assert portable exports omit both `DeviceMatch.DeviceId` and `AdvancedDeviceId`, and imports still work without silently relinking to a different physical controller.

### SEC-03 — MEDIUM: Installed binary trust is not rechecked at runtime
- **Evidence:** `HidMaestroPackageManager.GetStatus` checks only file existence; `Sdl3PackageManager.GetStatus` likewise. SDL loads the installed native DLL via `NativeLibrary.Load` (`Sdl3AdvancedControllerProvider.cs`, approximately lines 335–350).
- **Risk:** The validated package can be replaced or corrupted after installation. This is especially significant for SEC-01; the SDL library normally operates at the same privilege level as the user.
- **Remediation:** Record and verify a fixed, independently trusted **extracted-file** digest before SDK load; fail closed on tampering, surface reinstall UI. For privileged execution, apply SEC-01's stronger trust-boundary architecture rather than relying on a user-mode hash check alone.
- **Tests:** Replace the installed library with a different file after installation; confirm it is refused.

### REL-02 — MEDIUM: Real-device and negative-security tests are still absent
- **Evidence:** `docs/controller-hub-completion.md` describes 32 completion tests + 9 reliability tests and test-only fake HIDMaestro SDK; `docs/controller-hub-acceptance.md` marks every physical acceptance case `Not run`. CI runs both managed suites and validates the bundle.
- **Risk:** Real-driver reflection/API behaviour, elevation, SDL motion/touch, driver failure, anti-cheat, USB/Bluetooth reconnect and two identical physical controllers remain unknown. CI success must not be represented as hardware certification.
- **Remediation:** Complete documented hardware acceptance and add negative tests for SEC-01, SEC-02, REL-01, SAFE-01, PRIV-01; record pass/fail alongside driver, model and transport versions.
- **Tests:** Follow `docs/controller-hub-acceptance.md`; do not use the fake SDK for acceptance.

### HARD-01 — LOW–MEDIUM: Web request and dependency hardening
- **Evidence:** `MainWindow.xaml.cs` denies outside navigation but does not enforce an explicit denylist for external `fetch`/subresources; the current `index.html` references only bundled files. `.github/workflows/build-windows.yml` uses major-version GitHub Actions tags rather than immutable commit SHAs.
- **Risk:** Future UI extensions can inadvertently add network access; supply-chain provenance can be improved for CI actions. Neither is evidence of current telemetry.
- **Remediation:** Add restrictive local document CSP and network-denial rules; pin GitHub Actions to immutable commits; add dependency and license checks before releases.

## Verified positives

1. Local profiles/preferences, mapping, per-device sessions and provider configuration are designed to operate without accounts, advertising or DK telemetry. The **current active** HTML entrypoint imports local `app2.js` and other local assets; no external scripts or auto-upload were identified in the reviewed application shell.
2. Both third-party downloads are **explicit**, release-pinned and archive SHA-256 verified. On 2026-10-14 the GitHub release-asset metadata independently matched the embedded digests:
   - SDL3 3.4.16 Windows x64: `4217944b4e51457af4a59c82d883f8443b3e65964b2acd8943484c492756c4b6` — [official release](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.16).
   - HIDMaestro v1.8.0: `1e5f5019c20e4be8f922c7aa5a86ee87eb01f7aa851fe38daea14d0ce4fd8240` — [official release](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.8.0).
3. User approval/UAC is requested for driver maintenance; no hiding/filter driver is silently installed.
4. Sessions are isolated per physical ID, output has neutralization and finite retries, feedback levels are validated, and rumble duration is capped.
5. Windows CI succeeded on the reviewed commit with both test suites and release-package verification. This does not establish actual physical-device or driver compatibility.

## Recommended issue order

1. **Block user-facing driver maintenance until SEC-01 and SAFE-01 are addressed** and reviewed.
2. Fix **REL-01** and **SEC-02** as a focused native bridge hardening change, with negative tests.
3. Fix **PRIV-01** before adding UI profile sharing/export affordances.
4. Improve **SEC-03** and **HARD-01** while packaging the release.
5. Complete **REL-02** hardware acceptance, including another virtual-device client's cleanup safety, before any production-ready claim.

## Status / next work

- **Audit:** Complete as static source review; findings are not remediated by this documentation-only commit.
- **Module 1 backend:** Slices 1–7 merged, automated CI green at audited commit.
- **UI:** Not implemented beyond the local bridge/application shell.
- **Hardware/game testing:** Pending; every acceptance row was untested at review.
- **Outstanding work:** Fix the findings above, implement UI/editor, complete hardware acceptance and verify release behaviour. Maintain the project's local-first/no-telemetry architecture throughout.
