# Category 7 — Downloads & Transfers implementation roadmap

**Status:** requirements; partial implementation recorded below. **Updated:** 25 October 2026.
**Scope:** Modules 32–37, **30 development slices** (6 Chat-led, 4 hybrid, 20 Codex-led). Category 7 is additional to the original 31-module master index; do not renumber existing modules.

Progress: [32.1 contracts](../downloads-slice-32-1.md), the
[32.2 engine adapter](../downloads-slice-32-2.md),
[32.3 direct transfers](../downloads-slice-32-3.md),
[32.4 validated resume/recovery](../downloads-slice-32-4.md), and
[32.5 durable queue/retries](../downloads-slice-32-5.md) are implemented and locally
automated-tested. Basic Windows UI queue/pause/restart acceptance passed in 32.5.
[32.6B native UI integration](../downloads-slice-32-6b.md) is implemented with
111 download tests, 31 frontend tests and real Windows HTTP/action/restart checks.
The full content-width range, 150%/200% scaling and spoken screen-reader acceptance
remain unverified. Module 32 and Category 7 are not release-ready.

Source: [full original Download Category Plan](../project-sources/Download_Category_Plan.txt). See also [project source index](../project-sources/README.md) and [Codex overview](../project-overview-for-codex.md).

## Goals and hard requirements

Build an IDM-style Internet download manager integrated into the existing Windows Desktop Essentials host. Features should be free and local-first: HTTP(S) transfers, segmented/range downloads where valid, pause/resume, persistent queues, browser integration, optional media capture, scheduling, integrity checks and local history. No telemetry, ads, user account, license activation or hidden feature limits.

**Not the same as** Module 19 LAN File Transfer: local direct peer sharing remains in Network & Device Bridge. Do not create independent download engines for each new module.

Candidate building blocks to evaluate (not automatically approved dependencies): aria2 (transfers/segmentation), yt-dlp (supported media extractors), FFmpeg (media post-processing), SQLite (local metadata), browser WebExtensions/native messaging, native Windows security tooling. Verify licences, provenance, distribution rules and current version/support before bundling.

## Module 32 — Download Manager Core (6 slices; essential)

| Slice | Owner | Deliverable |
|---|---|---|
| **32.1** | **Chat** | Define job-state machine, persistent data model, queue contracts, error codes, download-engine abstraction and deterministic test cases. Proposed states: Queued, Resolving, Downloading, Paused, Retrying, Verifying, Completed, Failed, Cancelled. |
| **32.2** | **Codex** | Inspect .NET/WPF/WebView2 repository, evaluate aria2 versus suitable alternatives (including packaging/licence), integrate an isolated replaceable engine adapter. |
| **32.3** | **Codex** | Direct HTTP/HTTPS URL submission, save destination, basic transfers, progress/speed/ETA, error reporting and cancellation. |
| **32.4** | **Codex** | Optional parallel segments, validated range support, safe pause/resume and restart recovery. Handle HTTP 200/206/416, ETag/Last-Modified and changed remote content without corrupting partial files. |
| **32.5** | **Codex** | Durable queues, configurable concurrency, retry/backoff, cancellation, destination collisions and disk capacity/error handling. |
| **32.6** | **Hybrid** | Integrated Download Manager UI and reliability/Windows acceptance suite. Design UI in Chat, implement/integrate/test in Codex. |

Acceptance: paste URL, select destination, download, pause/resume when the server allows, safely survive app exit/relaunch, display honest status. Temporary files, sanitisation and non-execution are mandatory early safeguards borrowed from Module 36.

## Module 33 — Browser Integration (5 slices; high)

| Slice | Owner | Deliverable |
|---|---|---|
| **33.1** | **Chat** | Browser capture-policy settings, minimal required permissions, message schema, security/consent specification. |
| **33.2** | **Codex** | Secure local Windows native messaging host and authentication/authorization boundaries. |
| **33.3** | **Codex** | Chrome/Edge WebExtension: explicit `Download with DK`, optional supported automatic capture. |
| **33.4** | **Codex** | Firefox/Zen-compatible extension and browser-specific testing. |
| **33.5** | **Codex** | Interception consent, cookies/auth handling, signed URLs, browser restarts, extension removal and fallback validation. |

First implement user-invoked context menu; add opt-in automatic capture only after secure native bridge is proven. Do not claim every download/login is interceptable.

## Module 34 — Media & Batch Downloader (5 slices; high)

