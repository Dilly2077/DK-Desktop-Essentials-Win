# Module 1 discovery compatibility fix — 31 October 2026

## Evidence and scope

On this Windows 10.0.26300 host, PnP lists a DualSense and a HID game controller, while `Windows.Gaming.Input.RawGameController.RawGameControllers` remains empty. Immediate sampling, waiting five seconds, a foreground WPF diagnostic, and starting the installed GameInput service did not make WGI enumerate it. The exact OS/backend reason remains undetermined. The independent Windows `joyGetPosEx` API successfully reads a joystick; the application's exclusive reliance on WGI caused it to report no devices despite available Windows input.

The fix adds a built-in Windows multimedia joystick fallback when WGI has no tracked devices. No SDL download, provider installation, telemetry, hiding/filter changes or output/feedback calls are involved. Normal WGI devices retain their existing identity and output behavior. The provider also reconciles WGI's asynchronous list on refresh; the discovery command waits up to three seconds instead of immediately recording absence.

## Limits and safety

Fallback devices expose generic raw buttons, signed axes and POV positions, with backend/capability metadata. WinMM manufacturer/product numbers are not treated as USB VID/PID. Layout, transport and physical identity are not guessed. Connection type is explicitly unknown; the legacy `IsWireless` boolean must not be interpreted as verified wired transport for these devices. WGI appearing disables the fallback to avoid duplicates. WinMM discovery/disconnect is polling-based through list/read calls, not a replacement hot-plug event service.

IDs are scoped to the observed connection/provider lifetime and change after an observed disconnect/reset. WinMM slots cannot prove physical identity or distinguish a fast swap between identical devices. Both virtual-output entry points therefore reject this inspection-only backend. Persistent device bindings and automatic output reconnect are not certified for fallback devices. Standardized DualSense layout, advanced features and virtual output remain separate work; no claim is made that this repairs WGI itself.

## Validation

- Implemented and locally automated-tested: 49 completion + 9 reliability tests, all passing. Four new cases cover raw conversion/ABI, optional axes/ranges, unplug/reconnect identity invalidation and real service rejection of fallback virtual output. Existing published-helper/security cases still pass.
- Limited physical smoke: discovery succeeds, with 15 raw buttons, six axes and one POV hat. A 30-second observation captured 1,924 readable samples and 24 raw-state changes. The driver identifies itself as `Microsoft PC-joystick driver`; the DualSense model is known from the separate Windows device inventory, not inferred from that generic API name. Reports remain ignored local artifacts and retain `physicalHardwareValidated: false`.
- Native diagnostics did not install/remove drivers, issue feedback, change controller visibility or change service startup configuration. A temporary manual service-start experiment did not restore WGI; rollback results are recorded locally.
- Remaining backend: investigate the host WGI incompatibility and a stable-identity backend for full output support. Remaining UI: Controller Hub device/editor pages and explicit fallback capability display. Remaining manual acceptance: button/axis layout comparison, reconnect/transport/multiple-device testing, driver/game behavior and release validation. This is not a release-ready declaration.

References: [WGI list initialization](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller.rawgamecontrollers), [Windows joystick reads](https://learn.microsoft.com/en-us/windows/win32/api/joystickapi/nf-joystickapi-joygetposex), [joystick capabilities](https://learn.microsoft.com/en-us/windows/win32/api/joystickapi/ns-joystickapi-joycapsw).
