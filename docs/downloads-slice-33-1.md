# DK's Desktop Essentials — Module 33, Slice 33.1

**Status:** capture policy, strict message schema and pure test vectors committed; **no browser extension, no native messaging host and no real download interception built**.  
**Design date:** 31 October 2026.  
**Scope:** Browser capture rules, permissions, settings, source/consent handling, protocol and acceptance scenarios.  
**Next slice:** 33.2 — secure Windows native messaging host (Codex).

## 1. Foundation and prerequisites

Module 33 is a browser-side entry point to the existing **Module 32 download coordinator**.
It MUST NOT create its own transfer engine, queue database or download state machine.
The source of truth for job acceptance, persistence and verification remains the
native Module 32 backend, after that backend is implemented and validated.

The current `main` branch does not yet contain the earlier Download Manager
frontend or a native Module 32 backend; relevant earlier work is present on
`feature/downloads-slice-32-1` and `feature/downloads-slice-32-6a-ui`.
**Do not treat those separate feature branches as merged or production-ready.**
33.2 may implement its bridge independently, but real browser-to-queue acceptance
must be blocked until the native job API exists.

## 2. User-facing capture policy

**Default**: Manual. The sole initial trigger is right-clicking a standard
HTTP(S) hyperlink, then choosing **Download with DK** in the browser.
The extension reads that selected link URL from `contextMenus.onClicked`
(`linkUrl`), not from a content script or scanning the active tab.
No website access permissions are necessary for this action.

| Event | Mode / requirement | Decision |
|---|---|---|
| Right-click HTTPS link | Manual, user gesture | Offer to installed native host |
| Right-click HTTP link | Manual, insecure HTTP disabled | Show additional warning/confirmation |
| Right-click loopback/private-IP link | Any manual mode | Native warning/approval required |
| Right-click data:/blob:/file:/ftp:/magnet: | Any | Reject as unsupported by Module 32 |
| Context menu on text / a page | Any | No submission; only a link context is valid |
| Ordinary browser download | Default manual mode | Browser handles it normally |
| Browser download event on allowed page | Opt-in `suggest` + optional `downloads` permission + proven site origin | Local suggestion only; user must explicitly accept |
| Browser download event with missing/ambiguous initiator origin | Opt-in `suggest` | Ignore; fail closed |
| Browser download event from unapproved site | Opt-in `suggest` | Ignore |
| In private/incognito window | Default | Ignore; never infer permission from browser allowance |
| Native host absent/fails/rejects | Any | Show a clear error; do not cancel/alter the browser download |
| Captured URL with credentials embedded | Any | Reject username/password in URL authority |
| Source link with signed query | Explicit user request | Pass only in transient message; native must protect it |
| Source link with fragment | Manual | Strip fragment before native transport; fragment never needed for HTTP transfer |

**No 'automatic interception' in 33.1.** `suggest` is an observation/
notification mode only, not background capture, browser download cancellation,
or guaranteed diversion. A browser `downloads.onCreated` event can fire after
the browser has begun a transfer. Cancelling at this stage risks duplicate,
partial or lost downloads. A different, browser-specific interception
implementation requires later approval and acceptance tests under 33.5.

Don't assume that `referrer` is an accurate initiating-page origin. In
suggestion mode, fail closed if origin is not reliably supplied and verified
against an *exact* allowlist. Never convert an untrusted page message into a
trusted user gesture.

## 3. Extension permissions (least privilege)

**Initial Chrome / Edge MV3 concept**, pending 33.3 compatibility verification:

```json
{
  "manifest_version": 3,
  "permissions": ["contextMenus", "nativeMessaging", "storage"],
  "optional_permissions": ["downloads"],
  "host_permissions": [],
  "optional_host_permissions": []
}
```

This is a permissions fragment, **not an installable manifest**. `contextMenus`
registers the right-click action, `nativeMessaging` invokes the installed
native host and `storage` stores local nonsecret preferences.