| Slice | Owner | Deliverable |
|---|---|---|
| **34.1** | **Chat** | URL classification, quality/format presets, media metadata and extraction interface. |
| **34.2** | **Codex** | Optional yt-dlp adapter, source support/errors, subprocess isolation/update policy. |
| **34.3** | **Codex** | Video/audio streams, subtitle selection, quality presets and FFmpeg merging where permitted. |
| **34.4** | **Hybrid** | Batch-link import and consent-based website asset grabber: bounded crawl depth, exclusions, preview and filtering. |
| **34.5** | **Codex** | Media/batch UI; metadata, cancellation, output/playback and regression tests. |

No DRM circumvention or bypass of login/security barriers; supported sites may change without warning.

## Module 35 — Scheduler & Automation (5 slices; high)

| Slice | Owner | Deliverable |
|---|---|---|
| **35.1** | **Chat** | Scheduler data/rules, priorities, conflict resolution, concurrency and test vectors. |
| **35.2** | **Codex** | Persisted timed starts/stops and recovery after application sleep/restart. |
| **35.3** | **Codex** | Per-job/global bandwidth limits, connection caps and concurrency. |
| **35.4** | **Hybrid** | Categorisation, filename templates, post-download organisation and duplicate policies. |
| **35.5** | **Codex** | Notifications, restricted completion actions, settings UI and scheduling reliability tests. |

Completion actions, shutdown and script execution require explicit opt-in; integrate with existing Macro Engine later, not a second script executor.

## Module 36 — Download Library & Security (5 slices; essential)

| Slice | Owner | Deliverable |
|---|---|---|
| **36.1** | **Chat** | Local history/categories/tags, hashes, privacy/retention and test contracts. |
| **36.2** | **Codex** | Searchable library, filtering, history persistence, local export and deletion choices. |
| **36.3** | **Hybrid** | Size/hash/integrity verification, trustworthy mismatch handling and deterministic tests. |
| **36.4** | **Codex** | Windows security integration, safe partial files, safe paths, antivirus scans with correct status reporting and no auto-execution. |
| **36.5** | **Codex** | Library UI, privacy settings, safe removal, penetration/security and privacy review. |

Prioritise essential security from this module **during Module 32**, not only at the end of this list. Sanitise/redact URLs containing secrets in logs/exports.

## Module 37 — Torrents & Advanced Protocols (4 slices; optional)

| Slice | Owner | Deliverable |
|---|---|---|
| **37.1** | **Chat** | Protocol, permissions, privacy and packaging plan. |
| **37.2** | **Codex** | Optional BitTorrent/magnet/Metalink or other selected protocol integration behind an explicit enablement. |
| **37.3** | **Codex** | Peer progress, file selection, download/seed settings, rate limits. |
| **37.4** | **Codex** | Hash/integrity, network/peer exposure, restart, security and licence validation. |

Torrent swarms disclose network endpoints to other peers. Do not silently enable P2P/network sharing.

## Recommended milestones

1. **v0.1 core usable:** 32.1–32.6 plus minimum file/security controls from Module 36. No browser integration required.
2. **Browser capture:** 33.1–33.5, starting with manual link handoff.
3. **Media and bulk:** 34.1–34.5.
4. **Download automation and library:** 35.1–35.5 and remaining 36.1–36.5.
5. **Optional protocols:** 37.1–37.4 only if justified after stable core release.

## Test matrix / non-negotiable quality gates

Use a local HTTP test server plus local Windows checks to validate: broken connection; missing range support; invalid ranges; remote content changed during pause; redirects; response corruption; retries/backoff; drive full; permission errors; conflicting filenames; cancellation; app restart; and no network. Security: untrusted filenames, path traversal, symlink/reparse-point handling, authentication leakage, secret-bearing URL logs and execution protection.

Do not promise higher speeds than network/server limits; only use parallel segments when valid. Do not call the module finished solely because it compiles or tests pass.

## Chat vs Codex and handoff discipline

**Chat-led:** design, algorithms, schemas and deterministic tests. **Codex-led:** source integration, Windows/browser dependencies, real build/test, security/packaging. **Hybrid:** Chat design then Codex implementation/verification.

At each slice's completion record: files changed; commit/branch; tests passed/failed/not run; backend/UI completion separately; real Windows/browser tests; known limitations; privacy/security implications; remaining work; and next slice. Use local Windows builds/tests rather than GitHub Actions unless explicitly requested.

**First recommended task:** Module 32 Slice 32.1 (Chat) followed by 32.2 (Codex). Do not jump straight to browser/media/torrent features.
