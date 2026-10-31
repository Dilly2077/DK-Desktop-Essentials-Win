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
- Follow the mandatory repository-wide **LOCAL BUILD ONLY** policy below for every compilation or packaging task.
- Separate completion labels: planned, implemented, automated-tested, manual/hardware-tested, and release-ready.
- Provide deterministic tests for pure logic and practical Windows/browser/device testing for platform integrations.
- Security-sensitive remote control, driver writes, destructive file actions and file downloads require transparent consent and rollback/failure handling where feasible.
- Do not hide unsupported features behind placeholder UI that looks functional; give honest unsupported states.
- After every slice, record exactly what changed, tests run/results, remaining backend, remaining UI, manual validation, limitations and next steps.

## Downloads & Transfers

The original source index covers **31 modules in six categories**. A proposed **seventh** category adds modules **32–37** with **30 planned slices**. Prioritise Module 32 Download Manager Core and minimal Module 36 safeguards before browsers/media/scheduling/torrents. The seven original audits date from 16 September 2026; verify external facts/licences again before implementation.

The two raw planning documents contain some ChatGPT-specific markup. For navigable Markdown, prefer [Downloads roadmap](docs/roadmaps/downloads-and-transfers.md) and [project overview](docs/project-overview-for-codex.md), referring to originals for exact phrasing.

## Permanent LOCAL BUILD ONLY policy

- All application compilation, packaging and artifact generation must run on the user's computer hosting the active local workspace. This includes EXE, MSI, installers, APK, AAB, desktop/mobile applications and other compiled outputs, regardless of module or feature.
- Never compile, package or generate release binaries with GitHub Actions, GitHub-hosted runners, remote CI/CD services or cloud build infrastructure unless the user explicitly authorises that particular exception. GitHub is primarily for source control, repository synchronisation and collaboration. Never substitute a remote build when a local build fails.
- Use existing local build tools. This project uses .NET/MSBuild: .NET 8 for the WPF/WebView2 app and .NET 10 for the controller output helper. Verify required SDKs, tools and dependencies before building; local dependency downloads are permitted when necessary. Prefer an installed SDK or the existing ignored `artifacts/dotnet/dotnet.exe` SDKs.
- If working in a cloud/remote environment without access to the user's computer, prepare source and local build scripts/commands instead. Explain what must run on the user's computer; do not call a cloud build local or switch to Actions.
- Keep `.github/workflows/build-windows.yml` manual-only. Its `authorize_remote_build` input must be explicitly true, and a particular run still needs explicit user authorisation. Never create, enable, automatically trigger or dispatch a remote build workflow without that authorisation. A checked-in workflow, historical permission or a failed local build is not authorisation for a new remote run.
- Preserve unrelated workflows and existing workflow functionality. Disable unnecessary automatic compile/package/release triggers through reversible edits rather than deleting workflows. Inspect workflow files and, when accessible, actual GitHub settings before reporting their status. Never claim Actions is completely disabled from local YAML alone; report any remaining remote/default-branch, ruleset or repository-setting work.
- For every requested compiled artifact: identify the correct local process, verify tools, run it, check exit codes/logs, fix local failures where possible, and verify the generated file actually exists. Report its exact absolute path, Debug/Release configuration and actual signing status, and provide a usable file link when supported. Never report a successful build from an assumed path or a successful command alone.
- If local tooling is unavailable, explain what is missing and provide local setup/build commands. Do not silently install global tools or change signing/security configuration.
- Keep outputs, SDKs, caches, temporary files and generated binaries out of Git unless explicitly requested. Never commit signing keys, credentials, passwords or secrets. Preserve existing source functionality and project-specific conventions; avoid unnecessary build changes.
- Keep this root `AGENTS.md` tracked in Git. Preserve nested instructions and ensure they do not weaken this policy; explicitly approved user exceptions take precedence. These rules apply to every future Codex session in this repository, not just this chat.

See [local build commands and Actions activation limits](docs/local-build-policy.md).
