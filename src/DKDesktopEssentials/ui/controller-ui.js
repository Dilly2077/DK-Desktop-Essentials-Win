/* Controller Hub UI – local-only WebView surface, no mock devices or telemetry. */
(() => {
  'use strict';
  const CONTROL_NAMES = ['South','East','West','North','DPadUp','DPadDown','DPadLeft','DPadRight','LeftShoulder','RightShoulder','LeftStickButton','RightStickButton','Menu','View','Paddle1','Paddle2','Paddle3','Paddle4','LeftTrigger','RightTrigger','LeftStickX','LeftStickY','RightStickX','RightStickY'];
  const TABS = [['overview','Overview'],['mappings','Mappings'],['tuning','Tuning'],['diagnostics','Live input'],['advanced','Advanced'],['output','Virtual output']];
  const ROUTES = {'controller-mapper':'mappings','gamepad-tester':'diagnostics','input-monitor':'diagnostics','hid-tools':'advanced'};
  const FUTURE = {
    'desktop-controller':['Controller → Keyboard / Mouse','Map controller buttons to mouse movement, clicks, shortcuts and applications.','Controller-to-keyboard/mouse backend is not yet implemented.'],
    'game-profiles':['Automatic Game Profiles','Switch profiles when a selected game or application launches.','Game executable detection and automatic switching are not yet implemented.'],
    'save-manager':['Game Save Manager','Discover, back up and safely restore local game saves.','Save discovery, backup and restore are not yet implemented.'],
    'game-library':['Game Library','A single local library for games from multiple launchers.','Game-library scanning and storefront imports are not yet implemented.'],
    'fps-overlay':['FPS / System Overlay','Show frame pacing and selected system information during play.','In-game overlay and hardware monitoring are not yet implemented.']
  };
  const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const $ = (root, selector) => root.querySelector(selector);
  let main, rail, onBack, notify;
  let active = false, deviceId = '', profileId = '', tab = 'overview', editing = null;
  let devices = [], profiles = [], advancedDevices = [], advancedStatus = null, outputStatus = null, inputSnapshot = null;
  let pending = new Map(), serial = 0, timer = null, pollBusy = false, notice = '', busy = false, previewType = null;
  function api(command,payload={},limit=18000) {
    if (!window.chrome?.webview?.postMessage) return Promise.reject(new Error('Controller features require the Windows desktop application.'));
    const requestId = 'dkui-'+(++serial);
    return new Promise((resolve,reject) => {
      const timeout = setTimeout(() => { pending.delete(requestId); reject(new Error('The controller operation did not respond.')); },limit);
      pending.set(requestId,{resolve,reject,timeout});
      window.chrome.webview.postMessage(JSON.stringify({type:'controllerHub:'+command,requestId,payload}));
    });
  }
  function response(event) {
    let message = event.data;
    if (typeof message === 'string') {try{message=JSON.parse(message)}catch{return;}}
    if (!message || message.type !== 'controllerHub:response' || !pending.has(message.requestId)) return;
    const job = pending.get(message.requestId); pending.delete(message.requestId); clearTimeout(job.timeout);
    message.ok ? job.resolve(message.result) : job.reject(new Error(message.error || 'Controller action failed.'));
  }
  window.chrome?.webview?.addEventListener('message',response);
  function stopPoll(){if(timer)clearInterval(timer);timer=null;pollBusy=false;}
  function close(){
    active=false;stopPoll();editing=null;main=null;rail=null;
    for(const entry of pending.values()){clearTimeout(entry.timeout);entry.reject(new Error('Controller view closed.'));}
    pending.clear();
  }
  function profile(){return profiles.find(x=>x.id===profileId)||null;}
  function selectedDevice(){return devices.find(x=>x.deviceId===deviceId)||null;}
  function alertUser(message){notice=String(message);if(notify)notify(message);}
  async function load() {
    if(!active) return;
    busy=true;paint();
    try {
      const results=await Promise.allSettled([
        api('listDevices'),api('listProfiles'),api('getOutputStatus'),api('getAdvancedFeatureStatus'),api('listAdvancedDevices')
      ]);
      const [d,p,o,a,ad]=results.map(x=>x.status==='fulfilled'?x.value:null);
      const critical=results.slice(0,2).find(x=>x.status==='rejected');
      if(critical)throw critical.reason;
      if(!active)return;
      devices=Array.isArray(d)?d:[];profiles=Array.isArray(p)?p:(Array.isArray(p?.profiles)?p.profiles:[]);
      outputStatus=o;advancedStatus=a;advancedDevices=Array.isArray(ad)?ad:[];
      if(!devices.some(x=>x.deviceId===deviceId))deviceId=(devices.find(x=>x.isDefault)||devices[0])?.deviceId||'';
      if(!profiles.some(x=>x.id===profileId))profileId=profiles.find(x=>x.deviceMatch?.deviceId===deviceId)?.id||profiles[0]?.id||'';
    }catch(err){notice=err.message;devices=[];profiles=[];}
    busy=false;paint();
  }
  function mount(element,side,options={}) {
    close();active=true;main=element;rail=side;onBack=options.back;notify=options.toast;
    tab=ROUTES[options.id]||'overview';previewType=FUTURE[options.id]?options.id:null;
    notice='';devices=[];profiles=[];deviceId='';profileId='';inputSnapshot=null;advancedStatus=null;outputStatus=null;
    paint();if(!previewType)load();
  }
  function header(){
    return '<div class="cu-eyebrow"><button type="button" class="cu-back" data-do="back">← Controllers</button><span>/</span><span>'+(previewType?'Module preview':'Controller Hub')+'</span></div>'+
      '<header class="cu-header"><div><h1>'+(previewType?escapeHtml(FUTURE[previewType][0]):'Controller Hub')+'</h1><p>'+(previewType?escapeHtml(FUTURE[previewType][1]):'Configure mappings, inspect input and manage connected controllers.')+'</p></div><div class="cu-head-right"><span class="cu-local">● Local processing</span><button class="cu-btn cu-quiet" data-do="refresh" '+(previewType?'disabled':'')+'>↻ Refresh</button></div></header>';
  }
  function selects(){
    return '<div class="cu-select-row"><label>Connected controller<select id="cu-device" aria-label="Connected controller"><option value="">'+(devices.length?'Select a controller':'No controller detected')+'</option>'+devices.map(d=>'<option value="'+escapeHtml(d.deviceId)+'" '+(d.deviceId===deviceId?'selected':'')+'>'+escapeHtml(d.displayName)+' · '+escapeHtml(d.isWireless?'Wireless':'Wired')+'</option>').join('')+'</select></label>'+
    '<label>Profile<select id="cu-profile" aria-label="Controller profile"><option value="">'+(profiles.length?'Select profile':'No profiles')+'</option>'+profiles.map(p=>'<option value="'+escapeHtml(p.id)+'" '+(p.id===profileId?'selected':'')+'>'+escapeHtml(p.name)+'</option>').join('')+'</select></label>'+
    '<button class="cu-btn cu-primary" data-do="new-profile">+ New profile</button></div>';
  }
  function summary(){
    const d=selectedDevice();const sessions=outputStatus?.sessions||[];
    const session=sessions.find(x=>x.deviceId===deviceId);
    return '<div class="cu-summary">'+
      '<div class="cu-stat"><span>Device</span><strong>'+(d?escapeHtml(d.family):'Not connected')+'</strong><small>'+(d?escapeHtml((d.vendorId||0).toString(16).padStart(4,'0')+':'+(d.productId||0).toString(16).padStart(4,'0')):'Connect a controller to begin')+'</small></div>'+
      '<div class="cu-stat"><span>Input</span><strong>'+(d&&d.supportsStandardGamepad?'Available':d?'Limited':'Waiting')+'</strong><small>'+(d?d.buttonCount+' buttons · '+d.axisCount+' axes':'No live input yet')+'</small></div>'+
      '<div class="cu-stat"><span>Virtual output</span><strong>'+(session?.running?'Running':'Stopped')+'</strong><small>'+(session?escapeHtml(session.state):'No active session')+'</small></div>'+
      '<div class="cu-stat"><span>Feature backend</span><strong>'+(advancedStatus?.installed?'Installed':'Optional')+'</strong><small>Advanced features only on supported devices</small></div></div>';
  }
  function diagram(){
    return '<svg class="cu-controller" viewBox="0 0 480 250" role="img" aria-label="Generic controller layout, illustrative controls only"><path d="M146 52 Q161 31 193 35 L288 35 Q319 31 336 52 Q372 61 389 87 L433 164 Q450 210 407 216 Q389 220 364 193 L323 157 Q308 148 290 147 L192 147 Q170 149 153 160 L117 195 Q88 228 63 214 Q29 203 44 162 L86 88 Q100 66 146 52Z" fill="#112d42" stroke="#3687be" stroke-width="3"/><path d="M184 45 Q238 29 292 45" fill="none" stroke="#2f546e" stroke-width="5"/>'+
      '<circle cx="154" cy="126" r="27" fill="#061825" stroke="#527c95" stroke-width="3"/><circle cx="154" cy="126" r="12" fill="#173e57"/><circle cx="304" cy="155" r="27" fill="#061825" stroke="#527c95" stroke-width="3"/><circle cx="304" cy="155" r="12" fill="#173e57"/>'+
      '<path d="M89 101h15V86h17v15h15v17h-15v15h-17v-15H89z" fill="#355a70"/><circle cx="359" cy="89" r="10" fill="#265777"/><circle cx="382" cy="111" r="10" fill="#265777"/><circle cx="337" cy="111" r="10" fill="#265777"/><circle cx="359" cy="134" r="10" fill="#265777"/>'+
      '<path d="M216 84h16m18 0h16" stroke="#75a7c3" stroke-width="8" stroke-linecap="round"/>'+
      '<text x="359" y="94" text-anchor="middle" fill="#c9edff" font-size="10">Y</text><text x="382" y="115" text-anchor="middle" fill="#c9edff" font-size="10">B</text><text x="337" y="115" text-anchor="middle" fill="#c9edff" font-size="10">X</text><text x="359" y="138" text-anchor="middle" fill="#c9edff" font-size="10">A</text></svg>';
  }
  function card(title,body,extra=''){return '<section class="cu-card '+extra+'"><h2>'+title+'</h2>'+body+'</section>';}
  function overview(){
    const d=selectedDevice();
    return '<div class="cu-two">'+card('Controller preview',diagram()+'<p class="cu-muted cu-center">Generic layout · Supported inputs update in Live input.</p>','cu-device-visual')+
      card('Device information',d?'<dl class="cu-facts"><dt>Name</dt><dd>'+escapeHtml(d.displayName)+'</dd><dt>Family</dt><dd>'+escapeHtml(d.family)+'</dd><dt>Connection</dt><dd>'+(d.isWireless?'Wireless':'Wired')+'</dd><dt>Controls</dt><dd>'+d.buttonCount+' buttons · '+d.axisCount+' axes</dd></dl><button class="cu-btn" data-do="rename">Rename device</button>':'<div class="cu-empty">Connect a controller by USB or Bluetooth. The device list and capabilities will appear here.</div>')+'</div>'+
      card('Quick actions','<div class="cu-actions"><button class="cu-btn" data-tab="mappings">Edit mappings →</button><button class="cu-btn" data-tab="diagnostics">View live input →</button><button class="cu-btn" data-tab="output">Virtual output →</button></div>');
  }
  function controlOptions(selected){return CONTROL_NAMES.map(c=>'<option value="'+c+'" '+(c===selected?'selected':'')+'>'+escapeHtml(c)+'</option>').join('');}
  function mappings(){
    const p=editing||profile();if(!p)return card('Mappings','<div class="cu-empty">Create or select a profile to configure your controller.</div>');
    const layer=p.layers?.find(l=>l.isBaseLayer)||p.layers?.[0]; const bindings=layer?.bindings||[];
    return card('Mapping editor','<p class="cu-muted">Change an input to a different controller control. New edits are saved as a separate profile so the original stays intact.</p>'+
      '<div class="cu-toolbar"><button class="cu-btn" data-do="add-map">+ Add mapping</button><button class="cu-btn" data-do="duplicate">Duplicate profile</button><button class="cu-btn" data-do="export">Export JSON</button><button class="cu-btn" data-do="import">Import JSON</button><input id="cu-file" type="file" accept=".json,application/json" hidden></div>'+
      '<div class="cu-map-header"><span>Input</span><span>Output</span><span>Mode</span><span></span></div>'+
      (bindings.length?bindings.map((b,i)=>b.source?.kind!=='Standard'||b.target?.kind!=='ControllerControl'?'<div class="cu-note">Advanced binding '+(i+1)+' is retained in the profile. Its editing controls are not yet available.</div>':'<div class="cu-map-row"><select data-source="'+i+'" aria-label="Input '+(i+1)+'">'+controlOptions(b.source?.standard)+'</select><select data-target="'+i+'" aria-label="Output '+(i+1)+'">'+controlOptions(b.target?.controllerControl)+'</select><select data-mode="'+i+'" aria-label="Mode '+(i+1)+'">'+['Direct','Toggle','Turbo'].map(x=>'<option '+(x===b.mode?'selected':'')+'>'+x+'</option>').join('')+'</select><button class="cu-icon-btn" data-remove-map="'+i+'" title="Remove mapping" aria-label="Remove mapping">×</button></div>').join(''):'<div class="cu-empty">No remaps set. Unmapped controls can pass through unchanged.</div>')+
      '<div class="cu-draft-actions"><div class="cu-muted">'+(editing?'Unsaved changes to a draft.':'Showing stored profile. No changes made.')+'</div><button class="cu-btn cu-primary" data-do="save-copy" '+(!editing?'disabled':'')+'>Validate & save as new</button></div>')+
      card('Profile layers','<div class="cu-mini-grid"><div><strong>Base layer</strong><p class="cu-muted">Default controls and direct mappings.</p></div><div><strong>Shift layers · '+Math.max(0,(p.layers?.length||1)-1)+'</strong><p class="cu-muted">Advanced layer configuration is supported by the backend; visual editing is reserved for the next UI pass.</p></div></div>')+
      card('Output behaviour','<label class="cu-field">Profile output mode<select id="cu-outputmode">'+['None','PassThroughOnly','VirtualXbox360','VirtualDualShock4'].map(x=>'<option value="'+x+'" '+(p.outputMode===x?'selected':'')+'>'+x+'</option>').join('')+'</select></label><p class="cu-muted">Virtual output requires an optional provider and explicit installation. It does not hide your physical controller.</p>');
  }
  function tuning(){
    const p=editing||profile();if(!p)return card('Tuning','<div class="cu-empty">Select a profile to tune the sticks and triggers.</div>');
    const axis=['LeftStickX','LeftStickY','RightStickX','RightStickY','LeftTrigger','RightTrigger'];
    const rows=axis.map(name=>{const t=p.tuning?.find(x=>x.control?.standard===name);return '<div class="cu-tune-row"><div><strong>'+name.replace(/([A-Z])/g,' $1').trim()+'</strong><small>Deadzone: <output>'+Math.round((t?.deadzone||0)*100)+'%</output></small></div><input data-tune="'+name+'" type="range" min="0" max="40" step="1" value="'+Math.round((t?.deadzone||0)*100)+'" aria-label="'+name+' deadzone" /><span>'+Math.round((t?.deadzone||0)*100)+'%</span></div>';}).join('');
    return card('Analogue deadzones','<p class="cu-muted">Adjust your profile\'s software deadzone. Changes are previewed as values, never applied system-wide.</p>'+rows+'<div class="cu-draft-actions"><span class="cu-muted">'+(editing?'Draft changes waiting to be saved':'No changes yet')+'</span><button class="cu-btn cu-primary" data-do="save-copy" '+(!editing?'disabled':'')+'>Validate & save as new</button></div>')+
      card('Response curve','<div class="cu-curve"><svg viewBox="0 0 360 138" aria-label="Illustrative linear response curve"><path d="M25 15V115H338" stroke="#5b7890" stroke-width="2" fill="none"/><path d="M25 115L325 20" stroke="#48baff" stroke-width="3" fill="none"/></svg><div><strong>Linear default</strong><p class="cu-muted">Curve exponent, anti-deadzone, inversion and advanced mapping can be configured in the profile schema. The graph is illustrative, not measured input.</p></div></div>');
  }
  function diagnostics(){
    const d=selectedDevice();const state=inputSnapshot?.standardControls||{};
    const progress=(name)=>Math.round(Math.min(100,Math.abs(Number(state[name])||0)*100));
    return card('Live controller input','<p class="cu-muted">Raw values read from the selected Windows controller. Open this tab to begin sampling; no input is recorded to disk.</p>'+
      (d?'<div class="cu-live-grid"><div>'+diagram()+'<div class="cu-live-controls">'+CONTROL_NAMES.slice(0,14).map(c=>'<span class="cu-input-pill '+((Number(state[c])||0)>0.5?'lit':'')+'" data-name="'+c+'">'+c.replace('DPad','D-')+'</span>').join('')+'</div></div><div class="cu-bars">'+['LeftStickX','LeftStickY','RightStickX','RightStickY','LeftTrigger','RightTrigger'].map(c=>'<div><span>'+c+'</span><meter min="0" max="100" value="'+progress(c)+'"></meter><b>'+((Number(state[c])||0)*100).toFixed(0)+'%</b></div>').join('')+'</div></div>':'<div class="cu-empty">No connected controller. Attach a controller to inspect live input.</div>')+
      '<div class="cu-note">Drift statistics, polling analysis, calibration and export are planned for Module 2. These are not simulated here.</div>')+
      card('Controller Diagnostics — upcoming','<div class="cu-mini-grid"><div><strong>Stick drift analysis</strong><p>Noise and resting offset graphs</p></div><div><strong>Polling and timing</strong><p>Observed report intervals, not end-to-end latency</p></div><div><strong>Calibration</strong><p>Preview, compare and rollback recommended tuning</p></div></div>');
  }
  function advanced(){
    return card('Advanced controller capabilities','<p class="cu-muted">Some PlayStation/Nintendo-specific features require the optional verified SDL3 provider. Support depends on the actual device and connection method.</p>'+
      '<div class="cu-statusline"><strong>Provider</strong><span>'+(advancedStatus?.installed?'Installed':'Not installed')+'</span></div><div class="cu-actions"><button class="cu-btn cu-primary" data-do="advanced-install" '+(advancedStatus?.installed?'disabled':'')+'>Install verified provider</button><button class="cu-btn" data-do="advanced-remove" '+(!advancedStatus?.installed?'disabled':'')+'>Remove</button></div>'+
      '<p class="cu-muted">'+escapeHtml(advancedStatus?.message||'No changes happen without your action.')+'</p>')+
      card('Supported advanced devices','<div class="cu-device-list">'+(advancedDevices.length?advancedDevices.map(x=>'<div><strong>'+escapeHtml(x.displayName||x.name||'Controller')+'</strong><small>'+escapeHtml(x.backendId||x.deviceId||'')+'</small></div>').join(''):'<div class="cu-empty">No advanced device detected. Controller support varies by model and transport.</div>')+'</div>')+
      card('Haptics, lights & triggers','<p class="cu-muted">Rumble, touchpad, gyro, light-bar and adaptive-trigger commands are available on supported hardware. These controls should only be enabled after capability detection and physical-device verification.</p><div class="cu-mini-grid"><div><strong>Gyroscope / touchpad</strong><p>Advanced mapping and readout</p></div><div><strong>Lighting</strong><p>Device and player LEDs</p></div><div><strong>Adaptive triggers</strong><p>Resistance / vibration effects</p></div></div>');
  }
  function output(){
    const b=outputStatus?.backend||{};const sessions=outputStatus?.sessions||[];
    return card('Virtual controller output','<p class="cu-muted">Optional Xbox 360 / DualShock 4 output through HIDMaestro. The physical controller stays visible: some games may detect duplicate input. Anti-cheat compatibility is not guaranteed.</p>'+
      '<div class="cu-statusline"><strong>Provider package</strong><span>'+(b.providerPackagePresent?'Installed':'Not installed')+'</span></div>'+
      '<div class="cu-statusline"><strong>Output helper</strong><span>'+(b.helperPresent?'Present':'Unavailable')+'</span></div>'+
      '<div class="cu-actions"><button class="cu-btn" data-do="output-install" '+(b.providerPackagePresent?'disabled':'')+'>1. Install provider</button><button class="cu-btn" data-do="driver-install" '+(!b.providerPackagePresent?'disabled':'')+'>2. Install driver (admin)</button></div>'+
      '<div class="cu-note">Driver installation, repair and removal invoke a Windows confirmation and may require administrator privileges.</div>')+
      card('Session controls','<label class="cu-field">Update rate<select id="cu-hz"><option>125</option><option selected>250</option><option>500</option></select></label>'+
      '<p class="cu-muted">To start, choose a connected device and a saved profile with virtual Xbox 360 or DualShock 4 output.</p>'+
      '<div class="cu-actions"><button class="cu-btn cu-primary" data-do="session-start" '+(!deviceId||!profileId?'disabled':'')+'>Start output</button><button class="cu-btn" data-do="session-stop">Stop selected output</button><button class="cu-btn" data-do="stop-all">Stop all outputs</button></div>'+
      '<div class="cu-session-list">'+(sessions.length?sessions.map(x=>'<div><strong>'+escapeHtml(x.state||'unknown')+'</strong><span>'+escapeHtml(x.deviceId||'')+'</span></div>').join(''):'<div class="cu-empty">No output sessions running.</div>')+'</div>')+
      card('Maintenance','<p class="cu-muted">HIDMaestro cleanup can affect virtual devices from other applications. Review the warning before proceeding.</p><div class="cu-actions"><button class="cu-btn cu-danger" data-do="output-repair">Repair backend</button><button class="cu-btn cu-danger" data-do="output-remove">Remove backend</button></div>');
  }
  function future(){
    const f=FUTURE[previewType];
    return card('Planned experience','<div class="cu-empty"><strong>'+escapeHtml(f[2])+'</strong><p>No example data is being presented as real system state.</p></div>')+
      card('Proposed screens','<div class="cu-mini-grid">'+
      (previewType==='desktop-controller'?'<div><strong>Input mapper</strong><p>Keyboard keys, mouse buttons and cursor curves</p></div><div><strong>Desktop profiles</strong><p>App-specific sets and hotkeys</p></div>':
       previewType==='game-profiles'?'<div><strong>Rules list</strong><p>Executable, assigned profile, priority</p></div><div><strong>Activity log</strong><p>Rule match and revert history</p></div>':
       previewType==='save-manager'?'<div><strong>Detected saves</strong><p>Locations and backup status</p></div><div><strong>Restore timeline</strong><p>Safe preview and versions</p></div>':
       previewType==='game-library'?'<div><strong>Game covers</strong><p>Search, filters, metadata</p></div><div><strong>Game details</strong><p>Launch, controller, performance, saves</p></div>':
       '<div><strong>Overlay layouts</strong><p>Position, hardware metrics and hotkeys</p></div><div><strong>Session history</strong><p>Frametime and FPS comparisons</p></div>')+'</div>');
  }
  function paintRail(){
    if(!rail)return;
    const d=selectedDevice();
    rail.innerHTML='<section class="cu-rail-card"><h3>Controller status</h3><p>'+escapeHtml(d?.displayName||'No device selected')+'</p><small>'+(d?'Available locally':'Connect a controller to enable readouts')+'</small></section>'+
      '<section class="cu-rail-card"><h3>Safety & compatibility</h3><p>Virtual output never conceals the physical controller.</p><p>Driver maintenance always requires confirmation.</p><p>Macro references need the future Macro Engine.</p></section>'+
      '<section class="cu-rail-card"><h3>Privacy</h3><p>Local profiles. No accounts or telemetry.</p><small>Optional providers require an explicit download.</small></section>';
  }
  function paint(){
    if(!active||!main)return;
    main.innerHTML='<div class="cu-page">'+header()+(previewType?'<div class="cu-tabline"><span class="cu-badge">Design preview · not implemented</span></div>'+future():
      '<div class="cu-tabline">'+TABS.map(([key,title])=>'<button type="button" data-tab="'+key+'" class="'+(tab===key?'active':'')+'">'+title+'</button>').join('')+'</div>'+
      '<div class="cu-main">'+selects()+summary()+(notice?'<div class="cu-notice" role="status">'+escapeHtml(notice)+'</div>':'')+
      (busy?'<div class="cu-loading" role="status">Loading controller information…</div>':'')+
      (tab==='overview'?overview():tab==='mappings'?mappings():tab==='tuning'?tuning():tab==='diagnostics'?diagnostics():tab==='advanced'?advanced():output())+'</div>')+'</div>';
    paintRail();wire(); syncPolling();
  }
  function syncPolling(){
    if(previewType||tab!=='diagnostics'||!deviceId){stopPoll();return;}
    if(timer)return;
    timer=setInterval(async()=>{if(!active||tab!=='diagnostics'||!deviceId||pollBusy)return;pollBusy=true;
      try{const s=await api('readState',{deviceId},2500);if(active&&tab==='diagnostics'){inputSnapshot=s;const bars=$ (main,'.cu-bars');if(bars){bars.innerHTML=['LeftStickX','LeftStickY','RightStickX','RightStickY','LeftTrigger','RightTrigger'].map(c=>{const v=Number(s.standardControls?.[c])||0;return '<div><span>'+c+'</span><meter min="0" max="100" value="'+Math.round(Math.abs(v)*100)+'"></meter><b>'+(v*100).toFixed(0)+'%</b></div>';}).join('');}main.querySelectorAll('.cu-input-pill').forEach(el=>{const name=el.dataset.name;if(name)el.classList.toggle('lit',(Number(s.standardControls?.[name])||0)>.5);});}}
      catch{ /* disconnected devices are handled on refresh; no log spam */ }finally{pollBusy=false;}
    },180);
  }
  function edit(){if(!editing){const p=profile();if(!p)return null;editing=JSON.parse(JSON.stringify(p));}return editing;}
  function wire(){
    main.querySelectorAll('[data-tab]').forEach(b=>b.addEventListener('click',()=>{tab=b.dataset.tab;notice='';inputSnapshot=null;stopPoll();paint();}));
    $(main,'#cu-device')?.addEventListener('change',e=>{deviceId=e.target.value;inputSnapshot=null;stopPoll();paint();});
    $(main,'#cu-profile')?.addEventListener('change',e=>{profileId=e.target.value;editing=null;paint();});
    main.querySelectorAll('[data-source]').forEach(b=>b.addEventListener('change',e=>{const p=edit();if(!p)return;const l=p.layers.find(x=>x.isBaseLayer)||p.layers[0];l.bindings[+e.target.dataset.source].source={kind:'Standard',standard:e.target.value,index:-1};paint();}));
    main.querySelectorAll('[data-target]').forEach(b=>b.addEventListener('change',e=>{const p=edit();if(!p)return;const l=p.layers.find(x=>x.isBaseLayer)||p.layers[0];l.bindings[+e.target.dataset.target].target={kind:'ControllerControl',controllerControl:e.target.value,macroId:null};paint();}));
    main.querySelectorAll('[data-mode]').forEach(b=>b.addEventListener('change',e=>{const p=edit();if(!p)return;const l=p.layers.find(x=>x.isBaseLayer)||p.layers[0];l.bindings[+e.target.dataset.mode].mode=e.target.value;paint();}));
    main.querySelectorAll('[data-remove-map]').forEach(b=>b.addEventListener('click',()=>{const p=edit();if(!p)return;const l=p.layers.find(x=>x.isBaseLayer)||p.layers[0];l.bindings.splice(+b.dataset.removeMap,1);paint();}));
    main.querySelectorAll('[data-tune]').forEach(b=>b.addEventListener('input',e=>{const p=edit();if(!p)return;const name=e.target.dataset.tune; p.tuning=p.tuning||[];let t=p.tuning.find(x=>x.control?.standard===name);if(!t){t={control:{kind:'Standard',standard:name,index:-1},deadzone:0,antiDeadzone:0,outerDeadzone:0,curveExponent:1,scale:1,invert:false};p.tuning.push(t);}t.deadzone=+e.target.value/100;const row=e.target.closest('.cu-tune-row');row.querySelector('output').textContent=e.target.value+'%';row.querySelector('span').textContent=e.target.value+'%';const save=main.querySelector('[data-do="save-copy"]');if(save)save.disabled=false;}));
    $(main,'#cu-outputmode')?.addEventListener('change',e=>{const p=edit();if(p){p.outputMode=e.target.value;paint();}});
    $(main,'#cu-file')?.addEventListener('change',async e=>{const file=e.target.files?.[0];if(!file)return;if(file.size>1048576){alertUser('Profile file is too large.');return;}try{const result=await api('importProfile',{json:await file.text(),preserveId:false});await load();profileId=result.id;editing=null;paint();alertUser('Profile imported to local storage.');}catch(err){alertUser(err.message);}});
    main.querySelectorAll('[data-do]').forEach(b=>b.addEventListener('click',()=>action(b.dataset.do)));
  }
  async function action(name){
    if(name==='back'){close();onBack?.();return;}
    if(name==='refresh'){await load();return;}
    if(name==='add-map'){const p=edit();if(!p)return;const l=p.layers.find(x=>x.isBaseLayer)||p.layers[0];l.bindings=l.bindings||[];l.bindings.push({id:'ui-'+Date.now(),source:{kind:'Standard',standard:'South',index:-1},target:{kind:'ControllerControl',controllerControl:'East'},mode:'Direct',pressBehavior:'Immediate',suppressOriginal:true,activationThreshold:.5,scale:1,invert:false,turboHz:10,tapMaxMilliseconds:220,holdMilliseconds:350,doublePressWindowMilliseconds:280,requiredChord:null,valueZones:[]});paint();return;}
    if(name==='import'){$(main,'#cu-file')?.click();return;}
    if(name==='save-copy'){
      if(!editing)return;try{const data=JSON.parse(JSON.stringify(editing));data.name=(data.name||'Controller Profile')+' Edited';const check=await api('validateProfile',{profile:data});if(check?.errors?.length){alertUser('Profile validation: '+check.errors.join('; '));return;}const created=await api('importProfile',{json:JSON.stringify(data),preserveId:false});editing=null;await load();profileId=created.id;paint();alertUser('Saved a new local profile. Original retained.');}catch(e){alertUser(e.message);}return;
    }
    if(name==='rename'){const d=selectedDevice();if(!d)return;const value=prompt('New display name for this controller:',d.displayName);if(value===null)return;try{await api('setDeviceName',{deviceId,name:value});await load();}catch(e){alertUser(e.message);}return;}
    if(name==='new-profile'){const value=prompt('Name for the new profile:','My Controller Profile');if(value===null)return;try{const p=await api('createProfile',{deviceId:deviceId||null,name:value});await load();profileId=p.id;tab='mappings';paint();}catch(e){alertUser(e.message);}return;}
    if(name==='duplicate'){if(!profileId)return;try{const p=await api('duplicateProfile',{profileId});await load();profileId=p.id;paint();alertUser('Profile duplicated locally.');}catch(e){alertUser(e.message);}return;}
    if(name==='export'){if(!profileId)return;try{const r=await api('exportProfile',{profileId,includeDeviceIdentifiers:false});const blob=new Blob([r.json],{type:'application/json'});const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;a.download='controller-profile.json';document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),2500);}catch(e){alertUser(e.message);}return;}
    const commands={
      'advanced-install':['installAdvancedFeatures',{},120000],'advanced-remove':['removeAdvancedFeatures',{},30000],
      'output-install':['installOutputProvider',{},120000],'driver-install':['installOutputDriver',{},120000],
      'session-start':['startOutputSession',{deviceId,profileId,updateHz:+($(main,'#cu-hz')?.value||250)},30000],
      'session-stop':['stopOutputSession',{deviceId},30000],'stop-all':['stopOutputSession',{},30000],
      'output-repair':['repairOutputBackend',{confirmGlobalCleanup:true},120000],
      'output-remove':['removeOutputBackend',{confirmGlobalCleanup:true,removeProviderPackage:false},120000]
    };
    if(!(name in commands))return;
    const risky={
      'advanced-install':'Download a verified optional SDL3 provider from its official source?',
      'output-install':'Download a verified optional HIDMaestro package from its official source?',
      'driver-install':'Install an optional driver? Windows may request administrator permission.',
      'output-repair':'Repairing can remove virtual devices created by other applications. Proceed?',
      'output-remove':'Removal can affect virtual controllers belonging to other applications. Proceed?'
    };
    if(risky[name]&&!confirm(risky[name]))return;
    try{const [command,payload,timeout]=commands[name];await api(command,payload,timeout);await load();alertUser('Controller operation completed.');}
    catch(e){alertUser(e.message);}
  }
  window.DKControllerUI={handles(id){return !!ROUTES[id]||!!FUTURE[id];},mount,close};
})();
