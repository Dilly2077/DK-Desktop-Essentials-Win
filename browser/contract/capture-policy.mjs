/**
 * DK Desktop Essentials, Module 33 / Slice 33.1.
 * Reusable, pure browser capture policy (no browser API calls and no network I/O).
 * This is not a working extension or native host; Codex will integrate it in 33.2–33.5.
 */
export const POLICY_VERSION = 1;
export const BRIDGE_PROTOCOL_VERSION = 1;

export const DEFAULT_CAPTURE_SETTINGS = Object.freeze({
  schemaVersion: POLICY_VERSION,
  mode: 'manual', // 'manual' or 'suggest'; NEVER silent automatic handoff
  allowedPageOrigins: Object.freeze([]),
  allowPrivateManual: false,
  allowInsecureHttpManual: false,
  confirmLocalNetwork: true
});

const VALID_MODES = new Set(['manual', 'suggest']);
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const MAX_URL_CHARS = 8192;
const MAX_MESSAGE_CHARS = 32768; // separate native byte-size cap: 64 KiB
const MAX_ORIGINS = 100;

function parseHttpUrl(input) {
  if (typeof input !== 'string' || input.length === 0 || input.length > MAX_URL_CHARS) return null;
  let parsed;
  try { parsed = new URL(input); } catch { return null; }
  if (!['http:', 'https:'].includes(parsed.protocol) || !parsed.hostname ||
      parsed.username || parsed.password) return null;
  return parsed;
}
function safeOrigin(input) {
  const url = parseHttpUrl(input);
  if (!url || url.pathname !== '/' || url.search || url.hash || /[*\s]/.test(url.hostname)) return null;
  return url.origin;
}
function isLocalAddress(hostname) {
  const name = String(hostname || '').replace(/^\[|\]$/g, '').toLowerCase();
  if (name === 'localhost' || name.endsWith('.localhost') || name === '::1' ||
      name.startsWith('fe80:') || name.startsWith('fc') || name.startsWith('fd')) return true;
  // Handles syntactically obvious IPv4 addresses only. Native must also check resolved IPs/redirects.
  const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(name);
  if (!match) return false;
  const bytes = match.slice(1).map(Number);
  if (bytes.some(x => x > 255)) return true;
  return bytes[0] === 10 || bytes[0] === 127 || bytes[0] === 0 ||
    (bytes[0] === 169 && bytes[1] === 254) ||
    (bytes[0] === 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
    (bytes[0] === 192 && bytes[1] === 168) ||
    (bytes[0] === 100 && bytes[1] >= 64 && bytes[1] <= 127);
}
export function normalizeCaptureSettings(input = {}) {
  if (input == null || typeof input !== 'object' || Array.isArray(input)) input = {};
  const seen = new Set();
  const origins = [];
  if (Array.isArray(input.allowedPageOrigins)) {
    for (const candidate of input.allowedPageOrigins.slice(0, MAX_ORIGINS)) {
      const origin = safeOrigin(candidate);
      if (origin && !seen.has(origin)) { seen.add(origin); origins.push(origin); }
    }
  }
  return {
    schemaVersion: POLICY_VERSION,
    mode: VALID_MODES.has(input.mode) ? input.mode : 'manual',
    allowedPageOrigins: origins,
    allowPrivateManual: input.allowPrivateManual === true,
    allowInsecureHttpManual: input.allowInsecureHttpManual === true,
    confirmLocalNetwork: true // fixed safe default; native confirmation remains mandatory
  };
}
export function classifyDownloadUrl(candidate) {
  const url = parseHttpUrl(candidate);
  if (!url) return { ok: false, reason: 'UNSUPPORTED_OR_INVALID_URL' };
  return {
    ok: true,
    origin: url.origin,
    displayHost: url.hostname,
    insecureHttp: url.protocol === 'http:',
    potentiallyLocal: isLocalAddress(url.hostname)
  };
}
export function evaluateCapture(event, rawSettings = DEFAULT_CAPTURE_SETTINGS) {
  const s = normalizeCaptureSettings(rawSettings);
  if (!event || typeof event !== 'object' || Array.isArray(event))
    return { decision: 'ignore', reason: 'BAD_EVENT' };
  const classification = classifyDownloadUrl(event.linkUrl);
  if (!classification.ok) return { decision: 'ignore', reason: classification.reason };
  if (event.trigger === 'context_menu_link') {
    if (event.incognito === true && !s.allowPrivateManual)
      return { decision: 'ignore', reason: 'PRIVATE_MODE_DISABLED' };
    if (classification.insecureHttp && !s.allowInsecureHttpManual)
      return { decision: 'confirm', reason: 'INSECURE_HTTP', displayHost: classification.displayHost };
    if (classification.potentiallyLocal)
      return { decision: 'confirm', reason: 'LOCAL_NETWORK_TARGET', displayHost: classification.displayHost };
    return { decision: 'offer', reason: 'EXPLICIT_USER_ACTION', displayHost: classification.displayHost };
  }
  if (event.trigger === 'browser_download_created') {
    if (s.mode !== 'suggest') return { decision: 'ignore', reason: 'SUGGESTIONS_DISABLED' };
    if (event.downloadsPermission !== true) return { decision: 'ignore', reason: 'PERMISSION_NOT_GRANTED' };
    if (event.incognito === true) return { decision: 'ignore', reason: 'PRIVATE_MODE_DISABLED' };
    // A browser's referrer can be missing/unreliable: never guess site provenance.
    const page = event.initiatingPageOrigin;
    if (!page || !safeOrigin(page) || !s.allowedPageOrigins.includes(safeOrigin(page)))
      return { decision: 'ignore', reason: 'SITE_NOT_ALLOWED_OR_UNKNOWN' };
    if (classification.insecureHttp || classification.potentiallyLocal)
      return { decision: 'ignore', reason: 'SENSITIVE_TARGET' };
    return { decision: 'suggest', reason: 'USER_CONFIRMATION_REQUIRED', displayHost: classification.displayHost };
  }
  return { decision: 'ignore', reason: 'UNSUPPORTED_TRIGGER' };
}
function suggestedFileName(input) {
  if (typeof input !== 'string') return undefined;
  const cleaned = input.replace(/[\\/:*?"<>|\x00-\x1f]/g, '_').replace(/[. ]+$/g, '').slice(0, 160);
  return cleaned || undefined;
}
export function makeManualOffer({ id, linkUrl, fileName, confirmedWarnings = false } = {}) {
  if (!UUID_PATTERN.test(String(id || ''))) throw new TypeError('Invalid request identifier');
  const result = classifyDownloadUrl(linkUrl);
  if (!result.ok) throw new TypeError('Unsupported download link');
  if ((result.insecureHttp || result.potentiallyLocal) && confirmedWarnings !== true)
    throw new TypeError('An explicit additional warning acknowledgement is required');
  // Query string may contain a bearer token; only pass in the explicit user action.
  // A fragment is never sent to the HTTP server and may expose client-side secrets.
  const url = new URL(linkUrl);
  url.hash = '';
  const payload = { url: url.href, trigger: 'context_menu_link' };
  const cleanFileName = suggestedFileName(fileName);
  if (cleanFileName) payload.suggestedFileName = cleanFileName;
  const message = { protocolVersion: BRIDGE_PROTOCOL_VERSION, id, type: 'download.offer', payload };
  if (JSON.stringify(message).length > MAX_MESSAGE_CHARS) throw new TypeError('Message too large');
  return message;
}
export function validateBrowserMessage(input) {
  let encoded;
  try { encoded = JSON.stringify(input); }
  catch { return { ok: false, reason: 'INVALID_ENVELOPE' }; }
  if (!input || typeof input !== 'object' || Array.isArray(input) ||
      input.protocolVersion !== BRIDGE_PROTOCOL_VERSION ||
      typeof input.id !== 'string' || !UUID_PATTERN.test(input.id) ||
      typeof encoded !== 'string' || encoded.length > MAX_MESSAGE_CHARS)
    return { ok: false, reason: 'INVALID_ENVELOPE' };
  if (Object.keys(input).some(key => !['protocolVersion', 'id', 'type', 'payload'].includes(key)))
    return { ok: false, reason: 'UNEXPECTED_FIELD' };
  if (input.type === 'bridge.hello') {
    if (input.payload !== undefined &&
        (!input.payload || typeof input.payload !== 'object' ||
         Array.isArray(input.payload) || Object.keys(input.payload).length))
      return { ok: false, reason: 'UNEXPECTED_PAYLOAD' };
    return { ok: true, type: 'bridge.hello' };
  }
  if (input.type === 'download.offer') {
    const p = input.payload;
    if (!p || typeof p !== 'object' || Array.isArray(p) ||
        p.trigger !== 'context_menu_link' ||
        typeof p.url !== 'string' || !classifyDownloadUrl(p.url).ok ||
        new URL(p.url).hash !== '' ||
        (p.suggestedFileName !== undefined &&
         (typeof p.suggestedFileName !== 'string' ||
          suggestedFileName(p.suggestedFileName) !== p.suggestedFileName)))
      return { ok: false, reason: 'INVALID_DOWNLOAD_OFFER' };
    const allowed = ['url', 'trigger', 'suggestedFileName'];
    if (Object.keys(p).some(key => !allowed.includes(key))) return { ok: false, reason: 'UNEXPECTED_FIELD' };
    return { ok: true, type: 'download.offer' };
  }
  return { ok: false, reason: 'UNKNOWN_COMMAND' };
}
export function redactedOfferSummary(message) {
  const p = message?.payload;
  const classification = classifyDownloadUrl(p?.url);
  return Object.freeze({
    type: message?.type === 'download.offer' ? 'download.offer' : 'unknown',
    host: classification.ok ? classification.displayHost : 'unknown',
    // Never expose URL path, query, fragment, headers, cookies or auth tokens.
    requestId: typeof message?.id === 'string' ? message.id : null
  });
}
