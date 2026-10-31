# Module 32, Slice 32.6A — Download Manager UI / Codex 32.6B handoff

**Branch:** `feature/downloads-slice-32-6a-ui` (based on `main`).  
**Scope:** Chat-owned WebView2 frontend, mock adapter, acceptance preparation.  
**Implementation status:** source committed; pure frontend harness exercised; native integration **not started in this branch**.  
**Not covered:** Module 33, media extraction, native transfer/storage, production Windows acceptance.

This is the historical 32.6A handoff. For the current native implementation,
the tightened folder-grant contract and actual Windows acceptance results, see
[Slice 32.6B](downloads-slice-32-6b.md). Workflow statements below describe that
branch at handoff; the current local workflow is manual-only under AGENTS.md.

## 1. Architecture and files

- `src/DKDesktopEssentials/ui/downloads-core.js` — pure display model:
  normalisation, bytes/progress/speed/ETA, status grouping, case-insensitive
  search, input URL validation, filename sanitisation, allowed-action logic.
- `src/DKDesktopEssentials/ui/downloads-mock.js` — clearly identified
  in-memory **simulation**, with deterministic fixtures named
  `mixed`, `empty`, `missing` and `many` (250 jobs). No network, disk or
  persistent storage. User advances progress with **Advance demo +10s**.
- `src/DKDesktopEssentials/ui/downloads-ui.js` — DOM-based manager:
  summary counts, progress rows, controls, per-job detail panels, filters,
  new-download dialog and confirmation dialogs, loading/empty/error states.
  Jobs are rendered 50 at a time to bound DOM load in large queues.
- `src/DKDesktopEssentials/ui/downloads.css` — scoped design matching
  existing navy/cyan app shell, compact/reduced-motion preferences and
  container-width responsive layout.
- `src/DKDesktopEssentials/ui/app2.js` — adds Module 32 to the existing
  Command Center category list and routes its existing tool-detail mechanism
  into `DKDownloadsUI.mount`. Disposes listeners when leaving the page.
- `src/DKDesktopEssentials/ui/index.html` — loads new local CSS/JS in order
  and adds **Downloads & Transfers** among existing categories.
- `tests/DownloadUI.Tests/downloads.test.cjs` — dependency-free Node tests
  covering pure presentation logic and mock workflows.

The original WebView2 Content Security Policy remains unchanged: no remote
CSS/JS/fonts, no UI fetches, no external resources. No new global navigation
surface beyond the designated seventh category.

The UI uses `<dialog>.showModal()` for focus containment, modal keyboard
behaviour and visible confirmations. Escape dismisses dialog rather than
navigating away. Screen-reader labels and `aria-pressed` status filters
are provided. See https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/dialog

## 2. Current runtime behaviour and limitations

Click **Command Center → Downloads & Transfers → Download Manager**.

**Demo mode is the only runtime adapter attached in this branch.**
A permanent banner states “DEMO MODE · simulated downloads” and
“no network requests, disk writes or real downloads occur”.

- Clicking Start/Pause/Resume/Cancel/Retry updates only in-memory jobs.
- Remove deletes only the simulated job record, **never a file**.
- Clicking Add to queue or Start in the new-download dialogue creates
  an in-memory job; the submitted URL is validated but never fetched.
- **Browse…** and **Open folder** remain visibly disabled in demo mode.
  The destination input is manually editable for UI testing only.
- Resume remains disabled when the job declares `resumeCapability:
  "Unsupported"`. For `Unknown`, native must decide whether resumption
  is safe before attempting it.
- File lists show source **host**, not full URLs or query tokens. Secrets
  must not be included in UI event logs, view models, exceptions or search.
- No automatic demo clock runs. “Advance demo +10s” is the only mock progress
  advance, ensuring deterministic tests and avoiding any claim that an actual
  network transfer is progressing.
