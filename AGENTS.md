# Codex instructions — DK Desktop Essentials

Read these project references **before implementing new modules**:

1. [Project orientation and architecture](docs/project-overview-for-codex.md)
2. [Source index containing all nine imported project documents](docs/project-sources/README.md)
3. [Category 7 Downloads & Transfers roadmap](docs/roadmaps/downloads-and-transfers.md) if working on downloads.
4. For controller work, inspect [Controller Hub completion](docs/controller-hub-completion.md), [acceptance](docs/controller-hub-acceptance.md), and the actual implementation/tests.

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

## Downloads & Transfers

The original source index covers **31 modules in six categories**. A proposed **seventh** category adds modules **32–37** with **30 planned slices**. Prioritise Module 32 Download Manager Core and minimal Module 36 safeguards before browsers/media/scheduling/torrents. The seven original audits date from 16 September 2026; verify external facts/licences again before implementation.

The two raw planning documents contain some ChatGPT-specific markup. For navigable Markdown, prefer [Downloads roadmap](docs/roadmaps/downloads-and-transfers.md) and [project overview](docs/project-overview-for-codex.md), referring to originals for exact phrasing.
