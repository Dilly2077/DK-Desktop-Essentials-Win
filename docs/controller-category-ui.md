# Controller category UI — design, implementation, and acceptance

Branch: feat/controller-ui-chat-2026-10-15  
Implementation: src/DKDesktopEssentials/ui/controller-ui.js and controller-ui.css  
App shell: WPF with WebView2 and the existing Controller Hub message bridge.

## Design

Use the existing Command Center category cards, sidebar and dark navy/blue styling. Controller cards open one shared responsive Controller Hub interface, not detached windows. Contextual right-rail displays selected device, compatibility warnings and privacy information.

- Overview: selected controller, status, generic visual, device rename and quick actions.
- Mappings: direct controller-to-controller inputs, toggle/turbo, import/export, copy profiles and save-as-new validation.
- Tuning: per-axis software deadzone controls and labelled illustrative curve.
- Live input: actual raw device snapshots; digital indicators and analogue bars; polling stops when the tab closes.
- Advanced: verified SDL provider status, optional install/remove, advanced device listing and planned capability panels.
- Virtual output: optional HIDMaestro backend status, explicit installation, session start/stop, clear physical-controller-visibility warning, guarded repair/remove.

Module 2 diagnostics-analysis and calibration are clearly marked upcoming. Modules 3–7 have intentional non-operational UI preview pages, so card navigation has a cohesive layout without pretending their backends exist.

## Technical considerations

Device and profile lists come from the native bridge and never from manufactured production mock data. The frontend does not require a DK account, telemetry service, analytics or third-party browser requests. Downloads are only performed by the native backend when an explicit provider-install action is selected. Privileged maintenance still uses the Windows host confirmation.

The existing public bridge does not expose SaveProfile, so mapping/tuning draft changes are validated and saved as a NEW copy through the existing ImportProfile endpoint; the original is retained. The profile editor currently exposes direct controller-to-controller mappings, turbo/toggle and deadzones. Further controls are needed for advanced existing backend schemas: press behaviours, chords, shift layers, stick mapping, zones, radial menus, touchpad/gyro maps and macro references.

Physical controller hiding is unavailable. Games may detect duplicate physical and virtual input. No anti-cheat circumvention is attempted. Live sampled values are not true measured end-to-end latency and should not be labelled as such.

## Local acceptance checklist

- [ ] Build the Windows app locally and check startup plus all category/tool navigation.
- [ ] Check the empty state on a machine with no physical controller.
- [ ] Check each supported device type that is available on a physical test PC.
- [ ] Verify device rename, create/duplicate/export/import profile, and save-as-new draft editing.
- [ ] Verify sample indicators, signed axes and sampling shutdown when navigating away.
- [ ] Confirm no accumulating pending requests after disconnect/reconnect, tab changes or errors.
- [ ] Check optional provider status with missing/installed configurations.
- [ ] On a suitable test system, verify consent dialogs for driver installation, removal and repair, and session start/stop.
- [ ] Check high-DPI display, narrow windows, keyboard navigation, visible focus and reduced motion.
- [ ] Confirm portable export excludes device identifiers by default.
- [ ] Re-run existing 45 completion + 9 reliability tests locally, and add UI regression coverage.
- [ ] Review and merge provider-security PR #14 before final release integration.

UI design implementation and native integration are separate from production readiness: acceptance remains incomplete until a Windows build and real-controller testing pass.

Acronyms: UI = User Interface; WPF = Windows Presentation Foundation; SDL = Simple DirectMedia Layer; HID = Human Interface Device; JSON = JavaScript Object Notation; PR = Pull Request; DPI = Dots Per Inch.
