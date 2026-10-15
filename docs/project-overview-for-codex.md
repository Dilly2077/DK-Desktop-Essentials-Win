# Desktop Essentials — Codex project overview

> Project orientation, not an implementation-complete declaration. Updated 14 October 2026.

## Purpose and constraints

DK's Desktop Essentials is a free, open-source, local-first Windows power-user suite. Product principles: no required DK account, no subscriptions/paywall, no analytics or telemetry, no sale of user data, and transparent permissions. Internet access is permitted for user-requested operations (e.g., official driver searches, downloads) and separately approved update checks. Prefer established secure/open-source components and clearly document their licences and capabilities. No invented compatibility or performance guarantees.

The existing repository README documents a .NET 8 WPF host and bundled local WebView2 UI. Preserve the architecture, existing features and styling conventions. Existing code and tests, not this document, are the authoritative evidence of what is implemented.

## Category/module map

The original master source defines **six categories / 31 modules**:

1. **Controllers & Gaming Input (1–7)** — Controller Hub/remapping; diagnostics; controller-to-keyboard/mouse; automatic game profiles; game save manager; game library aggregator; FPS/system overlay.
2. **Hardware, Performance & Appearance (8–12)** — Hardware monitor; fan control; RGB/ScreenSync; performance/power profiles; startup/process tools.
3. **Drivers & System Maintenance (13–16)** — DK Drivers; driver confidence ranking; system information export; disk/storage tools.
4. **Network & Device Bridge (17–22)** — Home network dashboard; Wake-on-LAN/remote actions; LAN file transfer/clipboard; phone remote/macro deck; phone as webcam; local screen/control streaming.
5. **Files, Media & Creator Tools (23–28)** — PDF toolkit; image toolkit; audio/video converter; subtitles; screenshot/annotation; OCR.
6. **Automation & Power-User Productivity (29–31)** — Macro engine; macro deck desktop service; quick launcher/command palette.

**Proposed addition: Category 7 — Downloads & Transfers (32–37)**:
32 Download Manager Core; 33 Browser Integration; 34 Media & Batch Downloader; 35 Scheduler & Automation; 36 Download Library & Security; 37 Torrents & Advanced Protocols (optional).

Category 7 concerns **Internet downloads**; LAN peer-to-peer sharing stays in Module 19. Start here: [Downloads roadmap](roadmaps/downloads-and-transfers.md), then the full imported [Category 7 source](project-sources/Download_Category_Plan.txt).

## Current status, not just planned scope

- Repository README and [Module 1 completion report](controller-hub-completion.md) describe seven Controller Hub backend slices and automated validation. Physical-device/game acceptance and complete visible editor/UI remain separate.
- Do **not** claim all controllers, drivers, websites, codecs or devices are supported without actual integration/hardware tests.
- Modules 2–37 are not automatically implemented just because the audits describe their target requirements. Inspect Git history/tests and current code before describing status.
- Legacy planning text may include dated UI suggestions (e.g. favourites, recents, workspace). Treat current application implementation and later confirmed design decisions as higher priority than older descriptions.

## Key shared systems

- One controller input/profile stack rather than separate redundant low-level services.
- Shared hardware sensor service for fans/overlay/RGB/profiles where suitable.
- Secure LAN pairing/authentication for phone remote, WOL, transfer and desktop macro deck.
- Shared Macro Engine actions and access control.
- **One download-job engine and persistent local store** for Category 7; browser capture, media handling and scheduling should use it.
- Reuse existing UI shell and local settings.

## Engineering and quality gates

1. Inspect the repository and existing code/tests before editing. Work on only the assigned slice.
2. Distinguish **specified**, **coded**, **automated-tested**, **manually/hardware-verified**, and **release-ready**.
3. Keep full offline/local operation possible for UI, queue/history and other offline-capable functions. Never upload local personal data without informed user request.
4. Treat authentication, remote command execution, file deletion, driver/fan changes and network controls as privileged actions with clear controls and reversal where possible.
5. Keep optional integrations replaceable and licence-compliant.
6. For Category 7, test broken connections, partial responses, changed remote files, redirects, disk full, collisions, cancellation and restart; never claim universal acceleration/resume support.
7. Follow the root AGENTS.md **LOCAL BUILD ONLY** policy: compile, test and package on the user's local Windows computer. No GitHub Actions or other remote/cloud compilation without an explicit exception for that run. The preserved Windows workflow is manual-only, with an explicit authorisation input; verify remote activation before claiming GitHub automatic builds are disabled. See [local build policy](local-build-policy.md).

## Source and navigation

- [Index of all nine imported project source texts](project-sources/README.md)
- [Detailed new Category 7 roadmap and slice allocation](roadmaps/downloads-and-transfers.md)
- [Controller backend completion](controller-hub-completion.md)
- [Controller hardware acceptance](controller-hub-acceptance.md)
- Root [README](../README.md) — existing architecture and build instructions

## Handoff report for every slice

Record: changes and file paths; branch/commit; test commands/results; functionality remaining; UI remaining; manual/device acceptance remaining; security/privacy concerns; limitations; and the next slice plus suggested Chat vs Codex ownership.