`downloads` is an optional runtime permission **only** for user-enabled
observation/suggestions. Request it from a clear extension settings
interaction, call `permissions.contains()` before using its API, and listen
for revocation. If denied, suggestions stay disabled; manual right-click
continues to work.

Do **not** request `cookies`, `history`, `tabs`, `activeTab`,
`webRequest`, `webRequestBlocking`, `debugger`, `scripting`,
`<all_urls>`, broad host permissions, clipboard access or
`declarativeNetRequest` for the manual MVP. No content script, DOM
inspection or password/token extraction is part of 33.1.

**Firefox / Zen:** build a *separate* browser-specific manifest, including a
stable Gecko extension ID in `browser_specific_settings.gecko.id` and
matching native host `allowed_extensions`. The Chromium host manifest uses
`allowed_origins` containing exact `chrome-extension://<real-extension-id>/`
origins. Neither accepts a wildcard extension identity. Never invent or
publish a final extension ID before packaging/signing.

Do not assume Zen has identical browser-specific permissions or native
messaging registration behaviour without manual testing in 33.4.

## 4. Settings schema and storage

Policy version: `1`; example:

```json
{
  "schemaVersion": 1,
  "mode": "manual",
  "allowedPageOrigins": [],
  "allowPrivateManual": false,
  "allowInsecureHttpManual": false,
  "confirmLocalNetwork": true
}
```

- `mode`: `manual` or `suggest`. There is no `automatic` value.
- `allowedPageOrigins`: list of **exact HTTP(S) origins**, including
  scheme and port; no wildcard entries, domain suffix matching, or paths.
- `allowPrivateManual`: default false. Can enable only after explicit
  local setting and the browser also permits incognito use; **never**
  enables automatic incognito suggestions.
- `allowInsecureHttpManual`: default false; HTTP requires additional
  warning when disabled. Native should still independently evaluate risks.
- `confirmLocalNetwork`: always true in version 1, **not user-disableable**.
  Host must also check resolved IPs, DNS rebinding and redirects independently.

Only nonsecret settings may use `browser.storage.local`. No URLs, browsing
histories, query strings, authentication headers, cookies, job contents,
download metadata or site tracking logs should be persisted by the extension.
The source URL is only in the short-lived user-initiated handoff request.
A signed download link can expose access to content: redact it from all
errors, diagnostic exports and the extension UI.

## 5. Native messaging transport and trust

Chromium/Firefox `runtime.sendNativeMessage` or `runtime.connectNative`
uses an **installed local native messaging host**, not HTTP/fetch, localhost
TCP, a web relay or the existing WebView2 `dk.local` page channel.
The native messaging host must mediate to the Module 32 coordinator.

The OS installer must register a per-user native messaging manifest for
each supported browser. Chrome/Edge and Firefox have **different** host
manifest authorization syntax. The host uses stdin/stdout with length-prefixed
UTF-8 JSON frames; stdout carries protocol frames only, diagnostics go to
stderr, with strict size limits. See official sources below.

Do not rely on a claimed extension name inside JSON for caller authentication.
Validate the browser-provided identity against a pinned extension allowlist
and expected native host registration; verify executable integrity and
installation path, with appropriate Windows user-account permissions. A nonce
or GUID alone is **not** cryptographic authentication.

Set a stricter **64 KiB native inbound/outbound** per-message cap, even if the
browser's implementation permits more. Also limit individual link URLs to
8,192 characters and filenames to 160 characters; reject unknown fields and
commands and use a bounded rate limit. Validate bytes using UTF-8 framing,
not JS string lengths. The JS policy applies an additional conservative
32,768-code-unit envelope guard.

The source of truth for site and redirect safety must be the native host:
validate HTTP(S) URL scheme and authority, IDN/punycode normalization,
username/password, DNS/IP classifications after resolution, redirects,
filename/path traversal, local-network protection, user consent and
job/queue permissions **again**. Never accept browser-supplied absolute
paths, command lines, executable flags or cookies.

### Message protocol, version 1

Two request types only in 33.1:

