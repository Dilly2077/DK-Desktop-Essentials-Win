# Controller Hub / Remapping

Status: **Slices 1-3 implemented; not yet integrated into the visible controller UI**.

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
- `controllerHub:validateProfile` now returns both validation errors and conflicts.
- New `controllerHub:analyzeProfile` bridge command returns the conflict-analysis result without saving the profile.

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

## Not implemented yet

- Virtual Xbox/PlayStation device creation (Slice 4). `NoOutputSink` remains the only shipped sink.
- Physical-controller hiding/exclusive mode.
- Gyro, touchpad, adaptive-trigger or controller-specific haptics (Slice 5).
- Reliability/compatibility hardening beyond the current safe defaults (Slice 6).
- Full module test harness and completion pass (Slice 7).
- Calibration/drift graphs and polling-rate diagnostics (separate Controller Diagnostics module).
- Keyboard/mouse output (separate Controller -> KB/mouse module).
- Executable detection/profile auto-switching (separate Automatic Game Profiles module).
- Visible UI/editor integration.

## Virtual-output decision

Do **not** silently install or depend on ViGEmBus. The upstream ViGEmBus project is retired. The module keeps output behind an interface while a maintained, signed/user-mode option or DK-owned driver path is evaluated.

Any eventual driver installation must be explicit, show publisher/source/version, provide uninstall/recovery, and expose whether virtual output is available. Compatibility with anti-cheat-protected games must never be presented as universal.

## Privacy/security

The implementation performs no DK network requests and requires no account. Device state, aliases, defaults and profile data remain local. The provider uses Windows gaming-input APIs; profile files are readable JSON so users can inspect, export and back them up.

## Next controller-hub slice

Slice 4: Virtual Controller Output.
