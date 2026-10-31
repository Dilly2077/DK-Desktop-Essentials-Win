(() => {
  'use strict';
  const pending = new Map();
  const stopping = new Set();
  let destination = null, polling = false, recoveryBlocked = false;
  const bridge = window.chrome?.webview;
  const request = (command, payload = {}) => new Promise((resolve, reject) => {
    if (!bridge) { reject(new Error('Open this tool in the Windows app to download files.')); return; }
    const requestId = crypto.randomUUID();
    // Native folder selection stays pending until the user confirms or cancels it.
    const timeout = command === 'chooseDestination' ? null : setTimeout(() => { pending.delete(requestId); reject(new Error('The app did not respond. Refresh the session list before trying again.')); }, 30000);
    pending.set(requestId, { resolve, reject, timeout });
    bridge.postMessage({ type: `downloads:${command}`, requestId, payload });
  });
  bridge?.addEventListener('message', event => {
    const response = event.data;
    if (response?.type !== 'downloads:response') return;
    const task = pending.get(response.requestId);
    if (!task) return;
    clearTimeout(task.timeout); pending.delete(response.requestId);
    if (response.ok) task.resolve(response.result);
    else task.reject(new Error(response.error || 'The download request failed.'));
  });
  const bytes = value => {
    if (value == null) return 'Unknown size';
    const unit = value < 1024 ? 0 : Math.min(3, Math.floor(Math.log(value) / Math.log(1024)));
    return `${(value / 1024 ** unit).toFixed(unit ? 1 : 0)} ${['B','KB','MB','GB'][unit]}`;
  };
  const eta = value => value == null ? 'ETA unknown' : value < 60 ? `${Math.ceil(value)}s left` : `${Math.ceil(value / 60)}m left`;
  const message = text => { const area = document.getElementById('download-message'); if (area) area.textContent = text; };
  async function refresh() {
    const list = document.getElementById('download-jobs');
    if (!list || polling) return;
    polling = true;
    try {
      const jobs = await request('list');
      if (!list.isConnected) return;
      const scrollPanel = list.closest('main'), scrollTop = scrollPanel?.scrollTop;
      const focused = document.activeElement;
      const focusedJob = focused?.closest('[data-job-id]')?.dataset.jobId, focusedAction = focused?.dataset.action;
      const content = document.createDocumentFragment();
      if (!jobs.length) { const empty = document.createElement('p'); empty.className = 'download-muted'; empty.textContent = 'Your downloads will appear here. Recovered jobs wait for Resume.'; content.append(empty); }
      jobs.forEach(job => {
        const row = document.createElement('article'); row.className = 'download-job'; row.dataset.jobId = job.id;
        const header = document.createElement('div'); header.className = 'download-job-heading';
        const name = document.createElement('strong'); name.textContent = job.fileName;
        const state = document.createElement('span'); state.className = 'download-state'; state.textContent = job.state + (job.queuePosition ? ` · Queue ${job.queuePosition}` : '');
        header.append(name, state);
        const host = document.createElement('p'); host.className = 'download-muted'; host.textContent = job.displayHost;
        const meter = document.createElement('progress'); meter.setAttribute('aria-label', `Progress for ${job.fileName}`);
        if (job.expectedBytes != null) { meter.max = Math.max(1, job.expectedBytes); meter.value = job.expectedBytes === 0 && job.state === 'Completed' ? 1 : job.transferredBytes; }
        const stats = document.createElement('p'); stats.className = 'download-muted';
        stats.textContent = `${bytes(job.transferredBytes)} / ${bytes(job.expectedBytes)}${job.state === 'Downloading' ? ` · ${bytes(job.bytesPerSecond)}/s · ${eta(job.etaSeconds)}` : ''}`;
        row.append(header, host, meter, stats);
        if (job.attempts) {
          const attempt = document.createElement('p'); attempt.className = 'download-muted';
          attempt.textContent = `Attempt ${job.attempts}${job.nextAttemptUtc ? ` · Retry after ${new Date(job.nextAttemptUtc).toLocaleTimeString()}` : ''}`;
          row.append(attempt);
        }
        if (job.errorCode) { const error = document.createElement('p'); error.className = 'download-error'; error.textContent = `${job.safeErrorDetail || job.errorCode} Temporary data, if any, has been retained.`; row.append(error); }
        if (job.canResume) {
          const resume = document.createElement('button'); resume.type = 'button'; resume.className = 'download-secondary'; resume.textContent = 'Resume';
          resume.dataset.action = 'resume';
          resume.disabled = stopping.has(job.id);
          resume.onclick = async () => { stopping.add(job.id); resume.disabled = true; try { await request('resume', {id:job.id}); message('Resume submitted. The remote file and local checkpoint will be checked.'); } catch(error) { message(error.message); } finally { stopping.delete(job.id); await refresh(); } };
          row.append(resume);
          if (job.resumeCapability === 'Unsupported' && job.transferredBytes > 0) { const note = document.createElement('p'); note.className='download-muted'; note.textContent='The server has not established safe resume support. Resume may be refused; retained bytes will not be overwritten.'; row.append(note); }
        } else if (['Queued','Resolving','Downloading','Verifying','Retrying'].includes(job.state)) {
          const pause = document.createElement('button'); pause.type='button'; pause.className='download-secondary'; pause.textContent=stopping.has(job.id)?'Stopping…':'Pause'; pause.disabled=stopping.has(job.id);
          pause.dataset.action = 'pause';
          pause.onclick=async()=>{ stopping.add(job.id); pause.disabled=true; pause.textContent='Stopping…'; try { const result=await request('pause',{id:job.id}); message(result.state==='Completed'?'The download completed before pausing.':'Paused. Flushed bytes are retained for a validated resume.'); } catch(error){message(error.message);} finally{stopping.delete(job.id);await refresh();} };
          row.append(pause);
        }
        if (job.canRetry) {
          const retry = document.createElement('button'); retry.type = 'button'; retry.className = 'download-secondary'; retry.textContent = 'Retry';
          retry.dataset.action = 'retry';
          retry.disabled = recoveryBlocked || stopping.has(job.id);
          retry.onclick = async () => { stopping.add(job.id); retry.disabled = true; try { await request('retry', {id:job.id}); message('Retry queued. Retained bytes will be validated before reuse.'); } catch(error) { message(error.message); } finally { stopping.delete(job.id); await refresh(); } };
          row.append(retry);
        }
        if (['Queued','Resolving','Downloading','Verifying','Paused','Retrying','Failed'].includes(job.state)) {
          const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'download-secondary'; cancel.textContent = 'Cancel';
          cancel.dataset.action = 'cancel';
          cancel.disabled = stopping.has(job.id); if (cancel.disabled) cancel.textContent = 'Stopping…';
          cancel.onclick = async () => { stopping.add(job.id); cancel.disabled = true; cancel.textContent = 'Stopping…'; try { const result = await request('cancel', { id: job.id }); message(result.state === 'Completed' ? 'The download completed before cancellation.' : 'Cancellation acknowledged. Temporary data has been retained.'); await refresh(); } catch (error) { message(error.message); } finally { stopping.delete(job.id); } };
          row.append(cancel);
        }
        content.append(row);
      });
      list.replaceChildren(content);
      if (focusedJob && focusedAction) {
        const replacement = Array.from(list.querySelectorAll('button[data-action]')).find(button => button.dataset.action === focusedAction && button.closest('[data-job-id]').dataset.jobId === focusedJob);
        if (replacement && !replacement.disabled) replacement.focus({preventScroll:true});
      }
      if (scrollPanel && scrollTop != null) scrollPanel.scrollTop = scrollTop;
    } catch (error) { message(error.message); }
    finally { polling = false; }
  }
  function mount(panel) {
    panel.innerHTML = `<header class="download-hero"><div class="eyebrow">DOWNLOADS &amp; TRANSFERS</div><h1>Download Manager</h1><p>Save a direct HTTP or HTTPS link to your chosen folder.</p><span class="tag">Local queue &amp; recovery</span></header>
      <section class="download-card"><h2>New download</h2><form id="download-form" autocomplete="off">
      <label for="download-url">Download URL</label><input id="download-url" type="url" required maxlength="16384" placeholder="https://example.org/file.zip" autocomplete="off" spellcheck="false">
      <label for="download-name">Save as</label><input id="download-name" type="text" value="download.bin" required maxlength="255" spellcheck="false">
      <div class="download-destination"><button id="download-choose" type="button" class="download-secondary">Choose folder…</button><span id="download-folder">No folder selected</span></div>
      <label for="download-hash">Expected SHA-256 <span class="download-muted">(optional)</span></label><input id="download-hash" type="text" maxlength="64" spellcheck="false" placeholder="64 hexadecimal characters">
      <label for="download-segments">Connections</label><select id="download-segments"><option value="1">1 — standard transfer</option><option value="2">Up to 2 — where validated ranges are supported</option><option value="4">Up to 4 — where validated ranges are supported</option></select>
      <label for="download-priority">Queue priority</label><select id="download-priority"><option>Normal</option><option>High</option><option>Low</option></select>
      <p class="download-muted">Download adds the link to your local queue. Work starts when a slot is available; temporary connection and server failures retry within your attempt limit. Pause and closing the app retain flushed bytes and protected recovery data. Recovered jobs wait for Resume unless you enable automatic starts below. Resume depends on the server proving the file is unchanged. Signed URLs may expire. Cancel retains partial files.</p>
      <button id="download-submit" type="submit" class="download-primary">Download</button></form><p id="download-message" role="status" aria-live="polite"></p></section>
      <section class="download-card"><h2>Queue settings</h2><form id="download-settings">
      <label for="download-concurrency">Simultaneous downloads</label><input id="download-concurrency" type="number" min="1" max="16" value="3" required>
      <label for="download-attempts">Attempts per download, including the first</label><input id="download-attempts" type="number" min="1" max="20" value="5" required>
      <label class="download-checkbox" for="download-autostart"><input id="download-autostart" type="checkbox">Start queued downloads automatically when opening the app</label>
      <p class="download-muted">Automatic starts are off by default. Enabling them applies to future launches and lets saved, previously authorized links use the network. Paused downloads stay paused. Reducing concurrency lets active downloads finish before starting more.</p>
      <button id="download-save-settings" type="submit" class="download-secondary" disabled>Save queue settings</button></form></section>
      <section class="download-card"><div class="download-job-heading"><h2>Downloads</h2><button id="download-refresh" type="button" class="download-secondary">Refresh</button></div><p class="download-muted">Completed means the file was saved and verified. Files are never opened automatically; completion does not mean a file is safe.</p><div id="download-jobs"></div></section>`;
    if (destination) panel.querySelector('#download-folder').textContent = destination.displayName;
    panel.querySelector('#download-choose').onclick = async event => {
      event.target.disabled = true;
      try { const result = await request('chooseDestination'); if (result && panel.isConnected) { destination = result; panel.querySelector('#download-folder').textContent = result.displayName; } }
      catch (error) { message(error.message); }
      finally { event.target.disabled = false; }
    };
    panel.querySelector('#download-form').onsubmit = async event => {
      event.preventDefault();
      if (!destination) { message('Choose a destination folder first.'); return; }
      const submit = panel.querySelector('#download-submit'); submit.disabled = true;
      const url = panel.querySelector('#download-url');
      try {
        await request('enqueue', { url: url.value, fileName: panel.querySelector('#download-name').value,
          destinationToken: destination.destinationToken, expectedSha256: panel.querySelector('#download-hash').value.trim(),
          parallelSegments: Number(panel.querySelector('#download-segments').value), priority: panel.querySelector('#download-priority').value });
        url.value = '';
        message('Download submitted.'); await refresh();
      } catch (error) { message(error.message); }
      finally { submit.disabled = recoveryBlocked; }
    };
    const applySettings = settings => {
      panel.querySelector('#download-concurrency').value = settings.maxConcurrentDownloads;
      panel.querySelector('#download-attempts').value = settings.maxAttempts;
      panel.querySelector('#download-autostart').checked = settings.autoStartOnLaunch;
    };
    panel.querySelector('#download-settings').onsubmit = async event => {
      event.preventDefault(); const save = panel.querySelector('#download-save-settings'); save.disabled = true;
      try {
        applySettings(await request('setSettings', { maxConcurrentDownloads: Number(panel.querySelector('#download-concurrency').value),
          maxAttempts: Number(panel.querySelector('#download-attempts').value), autoStartOnLaunch: panel.querySelector('#download-autostart').checked }));
        message('Queue settings saved locally.'); await refresh();
      } catch(error) { message(error.message); } finally { save.disabled = recoveryBlocked; }
    };
    request('getSettings').then(settings => { if (panel.isConnected) { applySettings(settings); panel.querySelector('#download-save-settings').disabled = recoveryBlocked; } }).catch(error => message(error.message));
    panel.querySelector('#download-refresh').onclick = refresh;
    request('status').then(status => { recoveryBlocked = status.recoveryBlocked; if(recoveryBlocked && panel.isConnected) { message('Local queue data is unavailable or another app instance owns it. Downloads are blocked to protect retained files. Close other instances and inspect local recovery data before trying again.'); panel.querySelector('#download-submit').disabled=true; panel.querySelector('#download-save-settings').disabled=true; } }).catch(error=>message(error.message));
    refresh();
  }
  setInterval(() => { if (document.getElementById('download-jobs')) refresh(); }, 1000);
  window.DKDownloads = { mount };
})();
