/* Slice 32.6A frontend: rendered inside the existing Command Center tool detail.
   It never performs downloads; the injected adapter is responsible for every operation. */
(function (root) {
  'use strict';
  const core = root.DKDownloadsCore;
  const mock = root.DKDownloadsMock;
  if (!core) throw new Error('downloads-core.js must load before downloads-ui.js');
  const h = v => String(v ?? '').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
  const groups = [
    ['all','All'],['active','Active'],['queued','Queued'],['paused','Paused'],
    ['completed','Completed'],['failed','Failed'],['cancelled','Cancelled']
  ];
  let instance = null;
  function badge(state) { return '<span class="dkd-status dkd-state-' + h(state.toLowerCase()) + '">' + h(state) + '</span>'; }
  function button(action,id,label,enabled,title) {
    return '<button type="button" class="dkd-action" data-job-action="' + h(action) +
      '" data-id="' + h(id) + '"' + (enabled?'':' disabled') +
      ' title="' + h(title || label) + '" aria-label="' + h(label) + '">' + h(label) + '</button>';
  }
  function formatDate(v) {
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? 'Unknown' : d.toLocaleString();
  }
  function row(job,caps,expanded) {
    const a=core.actions(job,caps), p=core.progress(job), state=job.state;
    const done=core.bytes(job.transferredBytes), total=core.bytes(job.expectedBytes);
    const active=core.BUSY.has(state);
    const speed=active && job.bytesPerSecond>0 ? core.bytes(job.bytesPerSecond)+'/s':'—';
    const rem=core.eta(job), eta=rem===null?'—':core.duration(rem);
    const name=job.fileName;
    const controls=[
      ...(state==='Queued'?[button('start',job.id,'Start',a.start)]:[]),
      ...(active||state==='Queued'?[button('pause',job.id,'Pause',a.pause)]:[]),
      ...(state==='Paused'?[button('resume',job.id,'Resume',a.resume,
        job.resumeCapability==='Unsupported'?'Resume unavailable: the server does not support continuing this file.':'Resume if supported.')]:[]),
      ...(state==='Failed'?[button('retry',job.id,'Retry',a.retry)]:[]),
      ...(state!=='Completed'&&state!=='Cancelled'?[button('cancel',job.id,'Cancel',a.cancel)]:[]),
      ...(state==='Completed'?[button('openFolder',job.id,'Open folder',a.openFolder,
        caps.openFolder===false?'Unavailable in demo mode; no file was saved.':'Open the containing folder.')]:[]),
      ...(!active?[button('remove',job.id,'Remove',a.remove,'Remove the entry without deleting any real file.')]:[])
    ];
    return '<article class="dkd-download" data-job="' + h(job.id) + '">' +
      '<div class="dkd-entry"><div class="dkd-file-icon" aria-hidden="true">↓</div>' +
      '<div class="dkd-item-main"><div class="dkd-title-line"><div class="dkd-filename" title="' + h(name) + '">' + h(name) + '</div>' + badge(state) + '</div>' +
      '<div class="dkd-sub">' + h(core.kind(name)) + ' · ' + h(job.displayHost) + '</div></div>' +
      '<div class="dkd-size"><strong>' + h(done) + '</strong><span> / ' + h(total) + '</span></div></div>' +
      '<div class="dkd-progress" role="progressbar" aria-label="' + h(name) + ' progress" aria-valuemin="0" aria-valuemax="100"' +
      (p===null?' aria-valuetext="Unknown total size"':' aria-valuenow="'+p+'"') +'>'+
      '<span style="width:' + (p===null ? 0 : p) + '%"></span></div>' +
      '<div class="dkd-bottom"><div class="dkd-metrics">' +
      '<span>' + (p===null?'Size unknown':p+'%') + '</span>' +
      '<span>Speed: ' + h(speed) + '</span><span>Remaining: ' + h(eta) + '</span></div>' +
      '<div class="dkd-controls">' +
      controls.join('') +
      '<button type="button" class="dkd-action dkd-info-button" data-details="' + h(job.id) + '" aria-expanded="' + (expanded?'true':'false') + '" aria-label="Details for ' + h(name) + '">' + (expanded?'Hide details':'Details') + '</button>' +
      '</div></div>' +
      (expanded ? details(job) : '') + '</article>';
  }
  function details(job) {
    const code=job.errorCode?'<p class="dkd-alert-inline" role="status">' + h(core.errorText(job)) + '</p>':'';
    const resume=job.resumeCapability==='Supported'?'Supported by source (reported)':
      job.resumeCapability==='Unsupported'?'Not supported':'Unknown — server must confirm';
    return '<section class="dkd-details" aria-label="Download details">' + code +
      '<dl><div><dt>Source host</dt><dd>' + h(job.displayHost) + '</dd></div>' +
      '<div><dt>File type</dt><dd>' + h(core.kind(job.fileName)) + '</dd></div>' +
      '<div><dt>Destination</dt><dd class="dkd-wrap">' + h(job.destinationDirectory || 'Not reported') + '</dd></div>' +
      '<div><dt>Received</dt><dd>' + h(core.bytes(job.transferredBytes)) + '</dd></div>' +
      '<div><dt>Expected size</dt><dd>' + h(core.bytes(job.expectedBytes)) + '</dd></div>' +
      '<div><dt>Resuming</dt><dd>' + h(resume) + '</dd></div>' +
      '<div><dt>Last update</dt><dd>' + h(formatDate(job.updatedUtc)) + '</dd></div>' +
      '<div><dt>Error code</dt><dd>' + h(job.errorCode || 'None') + '</dd></div></dl>' +
      '<p class="dkd-details-foot">Full URLs and credentials are intentionally not displayed in the download list.</p></section>';
  }
  function render(instance,keepFocus) {
    if (instance.dead) return;
    const {host,jobs,adapter}=instance;
    const caps=adapter.capabilities||{};
    const demo=caps.mode==='mock';
    const counts=core.summary(jobs);
    const matched=core.filter(jobs,instance.query,instance.filter);
    const shown=matched.slice(0,instance.limit);
    let content='';
    if (instance.loading) content='<div class="dkd-notice" role="status">Loading download jobs…</div>';
    else if (instance.loadError) content='<div class="dkd-error" role="alert">' + h(instance.loadError) +
      ' <button type="button" data-dkd="refresh">Try again</button></div>';
    else if (!matched.length) content='<div class="dkd-empty"><div class="dkd-empty-symbol" aria-hidden="true">↓</div>' +
      '<h3>' + (jobs.length?'No matching downloads':'No downloads yet') + '</h3><p>' +
      (jobs.length?'Try another status or clear the search.':'Use New download to create a job. In demo mode, no file is saved.') +
      '</p>' + (jobs.length?'<button type="button" class="dkd-soft" data-dkd="clear">Clear filters</button>':'') + '</div>';
    else content='<div class="dkd-list" aria-label="Download jobs">' + shown.map(j=>row(j,caps,instance.expanded===j.id)).join('') + '</div>' +
      (shown.length<matched.length?'<button type="button" class="dkd-soft dkd-load-more" data-dkd="more">Show next 50 (' + (matched.length-shown.length) + ' remaining)</button>':'');
    const banner=demo ?
      '<div class="dkd-banner" role="note"><strong>DEMO MODE · simulated downloads</strong><span>These are test records. No network requests, disk writes or real downloads occur.</span></div>' :
      '<div class="dkd-banner" role="note"><strong>Native download manager</strong><span>Actions use the connected local backend.</span></div>';
    host.innerHTML='<div class="dk-downloads">' +
      '<div class="dkd-top"><div><button type="button" class="dkd-back" data-dkd="back">← Back to Downloads &amp; Transfers</button>' +
      '<div class="eyebrow">CATEGORY 7 / MODULE 32</div><h1>Download Manager</h1>' +
      '<p class="dkd-description">Manage direct downloads from one private, local interface.</p></div>' +
      '<button type="button" class="dkd-main-button" data-dkd="new">+ New download</button></div>' +
      banner +
      '<div class="dkd-stats" aria-label="Download summary">' +
      [['active','Active'],['queued','In queue'],['completed','Completed'],['failed','Failed']].map(([key,label])=>
        '<div class="dkd-stat"><span>' + label + '</span><strong>' + counts[key] + '</strong></div>').join('') + '</div>' +
      '<div class="dkd-toolbar"><label class="dkd-search-label"><span class="dkd-visually-hidden">Search downloads</span>' +
      '<input type="search" id="dkd-search" placeholder="Search files, sources or errors…" autocomplete="off" value="' + h(instance.query) + '"></label>' +
      '<button type="button" class="dkd-soft" data-dkd="refresh">Refresh</button></div>' +
      '<div class="dkd-filters" aria-label="Filter by download status">' +
      groups.map(([key,label])=>'<button type="button" data-dkd-filter="' + key + '" aria-pressed="' + (instance.filter===key) +
        '" class="' + (instance.filter===key?'selected':'') + '">' + label + ' <span>' + (counts[key]||0) + '</span></button>').join('')+'</div>' +
      (instance.message?'<div class="' + (instance.error?'dkd-error':'dkd-notice') + '" role="' + (instance.error?'alert':'status') + '">' + h(instance.message) + '</div>':'') +
      (demo?'<div class="dkd-demo-controls"><span>Demo scenarios</span><label><span class="dkd-visually-hidden">Scenario</span><select data-dkd="scenario">' +
        ['mixed','empty','missing','many'].map(x=>'<option value="' + x + '"' + (instance.scenario===x?' selected':'') + '>' + x + '</option>').join('') +
        '</select></label><button type="button" class="dkd-soft" data-dkd="advance">Advance demo +10s</button></div>':'') +
      '<div class="dkd-list-heading"><strong>Downloads</strong><span>' + shown.length + ' of ' + matched.length + ' shown</span></div>' + content +
      '<dialog class="dkd-modal" id="dkd-new" aria-labelledby="dkd-new-title"><form id="dkd-form" novalidate>' +
      '<div class="dkd-dialog-head"><h2 id="dkd-new-title">New download</h2><button type="button" data-dkd="close" aria-label="Close dialog">×</button></div>' +
      '<p>' + (demo?'Demo only. The entered URL is never requested or saved.':'Create a direct HTTP/HTTPS download job.') + '</p>' +
      '<label>Download URL<input id="dkd-url" type="url" autofocus autocomplete="off" spellcheck="false" required placeholder="https://example.org/file.zip"></label>' +
      '<label>Destination folder<div class="dkd-dest"><input id="dkd-destination" type="text" required autocomplete="off" value="C:\\Downloads" aria-describedby="dkd-dest-note">' +
      '<button type="button" data-dkd="browse"' + (caps.browseDestination===false?' disabled title="Native folder selection is unavailable in demo mode."':'') + '>Browse…</button></div></label>' +
      '<small id="dkd-dest-note">' + (demo?'Illustrative path only — no folder is used.':'Folder must be selected or entered explicitly.') + '</small>' +
      '<label>Filename (optional)<input id="dkd-filename" maxlength="160" autocomplete="off" placeholder="Use suggested filename"></label>' +
      '<div class="dkd-filename-preview" id="dkd-preview" aria-live="polite">Suggested filename: —</div>' +
      '<div class="dkd-dialog-error" id="dkd-form-error" role="alert"></div>' +
      '<div class="dkd-dialog-actions"><button type="button" class="dkd-soft" data-dkd="close">Cancel</button>' +
      '<button type="submit" class="dkd-soft" data-dkd-submit="queue">' + (demo?'Simulate queue':'Add to queue') + '</button>' +
      '<button type="submit" class="dkd-main-button" data-dkd-submit="start">' + (demo?'Simulate start':'Start download') + '</button></div></form></dialog>' +
      '<dialog class="dkd-modal dkd-confirm" id="dkd-confirm" aria-labelledby="dkd-confirm-title"><div class="dkd-dialog-head"><h2 id="dkd-confirm-title">Confirm action</h2>' +
      '<button type="button" data-dkd="close" aria-label="Close dialog">×</button></div><p id="dkd-confirm-copy"></p>' +
      '<div class="dkd-dialog-actions"><button type="button" class="dkd-soft" data-dkd="close">Keep download</button>' +
      '<button type="button" class="dkd-danger" data-dkd="confirm-yes">Confirm</button></div></dialog>' +
      '</div>';
    if (keepFocus) {
      const search=host.querySelector('#dkd-search');
      search?.focus();
      search?.setSelectionRange(search.value.length,search.value.length);
    }
  }
  async function refresh(i) {
    if (i.dead) return;
    const request=++i.request;
    try {
      const result=await i.adapter.list();
      if (i.dead||request!==i.request) return;
      i.jobs=Array.isArray(result)?result.map(core.normalize):[];
      i.loading=false;i.loadError=null;render(i,false);
    } catch(e) {
      if (i.dead||request!==i.request) return;
      i.loading=false;i.loadError='Could not load downloads. ' + (e?.message||'Unknown error.');
      render(i,false);
    }
  }
  function closeDialogs(i) {
    i.host.querySelectorAll('dialog[open]').forEach(d=>d.close());
    i.confirm=null;
  }
  function showDialog(i,id) {
    const d=i.host.querySelector(id);
    if (!d) return;
    i.returnFocus=document.activeElement;
    d.showModal();
  }
  function showMessage(i,message,error=false) {i.message=message;i.error=error;render(i,false);}
  async function execute(i,verb,id) {
    if (i.dead||i.busy) return;
    const job=i.jobs.find(j=>j.id===id);
    if (!job) return;
    if (!core.actions(job,i.adapter.capabilities)[verb]) {
      showMessage(i,'This control is unavailable for the selected download.',true);return;
    }
    i.busy=true;
    try {
      await i.adapter[verb](id);
      i.message=verb==='openFolder'?'Opened folder.':verb==='remove'?'Removed from the list.':'Action completed: ' + verb + '.';
      i.error=false; await refresh(i);
    } catch(e) {showMessage(i,e?.message||'The action could not be completed.',true);}
    finally{i.busy=false;}
  }
  function preview(i) {
    const form=i.host.querySelector('#dkd-form');
    if (!form) return;
    const parsed=core.validateSource(form.querySelector('#dkd-url').value);
    const name=form.querySelector('#dkd-filename').value.trim();
    const result=name?core.decodeFilename(name):(parsed.ok?parsed.filename:'—');
    form.querySelector('#dkd-preview').textContent='Suggested filename: '+result;
  }
  async function submit(i,button) {
    const form=i.host.querySelector('#dkd-form');
    const error=form?.querySelector('#dkd-form-error');
    if (!form||!error||i.busy) return;
    error.textContent='';
    const source=form.querySelector('#dkd-url').value;
    const valid=core.validateSource(source);
    if (!valid.ok) {error.textContent=valid.error;form.querySelector('#dkd-url').focus();return;}
    const dest=form.querySelector('#dkd-destination').value.trim();
    if (!dest) {error.textContent='Enter a destination folder.';form.querySelector('#dkd-destination').focus();return;}
    const fn=form.querySelector('#dkd-filename').value.trim();
    i.busy=true;
    try {
      await i.adapter.enqueue({url:valid.sourceUrl,destinationDirectory:dest,
        fileName:fn?core.decodeFilename(fn):valid.filename,startNow:button==='start'});
      closeDialogs(i);
      i.message=i.adapter.capabilities.mode==='mock'?'Demo job added. No data was downloaded.':'Download submitted.';
      i.error=false;await refresh(i);
    } catch(e) {error.textContent=e?.message||'Could not add the download.';}
    finally{i.busy=false;}
  }
  function handler(i,event) {
    const target=event.target;
    const clicked=target.closest('button');
    if (clicked?.dataset.details) {
      i.expanded=i.expanded===clicked.dataset.details?null:clicked.dataset.details;
      render(i,false);return;
    }
    if (clicked?.dataset.jobAction) {
      const verb=clicked.dataset.jobAction,id=clicked.dataset.id;
      if (verb==='cancel'||verb==='remove') {
        i.confirm={verb,id};
        const d=i.host.querySelector('#dkd-confirm');
        i.host.querySelector('#dkd-confirm-title').textContent=verb==='remove'?'Remove download?':'Cancel download?';
        i.host.querySelector('#dkd-confirm-copy').textContent=verb==='remove'
          ? 'Remove this entry from the list? This action must not delete downloaded files.'
          : 'Stop this download? The native backend decides whether partial data is retained.';
        i.host.querySelector('[data-dkd="confirm-yes"]').textContent=verb==='remove'?'Remove entry':'Cancel download';
        showDialog(i,'#dkd-confirm');return;
      }
      execute(i,verb,id);return;
    }
    const action=clicked?.dataset.dkd;
    if (action==='back') {i.onBack?.();return;}
    if (action==='new') {showDialog(i,'#dkd-new');return;}
    if (action==='close') {
      closeDialogs(i);i.returnFocus?.focus?.();return;
    }
    if (action==='confirm-yes' && i.confirm) {
      const {verb,id}=i.confirm;closeDialogs(i);execute(i,verb,id);return;
    }
    if (action==='refresh') {i.loading=true;i.message='';render(i,false);refresh(i);return;}
    if (action==='clear') {i.filter='all';i.query='';i.limit=50;render(i,false);return;}
    if (action==='more') {i.limit+=50;render(i,false);return;}
    if (action==='advance') {i.adapter.advance(10);return;}
    if (action==='browse') {
      i.adapter.chooseFolder().then(path=>{
        if (path) i.host.querySelector('#dkd-destination').value=path;
      }).catch(e=>{const box=i.host.querySelector('#dkd-form-error');if(box)box.textContent=e.message;});
      return;
    }
    if (clicked?.dataset.dkdFilter) {i.filter=clicked.dataset.dkdFilter;i.limit=50;render(i,false);}
  }
  function mount(host,{adapter,onBack}={}) {
    if (!(host && typeof host.addEventListener==='function')) throw new Error('A host element is required.');
    dispose();
    adapter=adapter||mock?.createMockAdapter();
    if (!adapter) throw new Error('The demo adapter is not available.');
    const i={host,adapter,onBack,jobs:[],query:'',filter:'all',expanded:null,limit:50,loading:true,
      message:'',error:false,loadError:null,request:0,dead:false,busy:false,confirm:null,scenario:'mixed'};
    instance=i;
    i.onClick=e=>handler(i,e);
    i.onInput=e=>{
      if (e.target.id==='dkd-search') {i.query=e.target.value;i.limit=50;render(i,true);}
      else if (e.target.id==='dkd-url'||e.target.id==='dkd-filename') preview(i);
    };
    i.onChange=e=>{
      if (e.target.dataset.dkd==='scenario') {i.scenario=e.target.value;i.expanded=null;i.limit=50;i.adapter.setScenario(i.scenario);}
    };
    i.onSubmit=e=>{
      if(e.target.id!=='dkd-form')return;
      e.preventDefault();
      const submitter=e.submitter;
      submit(i,submitter?.dataset.dkdSubmit||'queue');
    };
    i.onKey=e=>{
      if(e.key==='Escape' && i.host.querySelector('dialog[open]')) e.stopPropagation();
    };
    host.addEventListener('click',i.onClick);
    host.addEventListener('input',i.onInput);
    host.addEventListener('change',i.onChange);
    host.addEventListener('submit',i.onSubmit);
    host.addEventListener('keydown',i.onKey);
    i.unsub=adapter.subscribe?.(()=>{if(!i.dead)refresh(i)});
    render(i,false);refresh(i);
    return {dispose,refresh:()=>refresh(i)};
  }
  function dispose() {
    const i=instance;
    if (!i) return;
    i.dead=true;i.request++;
    i.unsub?.();closeDialogs(i);
    i.host.removeEventListener('click',i.onClick);
    i.host.removeEventListener('input',i.onInput);
    i.host.removeEventListener('change',i.onChange);
    i.host.removeEventListener('submit',i.onSubmit);
    i.host.removeEventListener('keydown',i.onKey);
    instance=null;
  }
  root.DKDownloadsUI=Object.freeze({mount,dispose,hasOpenDialog:()=>!!instance?.host.querySelector('dialog[open]')});
})(window);
