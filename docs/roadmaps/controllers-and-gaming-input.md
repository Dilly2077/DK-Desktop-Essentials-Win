# Category 1 — Controllers & Gaming Input: implementation roadmap

**Status:** Modules 1–7 roadmap, including the Chat/Codex allocation agreed for Module 2. **Last verified:** 31 October 2026.  
**Source of agreed plan:** [Original Controller Modules List](../project-sources/Controller_Modules_List.txt).  
**Existing implementation evidence:** [Controller Hub completion](../controller-hub-completion.md), [physical acceptance](../controller-hub-acceptance.md), [security audit](../controller-hub-security-audit-2026-10-14.md), and the repository's code/tests.

> This document describes *planned* work and explicitly recorded completion status; it does not certify physical hardware, driver or game compatibility. Always inspect the latest main branch, pull requests and test results before claiming a task has been completed.

## 1. Scope: seven original modules, in their fixed order

Do not renumber these modules or confuse them with the separately proposed Category 7 (Downloads & Transfers, Modules 32–37).

| Module | Name | Agreed development owner | Known status at last verification |
|---|---|---|---|
| **1** | **Controller Hub / Remapping** | Backend already implemented; Chat for UI design and audit, Codex for Windows/UI integration and physical testing | **7/7 planned backend slices merged**; visible UI/editor, physical acceptance and final release validation pending; security PR #14 pending |
| **2** | **Controller Diagnostics** | **Six-slice hybrid plan; assignments below** | **0/6 slices implemented**, as confirmed by inspected repository history |
| **3** | Controller → Keyboard/Mouse | **Codex-led** Windows input/output integration | Not started |
| **4** | Automatic Game Profiles | **Hybrid:** Chat rules and matching logic; Codex process/game integration | Not started |
| **5** | Game Save Manager | **Hybrid:** Chat schemas, backup/restore policies and tests; Codex filesystem integration and safe recovery | Not started |
| **6** | Game Library Aggregator | **Codex-led:** local launcher/service integration | Not started |
| **7** | FPS / System Overlay | **Codex-led:** graphics and system overlays | Not started |

The seven-module order comes from the master premium-feature index; **only Module 2 has a fixed six-slice breakdown at this point**. Do not invent approved slice counts for Modules 3–7. Scope those separately before coding them.

## 2. Module 1 — Controller Hub / Remapping

### Seven backend slices (implemented and merged)

| Slice | Completed backend scope |
|---|---|
| **1.1** | Windows controller discovery, standard/raw input snapshots, initial remapping, tuning, chords, layers, toggle and turbo |
| **1.2** | Local profile and preference storage, import/export, default selections, aliases and native bridge |
| **1.3** | Tap/hold/double-press, nested shift layers, trigger zones, stick mappings, radial menus and conflict analysis |
| **1.4** | Optional, explicit virtual Xbox 360 / DualShock 4 output helper and driver/package lifecycle |
| **1.5** | Optional SDL3 advanced capability layer: gyroscope, touchpad, rumble, lighting and selected adaptive trigger effects |
| **1.6** | Independent per-device sessions, disconnect/reconnect handling, neutral output on failure, bounded retries and safe shutdown |
| **1.7** | Repeatable automated completion/reliability tests, published helper fixture tests, Windows bundle verification and written hardware acceptance procedure |

Automated tests do **not** count as physical controller validation. Source of completed slices: [completion document](../controller-hub-completion.md). Initial security findings/mitigations were merged separately in PRs #10 and #11.

### Security and release gate