```json
{"protocolVersion":1,"id":"123e4567-e89b-42d3-a456-426614174000","type":"bridge.hello"}
```

```json
{"protocolVersion":1,"id":"123e4567-e89b-42d3-a456-426614174000",
 "type":"download.offer",
 "payload":{
   "trigger":"context_menu_link",
   "url":"https://files.example.org/archive.zip?signature=signed-token",
   "suggestedFileName":"archive.zip"
 }}
```

No `headers`, `cookies`, `pageUrl`, `referrer`, `userAgent`,
`destinationDirectory`, `webRequest` metadata or arbitrary action fields
are accepted by the `download.offer` schema. The URL is required and is the
only potentially secret-bearing field. Do not include any URL in protocol
logs or replies.

Exact JSON Schema contracts:

- [Request schema](../browser/contract/native-request-v1.schema.json)
- [Response schema](../browser/contract/native-response-v1.schema.json)
- [Policy evaluator](../browser/contract/capture-policy.mjs)
- [Pure tests](../browser/tests/capture-policy.test.mjs)

`bridge.hello` must return `ready:true`, `capabilities.manualOffer:true` and
`automaticHandoff:false` **only if** the native coordinator is reachable
and authorized. Never send a download offer after a failed handshake.

A successful `download.offer` response is a durable *queue acceptance*,
**not** evidence that the file has finished downloading:

```json
{"protocolVersion":1,"id":"123e4567-e89b-42d3-a456-426614174000",
 "ok":true,
 "result":{"accepted":true,"jobId":"123e4567-e89b-42d3-a456-426614174001",
           "state":"Queued"},
 "error":null}
```

On failure, respond with `ok:false`, `result:null`, safe `error.code`
and a generic (non-secret) `error.message`. Explicit failures include
`HOST_UNAVAILABLE`, `NOT_READY`, `PROTOCOL_UNSUPPORTED`,
`INVALID_MESSAGE`, `UNSUPPORTED_URL`, `USER_DECLINED`,
`CONFIRMATION_REQUIRED`, `RATE_LIMITED`, `PERMISSION_DENIED`,
`QUEUE_UNAVAILABLE`, `REQUEST_EXPIRED`, `NATIVE_ERROR`.
No raw errors/URLs in response.

The same request ID must be used across transient retries; the host must
deduplicate repeated requests and return the same job ID, rather than creating
duplicate jobs. Host should persist an expiring idempotency lookup for accepted
offers or otherwise guarantee deduplication over the retry window.

All sensitive target warnings (insecure HTTP, local/private addresses,
redirects to local services) must receive actual native-user confirmation.
A boolean flag supplied by the extension cannot prove consent.

### State sequence

```text
User right-clicks a link
  -> extension validates supported URL and local capture policy
  -> for sensitive source, user confirmation
  -> extension opens native host and performs bridge.hello
  -> browser/native identity and protocol/capabilities verified
  -> download.offer, correlated request ID
  -> native revalidates URL/redirect policy, destination & consent
  -> native enqueues via Module 32, commits job
  -> extension receives "accepted/Queued" or a safe error
  -> browser's own downloads remain untouched
```

If host missing, timed out, denied, not ready or user cancels, there must be
**no silent fallback download**, no unexpected duplicate job and no
automatic browser download cancellation.

## 6. Acceptance and security test matrix

