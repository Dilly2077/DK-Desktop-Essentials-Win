# Codex instructions — DK Desktop Essentials

Read these project references **before implementing new modules**:

1. [Project orientation and architecture](docs/project-overview-for-codex.md)
2. [Source index containing all nine imported project documents](docs/project-sources/README.md)
3. **For Controllers & Gaming Input (Modules 1–7):** read the [Controller category roadmap and Chat/Codex slice allocation](docs/roadmaps/controllers-and-gaming-input.md) **before planning or implementing a controller slice**; then inspect [Controller Hub completion](docs/controller-hub-completion.md), [physical acceptance](docs/controller-hub-acceptance.md), relevant security documentation and actual source/tests.
4. [Category 7 Downloads & Transfers roadmap](docs/roadmaps/downloads-and-transfers.md) if working on downloads.

## Non-negotiable product principles

Local-first, open-source and privacy-first Windows suite. No telemetry, analytics, paid features, hidden upload services, mandatory accounts, advertising SDKs or cloud entitlement checks. Network access only for explicit user-requested functionality or separately approved updates. Keep modules compatible with the existing .NET WPF/WebView2 solution and existing UI conventions. Treat the audits and roadmaps as requirements, not assertions of implementation.

## Development workflow

- Inspect code, existing documentation and tests before modifying anything. Avoid unrelated changes.
- Work **slice by slice**; do not interpret a roadmap as permission to implement the entire category in one task.
- **Prefer local compilation, tests and executable packaging on the user's Windows machine. Do not use GitHub Actions as a build service unless explicitly requested.** Take care not to invoke remote CI/release workflows unnecessarily.
- Separate completion labels: planned, implemented, automated-tested, manual/hardware-tested, and release-ready.
- Provide deterministic tests for pure logic and practical Windows/browser/device testing for platform integrations.
- Security-sensitive remote control, driver writes, destructive file actions and file downloads require transparent consent and rollback/failure handling where feasible.
- Do not hide unsupported features behind placeholder UI that looks functional; give honest unsupported states.
- After every slice, record exactly what changed, tests run/results, remaining backend, remaining UI, manual validation, limitations and next steps.

## Controllers & Gaming Input — required roadmap and ownership

- **The authoritative agreed work split for this category is [docs/roadmaps/controllers-and-gaming-input.md](docs/roadmaps/controllers-and-gaming-input.md)**, derived from the [original controller planning text](docs/project-sources/Controller_Modules_List.txt). Seven modules are numbered 1–7. Do **not** treat the historical source text as up-to-date implementation status.
- Module 1 Controller Hub: backend Slices 1–7 are merged, but its controller/profile UI, manual hardware and game acceptance, and release gates remain separate. Check [security PR #14](https://github.com/Dilly2077/DK-Desktop-Essentials-Win/pull/14) and current main/PR status before claiming provider hardening is merged or complete.
- Module 2 Controller Diagnostics: **six planned backend slices**. Ownership is **2.1 Codex; 2.2 Chat; 2.3 Codex; 2.4 Chat + Codex; 2.5 Chat + Codex; 2.6 Codex**. Keep the Chat-authored calculation, schema and deterministic-test work separate from native Windows integration and acceptance testing. Codex should not automatically take over the Chat-designated work or advance beyond the explicitly assigned slice.
- Begin with the short Module 1 physical controller discovery/input smoke test and outstanding security review, then Module 2 Slice 2.1 in Codex, followed by 2.2 in Chat. **Defer the large Module 1 UI until Module 2's backend is ready**; develop the combined Modules 1+2 controller UI in one integration phase, then complete physical hardware/game acceptance.
- Modules 3–7 follow their designated order and broad owners in the roadmap. **Their individual slice counts are not yet agreed**. Plan those before coding, rather than inventing a breakdown.
- Each slice must leave an explicit status report separating remaining **backend**, **UI**, **manual/device testing**, **security/privacy limitations** and the **next owner**. Do not label a module release-ready on the strength of managed tests alone.

## Downloads & Transfers

The original source index covers **31 modules in six categories**. A proposed **seventh** category adds modules **32–37** with **30 planned slices**. Prioritise Module 32 Download Manager Core and minimal Module 36 safeguards before browsers/media/scheduling/torrents. The seven original audits date from 16 September 2026; verify external facts/licences again before implementation.

The two raw planning documents contain some ChatGPT-specific markup. For navigable Markdown, prefer [Downloads roadmap](docs/roadmaps/downloads-and-transfers.md) and [project overview](docs/project-overview-for-codex.md), referring to originals for exact phrasing.