- [Security audit](../controller-hub-security-audit-2026-10-14.md) and [Codex remediation brief](../controller-hub-codex-remediation.md).
- [Security PR #14](https://github.com/Dilly2077/DK-Desktop-Essentials-Win/pull/14) — *open/unmerged as of 31 October 2026*. Its branch reports successful Windows CI and additional security tests, but this must **not** be described as part of `main` before merge. Review the elevated HIDMaestro trust-boundary changes, SDK integrity, global cleanup behaviour and any tests before approving/merging.
- Global HIDMaestro cleanup can affect other applications' virtual devices. Keep explicit user consent; do not silently install or remove drivers.
- Physical device hiding remains unavailable pending a separately vetted provider. Do not attempt anti-cheat circumvention.
- Controller-to-keyboard/mouse functionality belongs to Module 3. Automatic game-profile switching belongs to Module 4. The Macro Engine belongs to Module 29, not Module 1.

### Remaining Module 1 tasks and owner

| Task | Chat contribution | Codex / Windows contribution |
|---|---|---|
| Controller Hub page | Design layout, device cards, status, input preview and navigation | Implement the working WebView2 UI and native bindings |
| Profile editor | Plan controls, interaction flow and validation/error copy | Implement editing, save/import/export, mapping curves, shift/turbo/radial UI |
| Virtual output controls | Safety copy, consent/recovery UX | Wire real start/stop, installation, status and errors |
| Advanced features interface | Design gyro, touch, rumble, RGB and adaptive-trigger screens | Capability-gated bindings and unsupported-state handling |
| Hardware acceptance | Test matrix and expected outcomes | Manual USB/Bluetooth controller and game testing, Windows diagnostics |
| Compatibility/release | Review logs and results, docs/privacy/a11y audit | Reconnect, multi-controller, sleep/resume, game/driver, packaging and release verification |

**Design decision:** Defer the large Module 1 visual UI/editor until Module 2's backend is ready, then build one coherent controller interface rather than duplicate device screens. **Exception:** execute a limited *physical controller discovery/input smoke test* before building Module 2, to prove the shared input provider works on the target Windows machine.

Suggested local discovery command:

```powershell
dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- --hardware-inspect --report artifacts/hardware-discovery.json
```

Full backend validation: `./scripts/Test-ControllerHub.ps1` (on Windows); follow [hardware acceptance](../controller-hub-acceptance.md) for physical-device testing. No physical test results have been established solely by this roadmap.

## 3. Module 2 — Controller Diagnostics: six agreed slices

**Feature scope:** live sticks/buttons/triggers, stick drift and jitter, response/deadzone advice, input sampling/timing, user-approved calibration, reported battery/connection status where supported, and optional local diagnostics reports.

Implement only the explicitly authorised slice. Chat-led deliverables may be written and tested as isolated algorithms/schemas in Chat before Codex integrates them.

| Slice | Work | Owner | Definition of slice deliverable | Work still outstanding immediately afterwards |
|---|---|---|---|---|
| **2.1** | Diagnostic Foundation | **Codex** | Reuse Module 1's device provider; add bounded live sampling, controller selection, diagnostic sessions, timestamp handling and local native/UI bridge commands. Avoid a redundant raw-input service. | Slices 2.2–2.6; full visual UI; physical input validation |
| **2.2** | Stick Drift & Input Analysis | **Chat** | Standalone drift/rest offset, jitter/noise, centre deviation, trigger response and deadzone recommendation calculations; deterministic synthetic test vectors and clear contracts for integration. | Integrate analysis; Slices 2.3–2.6; UI; physical accuracy testing |
| **2.3** | Polling & Timing Diagnostics | **Codex** | Sample interval and timing variability measurements using the existing provider; separate observed polling/sample cadence from true end-to-end input latency. | Slices 2.4–2.6; UI; measured hardware timing validation |
| **2.4** | Calibration Engine | **Chat + Codex** | Chat: calibration math, validation and vectors. Codex: preview/apply/rollback wired to existing **local profiles**, with explicit user consent. Do not silently edit global system calibration. | Slices 2.5–2.6; UI; physical calibration testing |
| **2.5** | Battery, Health & Reports | **Chat + Codex** | Chat: status and local JSON/CSV report schemas/formatters. Codex: device-specific capability/status integration and optional user-triggered local exports. Show **unknown** when no trustworthy battery data is provided. | Slice 2.6; UI; battery/device compatibility testing |
| **2.6** | Reliability & Completion Tests | **Codex** | Automated negative and integration tests for samples, analysis accuracy, disconnect, cancellation, memory bounds, stored profiles and Modules 1+2 integration. | Combined visual UI, physical controller acceptance and release polish |

**Allocation:** 3 Codex-led slices (2.1, 2.3, 2.6), 1 Chat-led slice (2.2), 2 hybrid slices (2.4, 2.5). In the hybrids, do as much isolated maths/schema/test work as practical in Chat before delegating repository integration to Codex. This division minimises unnecessary coding-agent work **without** pretending Chat's calculations are Windows-validated.

**After all six backend slices:** Module 2 must still have a functional diagnostics interface: live device diagram, stick-drift visualisation, timing graph, calibration controls and reports UI. Validate with physical controllers, including known-good and known-drift examples where available. The UI and hardware work can then be completed jointly with Module 1.

## 4. Shared implementation and UI decisions

- **Single controller/device infrastructure** for Modules 1–4; Module 2 must consume existing discovery, identity and profile services instead of independently competing for the same devices.
- The user-approved application design is a minimal Command Center with expandable per-category tools. Do **not** reintroduce redundant All Tools/Favourites/Recents sidebar items or a standalone Workspace page simply because old UI prototype code or historical documents contain them.
- Quick Settings must be functional, not a set of misleading decorative toggles.
- Prefer capability-driven states and truthful compatibility messages over claiming universal controller, sensor, anti-cheat or game support.
- The shared local-first/privacy-first requirements apply to every slice: no telemetry, accounts, ads or hidden network calls. Third-party packages are optional, explicitly approved, sourced from reputable providers and integrity-verified.

## 5. Designated development order

1. **Module 1 security checkpoint:** review [PR #14](https://github.com/Dilly2077/DK-Desktop-Essentials-Win/pull/14), resolve any review issues and merge only after appropriate validation. Record any unresolved driver/real-hardware risk.
2. **Module 1 limited physical smoke test:** confirm discovery and basic input for at least one real controller on the intended Windows PC; record model and connection type.
3. **Module 2 backend slices 2.1–2.6:** Codex → Chat → Codex → hybrid → hybrid → Codex as in the allocation table. Require independent review/test evidence for each.
4. **Combined Modules 1+2 UI:** design in Chat, implement/integrate in Codex; include device cards, profile editor, mappings, output controls and diagnostics.
5. **Full physical/device/game acceptance:** validate USB/Bluetooth, reconnection, multiple controllers, optional driver/SDK, feedback, calibration, sleep/resume, duplicate inputs and game compatibility. Distinguish unsupported hardware from defects.
6. **Module 3:** controller-to-keyboard/mouse, Codex-led.
7. **Module 4:** automatic game profiles, hybrid.
8. **Modules 5–7:** game saves, game library and FPS/system overlay, with work split as in Section 1. Scope their individual slices before implementation.

## 6. Mandatory status report **after every slice**

The agent doing a slice (Chat or Codex) must record a durable progress report in repository documentation and give the user a concise summary, including:

| Field | Required contents |
|---|---|
| Scope completed | Slice identifier, implemented capabilities, exact files changed and omissions |
| Git changes | Branch, commits, pull request and merge status; never treat an unmerged PR as on main |
| Automated testing | Commands executed, passed/failed/not-run cases and any failures |
| Remaining backend | Unimplemented functions and the following backend slices |
| UI status | Implemented, partial or not started; outstanding UI/editor work |
| Hardware/manual status | Physical devices, Windows/game tests performed and still needed |
| Security/privacy/compatibility | Drivers, third-party packages, unsupported devices, known limitations and user-consent requirements |
| Next action | **Exact next slice and Chat/Codex owner** |

Use distinct statuses **planned → coded → automated-tested → hardware-verified → release-ready**. Do not mark an entire module done just because its backend tests pass. Build/test on the user's Windows environment by default; do not deliberately trigger GitHub Actions as a remote build service unless explicitly requested. Repository workflow configuration may run automatically on pushes.

## References and source precedence

- [Original controller roadmap transcription](../project-sources/Controller_Modules_List.txt) — the agreed module/slice assignment and work split, but contains UI-specific markup and historical status.
- [Master feature index](../project-sources/00_Master_Premium_Feature_Index.txt) and [controller paywall audit](../project-sources/01_Controllers_and_Gaming_Input_Paywall_Audit.txt) — category scope and intended features, **not** evidence of code.
- [Module 1 completion](../controller-hub-completion.md), [acceptance](../controller-hub-acceptance.md), [security audit](../controller-hub-security-audit-2026-10-14.md), [Codex remediation brief](../controller-hub-codex-remediation.md) — implementation, testing and release requirements.
- **Live source of truth for progress:** current GitHub branches, merges, code/tests and current Windows test reports; update this roadmap when a slice's verified status changes.
