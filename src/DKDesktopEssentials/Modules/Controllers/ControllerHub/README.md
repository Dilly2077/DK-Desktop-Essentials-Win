# Controller Hub / Remapping

Status: **Slices 1-5 implemented; not yet integrated into the visible controller UI**.

This module is the shared input/remapping engine for DK Desktop Essentials. It remains separated from the visible UI while each controller slice is built and tested.

## Slice 1 - Core input and remapping foundation

- Local Windows controller discovery through `Windows.Gaming.Input.RawGameController`.
- Hot-plug add/remove events.
- Vendor/product IDs, display name, wireless state, stable per-app device ID and raw capability counts.
- Standard gamepad readings when Windows can expose the device as a `Gamepad`.
- Raw buttons, axes and switches retained for generic/unrecognised controllers.
- Common controller state model shared by future diagnostics/profile modules.
- Local JSON profiles stored under `%LOCALAPPDATA%\DKDesktopEssentials\Controllers\Profiles`.
- Controller-to-controller remapping rules.
- Pass-through or suppression of original controls.
- Deadzone, anti-deadzone, outer-deadzone, response-curve exponent, scale and inversion tuning.
- Held and toggle shift layers.
- Chord conditions.
- Toggle mappings.
- Turbo mappings with an explicit 1-30 Hz bound.
- Macro references in the profile schema, delegated to the suite-wide Macro Engine.
- Virtual-output abstraction (`IControllerOutputSink`).

## Slice 2 - Controller and profile management

- Local controller aliases without changing the native Windows device name.
- Explicit default-controller selection.
- Per-controller default-profile selection with compatibility validation.
- Profile create, duplicate and delete operations.
- Readable JSON import/export with collision-safe profile IDs.
- Profile validation for schema, deadzones, curves, turbo frequency, layer IDs, binding IDs and target completeness.
- Corrupt/invalid profile reporting: a bad profile is skipped and reported without preventing valid profiles from loading.
- Preferences stored locally under `%LOCALAPPDATA%\DKDesktopEssentials\Controllers\controller-hub.json`.
- Local WebView bridge for controller enumeration and live state reads.
- The bridge also exposes profile list/create/duplicate/delete/import/export/validate and controller preference operations.
- The bridge is wired into the native host, but no visible Controller Hub page has been added yet.

## Slice 3 - Advanced mapping engine

- Backward-compatible advanced profile fields; Slice 1/2 profiles continue to load without a schema migration.
- Nested shift layers through `RequiredLayerIds` and `BlockedLayerIds`.
- Tap mappings with delayed single-tap resolution so a potential double press can be distinguished.
- Hold mappings with configurable hold thresholds.
- Double-press mappings with configurable timing windows.
- Tap/hold/double-press behaviours can still be combined with direct, toggle or turbo binding modes.
- Analog value zones for trigger-style staged actions (for example soft pull vs full pull).
- True paired 2D stick-to-stick transforms with radial deadzone, outer deadzone, curve, scale, rotation and per-axis inversion.
- Directional/radial stick sectors that map stick angle to controller or macro actions.
- Optional source suppression for stick and radial mappings.
- Mapping conflict analysis for missing layer dependencies, dependency cycles, overlapping value zones, overlapping radial sectors, competing bindings and competing stick targets.
- Advanced validation bounds timing values, stick transforms, zone ranges, radial sectors and targets before profiles are saved/imported.
- `controllerHub:validateProfile` returns both validation errors and conflicts.
- `controllerHub:analyzeProfile` returns the conflict-analysis result without saving the profile.

## Slice 4 - Virtual controller output

- Active backend: HIDMaestro, kept behind `IControllerOutputSink` and a separate helper process so it can be replaced without rewriting the mapping engine.
- Xbox 360 output uses HIDMaestro profile `xbox-360-wired`.
- DualShock 4 output uses HIDMaestro profile `dualshock-4-v2`.
- The main app remains .NET 8; `DKControllerOutputHost` is a self-contained .NET 10 helper matching HIDMaestro's current SDK target.
- HIDMaestro is loaded dynamically by the helper; DK Desktop does not compile-link or silently bundle the third-party SDK DLL.
- Explicit provider installation downloads only the pinned official HIDMaestro v1.8.0 GitHub release and verifies SHA-256 `1e5f5019c20e4be8f922c7aa5a86ee87eb01f7aa851fe38daea14d0ce4fd8240` before extracting the SDK DLL.
- Provider provenance is recorded locally in `source.json` under `%LOCALAPPDATA%\DKDesktopEssentials\Controllers\Providers\HIDMaestro\1.8.0`.
- Driver installation is a separate explicit elevation step; there is no silent UAC prompt at startup.
- Repair removes orphan virtual controllers while preserving the installed provider; removal can clean virtual devices/driver packages and optionally delete the locally cached provider DLL.
- A native background output session continuously reads the selected physical controller, maps it through the selected profile and feeds the virtual controller at a clamped 60-1000 Hz update rate (250 Hz default).
- Output sessions have explicit start, stop and status operations; shutdown stops the session and tears down the virtual controller.
- The helper maps standard face/shoulder/stick/menu/view/paddle buttons, d-pad/hat, sticks and triggers into HIDMaestro's abstract gamepad state.
- Stable HIDMaestro identity keys are derived from the DK profile ID so the virtual device identity remains consistent between sessions.
- The Windows release workflow builds and publishes the output helper next to the main application under `controller-output-host`.

## Slice 5 - Advanced controller features

