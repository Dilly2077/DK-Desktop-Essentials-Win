# Module 1 — Controller Hub completion

Slices 1–7 now implement the backend and its automated test harness. Visible UI/editor integration and physical hardware/game acceptance remain separate release work. This document does not declare the app production-certified.

## Scope and evidence

| Slice | Backend capability | Automated evidence |
|---|---|---|
| 1 | Windows discovery contracts, standard/raw state, mapping, tuning, chords, layers, toggle/turbo | Golden mapping vectors and shared-profile isolation |
| 2 | Local profile/preferences CRUD, import/export, defaults, aliases, corrupt-file reporting | Round trips, legacy profiles, concurrent/canceled persistence and bridge CRUD |
| 3 | Tap/hold/double press, nested layers, zones, 2D sticks, radial mappings, conflict analysis | Explicit timing/boundary vectors and dependency/overlap validation |
| 4 | Optional virtual Xbox360/DS4 helper/output backend, lifecycle and packaging | Published-helper IPC/reflection tests using a test-only substitute plus bundle verification |
| 5 | Optional SDL advanced input and capability-driven feedback | Touch vectors, explicit advanced linking, unsupported-effects handling and missing-input neutralization |
| 6 | Independent controller sessions, reconnect, neutralization, bounded recovery and shutdown | Nine fault-injection reliability cases and bridge-to-session tests |
| 7 | Repeatable completion harness, structured reports, release-bundle checks and acceptance procedure | Both suites run in Windows CI before the application artifact is published |

The completion suite has 32 named tests; the reliability suite has 9. Golden mapping tests supply timestamps rather than sleeping for tap/hold/turbo timing. Session tests use bounded polling. Five helper tests launch the actual published .NET 10 executable and verify Xbox/DS4 translation, acknowledgment, watchdog, malformed-input failure, neutralization and disposal through a substitute SDK. This exercises our helper and IPC, not the real HIDMaestro driver. The substitute cannot install/remove system devices and is excluded from the release artifact.

## Run the complete harness

Requirements: Windows 10/11 x64 and .NET 8 + .NET 10 SDKs. No controller, real provider, driver, account, elevation or provider download is needed for automated tests. Normal development restore can access NuGet for existing app dependencies.

```powershell
.\scripts\Test-ControllerHub.ps1
```

The script publishes a local helper fixture, builds the substitute SDK, runs both suites and writes `artifacts/controller-tests/reliability.json` and `completion.json`. Any failed test returns a failing script exit; results identify each case and preserve `physicalHardwareValidated: false`. CI uploads the JSON reports even when a suite fails. Build/restore failures before execution may leave no report.

For a faster managed-only run (explicitly excludes five process tests):

```powershell
.\scripts\Test-ControllerHub.ps1 -ManagedOnly
```

After publishing/bundling the app, `scripts/Verify-WindowsBundle.ps1` checks required app/helper/UI files and rejects leaked test SDKs or test assemblies.

## Completion review fixes

- Profile structure is validated before traversing null collections/actions or analyzing duplicate layer IDs. Unsupported enum values, invalid raw references and non-finite tuning are rejected. Invalid files are reported alongside valid files.
- Profile saves use unique temporary files with cancellation cleanup, and imports serialize collision checks with saving. Preference mutations serialize their complete read/modify/write transaction on the service's store; concurrent alias/default edits no longer overwrite one another. Multiple independent app processes writing the same preferences file remain outside this guarantee.
- Mistyped `stopOutputSession.deviceId` now yields an error instead of becoming stop-all. Omitted/null ID explicitly retains stop-all.

## Remaining release work and exclusions

Physical discovery/acceptance is documented in [controller-hub-acceptance.md](controller-hub-acceptance.md); every hardware row starts as not run. Actual Windows device events, the real driver, USB/Bluetooth, sensor/effect behavior, sleep/resume and game/anti-cheat compatibility require that evidence. Device hiding remains unavailable pending separate provider vetting. Standard-axis/digital release gates cannot identify mechanically stuck switches during normal use or infer generic raw-axis neutral ranges.

Controller Hub is wired to the local native/WebView bridge, but the visible controller page/editor is not implemented. Calibration/drift/polling graphs belong to Controller Diagnostics; keyboard/mouse output and automatic game profile selection are separate modules. Macro references are persisted and validated only: this engine does not execute the separate Macro Engine. Optional-provider packages are hash-pinned and explicit; this completion slice adds no telemetry, account service, runtime downloads or hiding driver.