| ID | Scenario | Expected |
|---|---|---|
| B01 | Right-click HTTPS link | Offer only after explicit user action |
| B02 | Right-click on plain text | No offer |
| B03 | Default browser download | Not observed or intercepted |
| B04 | No `downloads` permission | Manual still available |
| B05 | User opts into suggestions | Request optional permission interactively |
| B06 | Permission denied/revoked | Suggestions fail closed |
| B07 | Allowed site origin | Suggestion only, not silent handoff |
| B08 | Suffix-spoofed subdomain | Rejected |
| B09 | Unknown/missing referrer | No suggestion |
| B10 | Private/incognito mode | No automatic capture |
| B11 | Invalid or non-HTTP(S) URLs | Rejected |
| B12 | HTTP source | Requires extra confirmation |
| B13 | Loopback/private IP source | Native confirmation required |
| B14 | Redirect/DNS to private IP | Native revalidation blocks/prompt |
| B15 | Credential-bearing URL | Rejected |
| B16 | Signed query URL | Exact query delivered transiently, never logged |
| B17 | URL fragment | Removed before transport |
| B18 | Extra `headers`/`cookies` command | Schema rejects |
| B19 | Arbitrary command / wrong version | Reject |
| B20 | Missing/invalid request ID | Reject |
| B21 | Host absent/offline | Show error, browser unaffected |
| B22 | Host wrong extension ID | Reject access |
| B23 | Duplicate request ID | Exactly one durable queue job |
| B24 | Native queue rejects | Do not claim success/cancel browser download |
| B25 | Browser restart | Settings persist, no automatic replay of URLs |
| B26 | Extension removed | Module 32 works independently |
| B27 | Firefox/Zen manifest | Separate trusted ID and matching native host |
| B28 | Link length/frame limit exceeded | Reject safely |
| B29 | User denies native dialog | No queued job |
| B30 | Local/foreign page attempts message spoofing | No privileged host action |
| B31 | Download already in browser | Never cancel without separate proven handoff |
| B32 | WPF app not running | Native host connects to coordinator safely or reports NOT_READY |

**Pure test command**, on a local developer PC:

```powershell
node --test .\browser\tests\capture-policy.test.mjs
```

Pure tests cover B01, B03–B13, B15–B20 and core policy.
The rest require the real extension/native host or Windows acceptance.

## 7. Implementation ownership

- **33.1 Chat (this slice):** source design and deterministic policy/tests,
  manifests/permission specifications, strict schemas and handoff.
- **33.2 Codex:** Windows native host, registration/security, stdin/stdout
  framing, per-browser identity, fail-closed handshake, Module 32 bridge,
  replay/rate limits and integration tests.
- **33.3 Codex:** Chromium extension, Chrome+Edge permissions, context menu,
  user confirmation, optional suggestion UI, actual browser smoke tests.
- **33.4 Codex:** Firefox and Zen manifests/native host registration
  differences, real browser acceptance.
- **33.5 Codex:** authenticated edge cases, user permissions, signed URLs,
  safe retry, extension removal, and any approved non-destructive automatic
  handling; do not promise universal interception.

Do **not** build browser extensions or change Module 32/WPF code in 33.1.
No GitHub Actions, pull request or merge required for the specification.
The branch name does not match the existing `module/**` CI push filters.

## 8. Sources

- Chrome contextMenus: https://developer.chrome.com/docs/extensions/reference/api/contextMenus
- Chrome permissions: https://developer.chrome.com/docs/extensions/develop/concepts/declare-permissions
- Chrome downloads API: https://developer.chrome.com/docs/extensions/reference/api/downloads
- Chrome native messaging: https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging
- Firefox native messaging: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging
- Firefox optional permissions: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/optional_permissions
- Firefox menus: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/menus

## Completion report

| Area | Status |
|---|---|
| Browser capture policy | Implemented as pure JS; runtime integration pending |
| Permission matrix | Specified, official browser sources checked |
| Native request/response schemas | Committed, not enforced by a native host yet |
| Settings semantics / site allowlist | Pure implementation, no browser settings UI |
| Tests | Test vectors committed; see final implementation report for results |
| Extension UI/native code | Not started |
| Browser/Windows tests | Not run |
| Privacy | No telemetry/cookies/headers; protected signed URL flow specified |
| Dependency | Module 32 native coordinator and UI integration not verified on main |
| Next | 33.2 Codex: native bridge and security |

**Acronym key:** API = Application Programming Interface; MV3 = Manifest Version 3; URL = Uniform Resource Locator; HTTP(S) = Hypertext Transfer Protocol (Secure); JSON = JavaScript Object Notation; DNS = Domain Name System; WPF = Windows Presentation Foundation; UI = User Interface; IP = Internet Protocol.