- The UI uses `expectedBytes: null` for unknown total size, and
  `bytesPerSecond: null` / 0 for unknown speed. It does not invent ETA.

This frontend cannot prove the backend is safe; native operations and Windows
testing remain completely separate.

## 3. Frontend adapter contract

UI entry point:
```js
DKDownloadsUI.mount(document.getElementById('main-pane'), {
  adapter: nativeAdapter, // Codex must explicitly inject after handshake
  onBack: () => renderCommand('Downloads & Transfers')
});
```

Adapter interface (promise-returning unless noted):

| Method/property | Contract |
|---|---|
| `capabilities` | `{mode:'mock'\|'native',browseDestination,openFolder,start,pause,resume,retry,cancel,remove}` |
| `list()` | Returns full `DownloadJobView[]`, sorted/filtered in UI |
| `get(id)` | Returns one `DownloadJobView` or fails |
| `enqueue({url,destinationDirectory,fileName,startNow})` | Explicit user-submitted HTTP/HTTPS only; return created job |
| `start(id)` | Dispatch queued job, if supported; report backend acceptance |
| `pause(id)` | Pause/cancel engine lease and persist state safely |
| `resume(id)` | Resume paused job only when backend validates partial content; otherwise return typed actionable error |
| `retry(id)` | Transition failed job into queue under retry policy |
| `cancel(id)` | Explicit cancel with safe partial-file policy |
| `remove(id)` | **Remove history/record only**, never delete a completed or partial file without separate user-authorised operation |
| `openFolder(id)` | Native-only; safely reveal verified destination for completed job |
| `chooseFolder()` | Native Windows folder picker; return local path or `null` if cancelled |
| `subscribe(onChanged)` | Push notification, returns unsubscribe function; triggers `list()` refresh |
| `advance(seconds)` / `setScenario(name)` | Mock-only; never implement in native adapter |

Each view record:

```ts
type DownloadJobView = {
  id: string;
  state: 'Queued'|'Resolving'|'Downloading'|'Paused'|'Retrying'|
         'Verifying'|'Completed'|'Failed'|'Cancelled';
  fileName: string;                 // leaf name, never a full path
  displayHost: string;              // redacted origin display, no query
  destinationDirectory?: string|null; // path only, no credential
  transferredBytes: number;        // finite non-negative
  expectedBytes?: number|null;     // null = unknown
  bytesPerSecond?: number|null;    // null = unknown
  createdUtc?: string|null;
  updatedUtc?: string|null;
  priority?: 'Low'|'Normal'|'High';
  errorCode?: string|null;
  safeErrorDetail?: string|null;    // no raw headers, signed URLs, secrets
  resumeCapability?: 'Supported'|'Unsupported'|'Unknown';
  capabilities?: Record<string,boolean>; // optional per-job overrides
  revision?: number;
};
```

Host messages should follow existing trusted `dk.local` WebView2 bridge
conventions and use correlation IDs. Proposed new commands:

```text
downloads:capabilities
downloads:list       downloads:get
downloads:enqueue    downloads:start
downloads:pause      downloads:resume
downloads:retry      downloads:cancel
downloads:remove     downloads:openFolder
downloads:chooseFolder
downloads:event      (native -> UI notification)
```

Proposed frontend request, with the URL present **only** in the explicit
enqueue invocation (do not broadcast it):

```json
{"type":"downloads:enqueue","requestId":"uuid","payload":
 {"url":"https://example.org/archive.zip","destinationDirectory":"C:\\Downloads",
  "fileName":"archive.zip","startNow":false}}
```

Proposed response:

```json
{"command":"downloads:enqueue","requestId":"uuid","ok":true,"result":{ "...":"redacted job view" },"error":null}
```

