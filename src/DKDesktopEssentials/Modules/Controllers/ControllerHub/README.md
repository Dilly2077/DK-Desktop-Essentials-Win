# Controller Hub / Remapping

Status: **foundation implemented; not yet wired into the WebView UI**.

This module is the shared input/remapping engine for DK Desktop Essentials. It is deliberately isolated from the current UI so it can be built and tested before the controller screen is integrated into the application shell.

## Implemented in this slice

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
- Macro references in the profile schema, intentionally delegated to the suite-wide Macro Engine rather than implemented twice.
- Virtual-output abstraction (`IControllerOutputSink`) so a driver backend can be selected later without coupling the mapping engine to one driver.

## Not implemented yet

- Virtual Xbox/PlayStation device creation. The engine can generate mapped state, but `NoOutputSink` is the only sink currently shipped.
- Physical-controller hiding/exclusive mode.
- Gyro, touchpad, adaptive-trigger or controller-specific haptics.
- Calibration/drift graphs and polling-rate diagnostics (Controller Diagnostics module).
- Keyboard/mouse output (Controller -> KB/mouse module).
- Executable detection/profile auto-switching (Automatic Game Profiles module).
- UI/editor integration.

## Virtual-output decision

Do **not** silently install or depend on ViGEmBus. The upstream ViGEmBus project is retired. The module keeps output behind an interface while we evaluate a maintained, signed/user-mode option or a DK-owned driver path.

Any eventual driver installation must be explicit, show publisher/source/version, provide uninstall/recovery, and expose whether virtual output is available. Compatibility with anti-cheat-protected games must never be presented as universal.

## Privacy/security

The implementation performs no DK network requests and requires no account. Device state and profile data remain local. The provider uses Windows' own gaming-input APIs; profile files are readable JSON so users can export, inspect and back them up.

## Next controller-hub slice

1. Add a small native test surface/bridge command so the existing WebView can enumerate controllers and inspect live state without yet redesigning the UI.
2. Add profile validation/import/export and explicit per-device default selection.
3. Vet and implement a virtual-output backend behind `IControllerOutputSink`.
4. Add integration tests for mapping/layer/turbo behaviour before enabling real output.