- Advanced physical-controller features are capability-driven through an optional SDL3 backend rather than being inferred solely from controller branding.
- SDL3 v3.4.16 is installed only when explicitly requested from its official GitHub release; SHA-256 `4217944b4e51457af4a59c82d883f8443b3e65964b2acd8943484c492756c4b6` is verified before `SDL3.dll` is extracted.
- Provider provenance is stored locally under `%LOCALAPPDATA%\DKDesktopEssentials\Controllers\Providers\SDL3\3.4.16\source.json`.
- SDL3 is dynamically loaded at runtime, so the normal controller path still works when the optional advanced backend is absent.
- Per-device capability reporting covers gyroscope, accelerometer, touchpad, body rumble, trigger rumble, RGB LED, player LED and DualSense adaptive triggers.
- Gyroscope readings expose pitch/yaw/roll angular velocity; accelerometer readings expose X/Y/Z acceleration.
- Touchpad state retains raw 0..1 coordinates, pressure and contact data for UI/inspection. When touch X/Y is used as a remapping source it is converted to a centred signed axis, with Y oriented like a gamepad stick and zero output when no finger is touching.
- Advanced controls are first-class profile sources, so gyro, accelerometer and touch data can feed ordinary bindings, value zones, chords, 2D stick transforms and radial mappings.
- Profiles explicitly store an `AdvancedDeviceId`; DK Desktop does not guess between multiple identical controllers. Vendor/product compatibility is checked before linking an advanced device to a profile.
- Standard rumble, trigger-rumble, RGB-light and player-LED actions are exposed only when SDL reports the corresponding capability.
- DualSense and DualSense Edge adaptive-trigger output uses SDL's device-specific effect channel with explicit Off, Resistance and Vibration modes. Calls still return failure when the active transport/driver rejects the effect.
- Advanced feature installation/removal, device enumeration, live advanced-state reads, profile linking and hardware effects are exposed through the local WebView bridge.

### Advanced-feature limitations

Capability support depends on the physical controller, connection mode, Windows driver and SDL backend. DK Desktop does not claim that a feature is available merely because a controller family commonly has it. CI verifies compilation and packaging but cannot certify physical gyro, touchpad, rumble, LED or adaptive-trigger behaviour without hardware testing.

### Physical-controller hiding

Physical-device hiding is deliberately **not** automated yet. The maintained HidHide source has continued development, but the latest signed public installer lags the current source and installation/update requires reboot-sensitive filter-driver changes. DK Desktop therefore does not silently add a second system driver or claim double-input prevention is universal. A vetted hiding/exclusive-mode path belongs in Slice 6 compatibility hardening.

### WebView bridge protocol

The local UI can post JSON objects/JSON strings with a `type` beginning `controllerHub:` and an optional `requestId`. Responses are returned as `controllerHub:response` with the same request ID, an `ok` flag, `result`, and an error string when applicable.

Commands currently available:

- `controllerHub:listDevices`
- `controllerHub:readState`
- `controllerHub:listProfiles`
- `controllerHub:createProfile`
- `controllerHub:duplicateProfile`
- `controllerHub:deleteProfile`
- `controllerHub:exportProfile`
- `controllerHub:importProfile`
- `controllerHub:validateProfile`
- `controllerHub:analyzeProfile`
- `controllerHub:getPreferences`
- `controllerHub:setDeviceName`
- `controllerHub:setDefaultDevice`
- `controllerHub:setDefaultProfile`
- `controllerHub:getAdvancedFeatureStatus`
- `controllerHub:installAdvancedFeatures`
- `controllerHub:removeAdvancedFeatures`
- `controllerHub:listAdvancedDevices`
- `controllerHub:readAdvancedState`
- `controllerHub:setProfileAdvancedDevice`
- `controllerHub:rumble`
- `controllerHub:rumbleTriggers`
- `controllerHub:setLed`
- `controllerHub:setPlayerLed`
- `controllerHub:setAdaptiveTriggers`
- `controllerHub:getOutputStatus`
- `controllerHub:installOutputProvider`
- `controllerHub:installOutputDriver`
- `controllerHub:repairOutputBackend`
- `controllerHub:removeOutputBackend`
- `controllerHub:startOutputSession`
- `controllerHub:stopOutputSession`

## Not implemented yet

- Physical-controller hiding/exclusive mode; deferred to Slice 6 for a separately vetted driver/compatibility path.
- Reliability/compatibility hardening beyond the current safe defaults (Slice 6).
- Full module test harness and completion pass (Slice 7).
- Calibration/drift graphs and polling-rate diagnostics (separate Controller Diagnostics module).
- Keyboard/mouse output (separate Controller -> KB/mouse module).
- Executable detection/profile auto-switching (separate Automatic Game Profiles module).
- Visible UI/editor integration.

## Virtual-output decision

Do **not** silently install or depend on ViGEmBus. The upstream ViGEmBus project is retired. Slice 4 uses HIDMaestro v1.8.0 as an optional provider because it is active, MIT-licensed, exposes Xbox/PlayStation profiles and keeps its standard controller path in UMDF2/user mode. Provider installation remains explicit and removable.

Virtual-controller compatibility with anti-cheat-protected games must never be presented as universal. DK Desktop does not attempt to bypass anti-cheat or conceal the fact that a virtual device is being used.

## Privacy/security

The controller engine performs no DK telemetry and requires no account. Device state, aliases, defaults, profiles and provider metadata remain local. Network access happens only when the user explicitly installs an optional provider package: HIDMaestro for virtual output or SDL3 for advanced physical features. Each request goes directly to a pinned official GitHub release and the downloaded package is hash-verified before use.

## Next controller-hub slice

Slice 6: Reliability, safety and compatibility hardening.