Error should be a typed, sanitised code and safe message, never a raw exception
or unescaped URL. Use `window.chrome.webview.postMessage(JSON.stringify(...))`
only from the trusted local page. The .NET WPF host must verify document
origin before dispatch. It must validate URL scheme, destination, file name,
capabilities, and job state **again** server-side; JS checks are advisory.
No arbitrary shell commands, frontend-supplied executable paths or engine
flags may pass through the bridge.

**Important backend differences to reconcile:** The 32.1
`DownloadJobView` contract did not expose `bytesPerSecond`,
`resumeCapability` or `destinationDirectory` and omitted UI-only
`start`, `remove`, `openFolder`, `chooseFolder`.
Codex should expose safe projections or adapt them through a native bridge,
never put raw `DownloadJobRecord` or secret-containing `SourceKey` on
the UI bus.

## 4. UI acceptance matrix (manual/Windows + automation targets)

| ID | Scenario | Expected acceptance result | Status |
|---|---|---|---|
| U01 | Open from Command Center | Module 32 dashboard in existing shell, no unrelated navigation | Source implemented; Windows pending |
| U02 | First load | Loading indication until list resolves | Source implemented; Windows pending |
| U03 | No records | Clear empty state, New download available | Mock `empty` |
| U04 | Normal single transfer | Correct status, bytes and progress | Mock `mixed`; native pending |
| U05 | Two or more active | Distinct progress/speeds/ETA per job | Mock `mixed`; native pending |
| U06 | Queued start | Dispatch only selected queued record | Deterministic test; native pending |
| U07 | Pause and resume | Correct controls/state, server validated before native resume | Mock flow only |
| U08 | Resume unsupported | Disabled Resume and explanatory tooltip | Pure test; manual pending |
| U09 | Cancel and remove | Confirmation, no accidental file deletion | Mock only |
| U10 | Failed record | Error code/details and actionable Retry | Mock `mixed`/`missing` |
| U11 | Completed file | Open folder only when native confirms file exists | Disabled in demo |
| U12 | Malformed URL | Form error, no job created | Pure tests |
| U13 | Non-HTTP(S) URL | Rejected without network call | Pure tests |
| U14 | Credential URL | Reject embedded username/password | Pure tests |
| U15 | Missing total | Unknown size, indeterminate progress, no fabricated ETA | Pure tests + mock `missing` |
| U16 | Missing speed | Speed/ETA shown as unknown rather than 0% guess | Pure tests |
| U17 | Very long filename | Ellipsis or wrap without horizontal overflow | Mock `mixed`; visual pending |
| U18 | Search | Case-insensitive filename/source/error lookup | Pure test |
| U19 | Status filters | Accurate counts, selected filter and empty-results state | Pure test; UI pending |
| U20 | 250 jobs | Load first 50, Show next 50, no UI lockup | Mock `many`; perf pending |
| U21 | New-download dialog | URL, destination, filename preview, start/queue actions | Source implemented; UI pending |
| U22 | Browse folder | Native picker only, cancel harmless; disabled in demo | Native pending |
| U23 | Offline UI | Existing dashboard/history opens without fetching network assets | CSP designed; Windows pending |
| U24 | Window 320–700px content width | Cards, filters and controls wrap; no clipped actions | CSS added; visual pending |
| U25 | 100% / 125% / 150% / 200% scaling | Text, modal and list remain readable | Windows pending |
| U26 | Keyboard Tab/Shift+Tab | Logical order, focus visible for controls | CSS/semantics; manual pending |
| U27 | Keyboard Enter/Space | Activates focused buttons | Native button semantics; manual pending |
| U28 | Dialog Escape/focus | Dialog closes before page navigation; focus restored | `showModal()` + guards; manual pending |
| U29 | Screen reader | Name/state/progress available, status filters announce `aria-pressed` | Markup added; reader pending |
| U30 | Host error/timeout | Error message, no false success, Retry refresh offered | Source implemented; native pending |
| U31 | Untrusted filename injection | Rendered as text, never executed as HTML | Escaping present; security test pending |
| U32 | Leave and return | Listeners unsubscribed; no dangling timer | Source implemented; Windows pending |
| U33 | Real restart/persistence | Backend restores queue/progress without corruption | **Native 32.6B only** |
| U34 | Real range/content change/disk full | Accurate actionable failures; never splice invalid partial content | **Native 32.6B only** |

