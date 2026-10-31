/* Module 32 — pure, deterministic presentation helpers. No networking or storage. */
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.DKDownloadsCore = api;
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';
  const STATES = ['Queued','Resolving','Downloading','Paused','Retrying','Verifying','Completed','Failed','Cancelled'];
  const BUSY = new Set(['Resolving','Downloading','Verifying']);
  const PENDING = new Set(['Queued','Retrying']);
  const PAUSABLE = new Set(['Queued','Resolving','Downloading','Retrying','Verifying']);
  const CANCELLABLE = new Set(['Queued','Resolving','Downloading','Paused','Retrying','Verifying','Failed']);
  const MOCK_ERROR_MESSAGES = Object.freeze({
    NetworkUnavailable:'Network unavailable. Check your connection and try again.',
    Timeout:'The transfer timed out. You can retry.',
    HttpNotFound:'The source file was not found (HTTP 404).',
    HttpForbidden:'The server refused access (HTTP 403).',
    HttpRateLimited:'The server is limiting requests. Retry later.',
    DiskFull:'Not enough free disk space for this download.',
    PermissionDenied:'The destination folder is not writable.',
    RemoteContentChanged:'The remote file changed. Its partial data cannot safely be appended.',
    RangeNotSupported:'This server does not support continuing this download.',
    ContentLengthMismatch:'The downloaded size did not match the expected size.',
    ChecksumMismatch:'The downloaded file failed integrity verification.',
    EngineUnavailable:'The download engine is unavailable.'
  });
  function number(value) { return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, value) : null; }
  function bytes(value) {
    const n = number(value);
    if (n === null) return 'Unknown';
    if (n < 1024) return Math.round(n) + ' B';
    const unit = Math.min(4, Math.floor(Math.log(n) / Math.log(1024)));
    return (n / 1024 ** unit).toFixed(n / 1024 ** unit >= 10 ? 1 : 2).replace(/\.0+$/, '') + ' ' + ['B','KB','MB','GB','TB'][unit];
  }
  function duration(seconds) {
    const n = number(seconds);
    if (n === null) return 'Unknown';
    if (n > 359999) return '> 99 h';
    const s = Math.ceil(n);
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
    return h ? h + ' h ' + m + ' m' : m ? m + ' m ' + (s % 60) + ' s' : s + ' s';
  }
  function progress(job) {
    const done = number(job.transferredBytes) || 0, total = number(job.expectedBytes);
    if (total === null || total <= 0) return null;
    return Math.min(100, Math.max(0, Math.round(done / total * 100)));
  }
  function eta(job) {
    if (job.state !== 'Downloading') return null;
    const rate = number(job.bytesPerSecond), total = number(job.expectedBytes);
    if (rate === null || rate <= 0 || total === null) return null;
    return Math.max(0, (total - (number(job.transferredBytes) || 0)) / rate);
  }
  function kind(name) {
    const ext = String(name || '').split('.').pop().toLowerCase();
    return ({zip:'Archive',rar:'Archive','7z':'Archive',exe:'Application',msi:'Installer',
      pdf:'PDF document',mp4:'Video',mkv:'Video',mov:'Video',mp3:'Audio',flac:'Audio',
      png:'Image',jpg:'Image',jpeg:'Image',webp:'Image',iso:'Disk image',txt:'Text document',
      csv:'Spreadsheet'})[ext] || 'File';
  }
  function displayName(job) { return String(job.fileName || job.filename || 'Unnamed download'); }
  function normalize(job) {
    if (!job || typeof job !== 'object') throw new TypeError('A download job must be an object.');
    const state = STATES.includes(job.state) ? job.state : 'Queued';
    const total = number(job.expectedBytes);
    const done = number(job.transferredBytes) ?? 0;
    return {
      id: String(job.id ?? ''), state, fileName: displayName(job),
      displayHost: String(job.displayHost || 'Unknown source'),
      destinationDirectory: job.destinationDirectory ? String(job.destinationDirectory) : null,
      transferredBytes: done, expectedBytes: total, bytesPerSecond: number(job.bytesPerSecond),
      createdUtc: job.createdUtc || null, updatedUtc: job.updatedUtc || null,
      priority: String(job.priority || 'Normal'), errorCode: job.errorCode || job.lastErrorCode || null,
      safeErrorDetail: job.safeErrorDetail || null,
      resumeCapability: ['Supported','Unsupported','Unknown'].includes(job.resumeCapability)
        ? job.resumeCapability : 'Unknown',
      capabilities: { ...job.capabilities }, revision: Number.isInteger(job.revision) ? job.revision : 0,
      attempts:number(job.attempts),nextAttemptUtc:job.nextAttemptUtc||null,queuePosition:number(job.queuePosition)
    };
  }
  function statusGroup(job) {
    if (BUSY.has(job.state)) return 'active';
    if (PENDING.has(job.state)) return 'queued';
    return job.state.toLowerCase();
  }
  function actions(job, adapterCapabilities = {}) {
    const c = adapterCapabilities || {};
    const supported = name => c[name] !== false && job.capabilities?.[name] !== false;
    return {
      start: job.state === 'Queued' && supported('start'),
      pause: PAUSABLE.has(job.state) && supported('pause'),
      resume: job.state === 'Paused' && job.resumeCapability !== 'Unsupported' && supported('resume'),
      retry: job.state === 'Failed' && supported('retry'),
      cancel: CANCELLABLE.has(job.state) && supported('cancel'),
      remove: !BUSY.has(job.state) && supported('remove'),
      openFolder: job.state === 'Completed' && supported('openFolder')
    };
  }
  function filter(jobs, query = '', status = 'all') {
    const q = String(query).trim().toLocaleLowerCase();
    return jobs.filter(j => (status === 'all' || statusGroup(j) === status) &&
      (!q || [j.fileName, j.displayHost, j.errorCode, j.state].some(x => String(x || '').toLocaleLowerCase().includes(q))));
  }
  function summary(jobs) {
    const counts = {all:jobs.length,active:0,queued:0,paused:0,completed:0,failed:0,cancelled:0};
    jobs.forEach(j => { const type = statusGroup(j); if (Object.hasOwn(counts,type)) counts[type]++; });
    return counts;
  }
  function validateSource(value) {
    const source = String(value || '').trim();
    if (!source) return {ok:false,error:'Paste an HTTP or HTTPS link.'};
    let parsed;
    try { parsed = new URL(source); } catch { return {ok:false,error:'Enter a valid download URL.'}; }
    if (!['http:','https:'].includes(parsed.protocol))
      return {ok:false,error:'Only HTTP and HTTPS links are supported by Module 32.'};
    if (parsed.username || parsed.password)
      return {ok:false,error:'Links containing embedded usernames or passwords are not supported.'};
    if (parsed.hash) return {ok:false,error:'Links containing a fragment are not supported. Use the direct HTTP or HTTPS link.'};
    if (!parsed.hostname) return {ok:false,error:'The URL needs a hostname.'};
    return {ok:true,sourceUrl:parsed.href,host:parsed.hostname,
      filename:decodeFilename(parsed.pathname.split('/').pop())};
  }
  function decodeFilename(segment) {
    let v = String(segment || '');
    try { v = decodeURIComponent(v); } catch { /* raw segment is fine */ }
    v = v.replace(/[\\/:*?"<>|\x00-\x1f]/g,'_').replace(/[. ]+$/g,'').slice(0,160);
    return v || 'download';
  }
  function errorText(job) {
    return job.safeErrorDetail || MOCK_ERROR_MESSAGES[job.errorCode] || (job.errorCode ? 'Download failed (' + job.errorCode + ').' : 'No errors reported.');
  }
  return Object.freeze({ STATES, BUSY, normalize, bytes, duration, progress, eta, kind, actions,
    filter, summary, statusGroup, validateSource, decodeFilename, errorText });
});