## 5. Frontend test execution

Dependencies: Node 18+ only (Node's built-in `node:test`).

```powershell
node --check .\src\DKDesktopEssentials\ui\downloads-core.js
node --check .\src\DKDesktopEssentials\ui\downloads-mock.js
node --check .\src\DKDesktopEssentials\ui\downloads-ui.js
node --check .\src\DKDesktopEssentials\ui\app2.js
node --test .\tests\DownloadUI.Tests\downloads.test.cjs
```

For Windows user-acceptance, run the native app **locally**:

```powershell
dotnet run --project .\src\DKDesktopEssentials\DKDesktopEssentials.csproj
```

Do NOT run `workflow_dispatch`, push to `main`, create a PR or use GitHub
Actions to execute these tests. The `feature/` branch does not match
the repository's existing `push: main, module/**` workflow filters; opening
a pull request to main **would** trigger CI.

The external code execution environment here can run script syntax/pure
JavaScript checks, but it is not a Windows WebView2 runtime. Therefore no
Windows/manual UI acceptance is claimed.

## 6. Exact native integration controls outstanding (Codex 32.6B)

1. Implement `NativeDownloadAdapter` bound to existing .NET Download Coordinator.
2. Add secure `downloads:` WebView2 host dispatcher and correlation/timeout handling.
3. Map raw job to the **redacted** view model, including progress, ETA inputs,
   resume capability and destination information.
4. Replace injected mock with native adapter **only after a successful explicit
   capability handshake**; retain opt-in mock scenario for development.
5. Implement real Start, Pause, Resume, Cancel, Retry, Remove; honour state machine.
6. Wire native folder picker and safe Open containing folder; do not allow
   frontend-controlled arbitrary command execution.
7. Persist and restore settings, queues and error events through existing backend.
8. Run local Windows builds/Node tests plus U01–U34 acceptance, including
   real network/HTTP fixtures and offline privacy checks.
9. Validate keyboard focus, scaling, high-DPI and WebView2 CSP in the packaged app.
10. Run security/privacy review before claiming release readiness.

## 7. Full Slice 32.6B Codex handoff prompt

> On the repository `Dilly2077/DK-Desktop-Essentials-Win`, integrate the
> frontend from branch `feature/downloads-slice-32-6a-ui` with the current
> local Module 32 backend. First inspect `AGENTS.md`,
> `docs/project-overview-for-codex.md`,
> `docs/roadmaps/downloads-and-transfers.md` and
> `docs/downloads-slice-32-6a.md`.
>
> Preserve all unrelated UI/modules. Merge/cherry-pick the isolated frontend
> as appropriate; resolve backend branch divergence without overwriting
> local uncommitted work. Implement an explicit secure native WebView2
> adapter for `downloads:` actions, validate jobs and permissions in C#,
> provide a Windows folder picker and verified open-folder operation, enforce
> filename/path safety, and expose safe view models/events. Replace mock
> mode only after capability handshake; never show demo jobs as real.
> Use the acceptance matrix U01–U34 and run local builds/tests on Windows.
> Do not trigger GitHub Actions, do not move to Module 33, and do not invent
> backend successes. Report changed files, executed tests, manual acceptance,
> known limitations, remaining work and release status.

**Acronyms:** UI = User Interface; API = Application Programming Interface;
WPF = Windows Presentation Foundation; CSP = Content Security Policy;
DOM = Document Object Model; URL = Uniform Resource Locator;
HTTP(S) = Hypertext Transfer Protocol (Secure); ETA = Estimated Time of Arrival;
DPAPI = Data Protection API.
