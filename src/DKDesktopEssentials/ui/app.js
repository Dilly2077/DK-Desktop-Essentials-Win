(() => {
  'use strict';

  const main = document.getElementById('main-pane');
  const rail = document.getElementById('right-rail');
  const app = document.getElementById('app');
  const toastRegion = document.getElementById('toast-region');

  const categoryAccent = {
    'Controllers': '#58b8ff',
    'Hardware': '#7b6cff',
    'Drivers': '#63aaff',
    'Network': '#59e982',
    'Files & Media': '#bb73ff',
    'Automation': '#4ab7ff',
    'Privacy & Security': '#54ef89',
    'Utilities': '#71d4ff'
  };

  const tools = [
    {id:'gamepad-tester',title:'Gamepad Tester',category:'Controllers',icon:'gamepad',desc:'Test and calibrate controllers',tags:['Diagnostics','Real-time']},
    {id:'controller-mapper',title:'Controller Mapper',category:'Controllers',icon:'share',desc:'Remap buttons and create controller profiles',tags:['Mapping','Profiles']},
    {id:'input-monitor',title:'Input Monitor',category:'Controllers',icon:'bars',desc:'Live input visualization and polling checks',tags:['Real-time','Diagnostics']},
    {id:'hid-tools',title:'HID Tools',category:'Controllers',icon:'chip',desc:'View and manage HID devices',tags:['Advanced','Troubleshooting']},
    {id:'desktop-controller',title:'Controller → Keyboard/Mouse',category:'Controllers',icon:'keyboard',desc:'Use a controller across desktop apps and unsupported games',tags:['Input','Profiles']},
    {id:'game-profiles',title:'Automatic Game Profiles',category:'Controllers',icon:'profile',desc:'Apply mappings and profiles when a game launches',tags:['Automatic','Gaming']},
    {id:'save-manager',title:'Game Save Manager',category:'Controllers',icon:'save',desc:'Create local versioned game-save backups',tags:['Backup','Local-first']},
    {id:'game-library',title:'Game Library',category:'Controllers',icon:'library',desc:'Catalogue installed games across launchers',tags:['Library','Local-first']},
    {id:'fps-overlay',title:'FPS / System Overlay',category:'Controllers',icon:'overlay',desc:'On-screen FPS, frametime and selected system metrics',tags:['Overlay','Real-time']},

    {id:'system-info',title:'System Information',category:'Hardware',icon:'monitor',desc:'Detailed hardware and OS overview',tags:['System','Diagnostics']},
    {id:'sensors',title:'Sensors Monitor',category:'Hardware',icon:'thermometer',desc:'CPU, GPU, temps, fans and power',tags:['Real-time','Monitoring']},
    {id:'benchmark',title:'Hardware Benchmark',category:'Hardware',icon:'gauge',desc:'Test and compare PC performance',tags:['Benchmark','Reports']},
    {id:'power-manager',title:'Power Manager',category:'Hardware',icon:'battery',desc:'Power plans and device control',tags:['Optimization','Local-first']},
    {id:'fan-control',title:'Fan Control',category:'Hardware',icon:'fan',desc:'Curves, hysteresis and sensor-based fan presets',tags:['Cooling','Profiles']},
    {id:'rgb-control',title:'RGB Control',category:'Hardware',icon:'rgb',desc:'Local control for supported RGB devices',tags:['Lighting','Local-first']},
    {id:'screensync',title:'ScreenSync RGB',category:'Hardware',icon:'screen',desc:'Make lighting react to screen colours and zones',tags:['Lighting','Real-time']},
    {id:'process-tools',title:'Startup & Process Tools',category:'Hardware',icon:'startup',desc:'Inspect startup items, processes and services',tags:['System','Diagnostics']},

    {id:'driver-manager',title:'Driver Manager',category:'Drivers',icon:'download',desc:'Find, validate, update and back up drivers',tags:['Drivers','Backup']},
    {id:'driver-confidence',title:'Driver Confidence',category:'Drivers',icon:'shield',desc:'Rank candidates by provenance, match and compatibility',tags:['Trusted sources','Safety']},
    {id:'driver-explorer',title:'Driver Explorer',category:'Drivers',icon:'list',desc:'View installed driver and package details',tags:['Information','Export']},
    {id:'storage-tools',title:'Disk & Storage Tools',category:'Drivers',icon:'disk',desc:'Large files, duplicate candidates, SMART and checksums',tags:['Storage','Diagnostics']},

    {id:'network-analyzer',title:'Network Analyzer',category:'Network',icon:'networkBars',desc:'Monitor and analyze local network traffic',tags:['Analysis','Diagnostics']},
    {id:'ping',title:'Ping & Traceroute',category:'Network',icon:'terminal',desc:'Test connectivity and trace network routes',tags:['Troubleshooting','CLI']},
    {id:'dns',title:'DNS Manager',category:'Network',icon:'database',desc:'Inspect and manage DNS settings',tags:['DNS','Privacy']},
    {id:'network-scan',title:'Network Scanner',category:'Network',icon:'radar',desc:'Discover devices on your local network',tags:['Scanning','Security']},
    {id:'wol',title:'Wake-on-LAN',category:'Network',icon:'wol',desc:'Wake trusted PCs and configured devices',tags:['LAN','Remote']},
    {id:'file-transfer',title:'LAN File Transfer',category:'Network',icon:'transfer',desc:'Direct device-to-device file transfer',tags:['Local Network','Secure']},
    {id:'clipboard',title:'LAN Clipboard Sync',category:'Network',icon:'clipboard',desc:'Sync text and files between paired local devices',tags:['Local Network','Pairing']},
    {id:'phone-remote',title:'Phone Remote / Macro Deck',category:'Network',icon:'phone',desc:'Control media, launch apps and trigger local actions',tags:['Companion','LAN']},
    {id:'phone-webcam',title:'Phone as Webcam',category:'Network',icon:'webcam',desc:'Use a paired phone camera over LAN or USB',tags:['Camera','Optional']},
    {id:'screen-stream',title:'Local Screen Streaming',category:'Network',icon:'stream',desc:'Screen and control streaming across your own network',tags:['LAN','Advanced']},

    {id:'pdf',title:'PDF Toolkit',category:'Files & Media',icon:'pdf',desc:'Merge, split, rotate, compress and convert PDFs',tags:['Edit','Convert']},
    {id:'image',title:'Image Toolkit',category:'Files & Media',icon:'image',desc:'Resize, crop, compress and convert images',tags:['Edit','Batch']},
    {id:'av-converter',title:'Audio / Video Converter',category:'Files & Media',icon:'media',desc:'Local format conversion, compression and audio extraction',tags:['Media','Local-first']},
    {id:'subtitles',title:'Subtitle Tools',category:'Files & Media',icon:'subtitle',desc:'Edit timing and convert SRT/VTT subtitles',tags:['Subtitles','Timing']},
    {id:'screenshot',title:'Screenshot / Annotation',category:'Files & Media',icon:'screenshot',desc:'Capture, crop, blur and annotate screenshots',tags:['Capture','Annotate']},
    {id:'ocr',title:'OCR (Text Extractor)',category:'Files & Media',icon:'ocr',desc:'Extract selectable text from images and scans',tags:['OCR','Local-first']},

    {id:'macro',title:'Macro Engine',category:'Automation',icon:'gear',desc:'Record and automate repetitive local actions',tags:['Automation','Scripting']},
    {id:'macro-service',title:'Macro Deck Service',category:'Automation',icon:'phoneAction',desc:'Receive trusted actions from paired local devices',tags:['LAN','Actions']},
    {id:'launcher',title:'Quick Launcher',category:'Automation',icon:'command',desc:'Launch apps, files, scripts and DK actions',tags:['Command Palette','Fast']},

    {id:'privacy-dashboard',title:'Privacy Dashboard',category:'Privacy & Security',icon:'shield',desc:'Review permissions, storage and recent external connections',tags:['Transparency','Local-first']},
    {id:'diagnostic-bundle',title:'Diagnostic Bundle',category:'Privacy & Security',icon:'report',desc:'Generate a local troubleshooting bundle only when requested',tags:['User initiated','Export']},
    {id:'permission-review',title:'Permission Review',category:'Privacy & Security',icon:'lock',desc:'Review which modules can access devices and network features',tags:['Permissions','Safety']},
    {id:'connection-log',title:'Connection Activity',category:'Privacy & Security',icon:'activity',desc:'See recent user-initiated external connections',tags:['Network','Transparency']},

    {id:'system-export',title:'System Information Export',category:'Utilities',icon:'report',desc:'Create a readable local report of hardware, drivers and OS',tags:['Export','Diagnostics']},
    {id:'checksum',title:'Checksum Tool',category:'Utilities',icon:'hash',desc:'Calculate and compare file checksums locally',tags:['Files','Integrity']},
    {id:'archive',title:'Archive Utility',category:'Utilities',icon:'archive',desc:'Create and extract common archive formats',tags:['Files','Local-first']},
    {id:'command-tools',title:'Command Utilities',category:'Utilities',icon:'command',desc:'Small local helpers for frequent system tasks',tags:['Utilities','Local-first']}
  ];

  const categories = ['Controllers','Hardware','Drivers','Network','Files & Media','Automation','Privacy & Security','Utilities'];

  const state = {
    view:'command',
    category:'All',
    query:'',
    favorites:new Set(readJson('dk:favorites',['controller-mapper','benchmark','power-manager','dns','pdf'])),
    pinned:new Set(readJson('dk:pinned',['gamepad-tester','system-info','network-analyzer','driver-manager'])),
    recent:readJson('dk:recent',['network-analyzer','gamepad-tester','driver-manager','system-info','dns','image']),
    settings:Object.assign({darkMode:true,startWithWindows:false,checkUpdates:true,toolTips:true,compact:false},readJson('dk:settings',{})),
    workspace:readJson('dk:workspace',['hardware-widget','network-widget','driver-widget','controller-widget','files-widget','quick-widget','notes-widget'])
  };

  function readJson(key,fallback){try{const raw=localStorage.getItem(key);return raw?JSON.parse(raw):fallback}catch{return fallback}}
  function saveState(){
    localStorage.setItem('dk:favorites',JSON.stringify([...state.favorites]));
    localStorage.setItem('dk:pinned',JSON.stringify([...state.pinned]));
    localStorage.setItem('dk:recent',JSON.stringify(state.recent));
    localStorage.setItem('dk:settings',JSON.stringify(state.settings));
    localStorage.setItem('dk:workspace',JSON.stringify(state.workspace));
  }

  function svg(paths,opts=''){return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" ${opts}>${paths}</svg>`}
  const icons = {
    home:()=>svg('<path d="M3 11.5 12 4l9 7.5"/><path d="M5.5 10.5V20h13v-9.5"/><path d="M9.5 20v-6h5v6"/>'),
    grid:()=>svg('<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>'),
    tiles:()=>svg('<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>'),
    gamepad:()=>svg('<path d="M7.2 8h9.6c2.2 0 3.8 1.5 4.2 3.6l.8 4.5c.4 2.3-2.3 3.7-3.8 2l-2-2.1H8l-2 2.1c-1.5 1.7-4.2.3-3.8-2l.8-4.5C3.4 9.5 5 8 7.2 8Z"/><path d="M7 11v4M5 13h4"/><circle cx="16.5" cy="12" r=".8" fill="currentColor" stroke="none"/><circle cx="18.5" cy="14" r=".8" fill="currentColor" stroke="none"/>'),
    chip:()=>svg('<rect x="7" y="7" width="10" height="10" rx="2"/><path d="M9.5 10h5v4h-5zM9 3v4m3-4v4m3-4v4M9 17v4m3-4v4m3-4v4M3 9h4m-4 3h4m-4 3h4m10-6h4m-4 3h4m-4 3h4"/>'),
    drive:()=>svg('<path d="M5 5h14l2 5v9H3v-9l2-5Z"/><path d="M4 11h16"/><circle cx="17" cy="15" r="1" fill="currentColor" stroke="none"/>'),
    globe:()=>svg('<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.8 2.5 4.2 5.5 4.2 9S14.8 18.5 12 21c-2.8-2.5-4.2-5.5-4.2-9S9.2 5.5 12 3Z"/>'),
    folder:()=>svg('<path d="M3 6.5h7l2 2H21v10.5H3z"/>'),
    play:()=>svg('<circle cx="12" cy="12" r="9"/><path d="m10 8 6 4-6 4z"/>'),
    shield:()=>svg('<path d="M12 3 20 6v5c0 5.2-3.1 8.3-8 10-4.9-1.7-8-4.8-8-10V6l8-3Z"/><path d="m8.5 12 2.2 2.2 4.8-5"/>'),
    wrench:()=>svg('<path d="M14.5 6.5a5 5 0 0 0-6.2 6.2L3 18l3 3 5.3-5.3a5 5 0 0 0 6.2-6.2l-3 3-3-3 3-3Z"/>'),
    star:()=>svg('<path d="m12 3 2.7 5.6 6.3.9-4.6 4.4 1.1 6.1-5.5-2.9L6.5 20l1.1-6.1L3 9.5l6.3-.9L12 3Z"/>'),
    starFill:()=>'<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="m12 2.7 2.9 5.8 6.4.9-4.7 4.5 1.1 6.3-5.7-3-5.7 3 1.1-6.3-4.7-4.5 6.4-.9L12 2.7Z"/></svg>',
    clock:()=>svg('<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>'),
    settings:()=>svg('<circle cx="12" cy="12" r="3"/><path d="M19 13.5v-3l-2-.6a7 7 0 0 0-.6-1.5l1-1.8-2.1-2.1-1.8 1A7 7 0 0 0 12 5l-.6-2h-3l-.6 2a7 7 0 0 0-1.5.6l-1.8-1-2.1 2.1 1 1.8A7 7 0 0 0 3 10l-2 .6v3l2 .6a7 7 0 0 0 .6 1.5l-1 1.8 2.1 2.1 1.8-1a7 7 0 0 0 1.5.6l.6 2h3l.6-2a7 7 0 0 0 1.5-.6l1.8 1 2.1-2.1-1-1.8a7 7 0 0 0 .6-1.5l2-.6Z" transform="translate(1.5 -1.5) scale(.875)"/>'),
    info:()=>svg('<circle cx="12" cy="12" r="9"/><path d="M12 10v6"/><circle cx="12" cy="7" r=".8" fill="currentColor" stroke="none"/>'),
    windows:()=>svg('<path fill="currentColor" stroke="none" d="M3 4.5 10.8 3v8H3V4.5Zm9  -1.8L21 1v10h-9V2.7ZM3 12.8h7.8v8L3 19.5v-6.7Zm9 0h9v10l-9-1.7v-8.3Z"/>'),
    search:()=>svg('<circle cx="10.5" cy="10.5" r="6.5"/><path d="m15.5 15.5 5 5"/>'),
    share:()=>svg('<circle cx="6" cy="12" r="2.2"/><circle cx="18" cy="6" r="2.2"/><circle cx="18" cy="18" r="2.2"/><path d="m8 11 8-4m-8 6 8 4"/>'),
    bars:()=>svg('<path d="M5 20V10m7 10V4m7 16v-7"/>'),
    keyboard:()=>svg('<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M6 10h1m2 0h1m2 0h1m2 0h1m2 0h1M6 13h1m2 0h1m2 0h1m2 0h1m2 0h1M8 16h8"/>'),
    profile:()=>svg('<path d="M4 5h16v14H4z"/><path d="M8 9h8m-8 4h5m3 0 2 2 2-2"/>'),
    save:()=>svg('<path d="M5 3h12l2 2v16H5z"/><path d="M8 3v6h8V4M8 14h8v7H8z"/>'),
    library:()=>svg('<path d="M4 5h5v15H4zM10 5h5v15h-5zM16 5h4v15h-4z"/><path d="M5 8h3m3 0h3m3 0h2"/>'),
    overlay:()=>svg('<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M7 14l3-4 3 2 4-5"/><path d="M8 21h8"/>'),
    monitor:()=>svg('<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8m-4-4v4"/>'),
    thermometer:()=>svg('<path d="M10 5a2 2 0 0 1 4 0v8.2a4 4 0 1 1-4 0V5Z"/><path d="M12 8v7"/>'),
    gauge:()=>svg('<path d="M4 18a8 8 0 1 1 16 0"/><path d="m12 14 4-5"/><circle cx="12" cy="14" r="1.2"/>'),
    battery:()=>svg('<rect x="3" y="7" width="17" height="10" rx="2"/><path d="M20 10h2v4h-2M6 10h9v4H6z"/>'),
    fan:()=>svg('<circle cx="12" cy="12" r="2"/><path d="M11 10c-3-4-1-7 1-7 2.5 0 3.1 3.6 1.6 7M14 12c4-2 6 .6 5 2.5-1.3 2.2-4.7.8-6.2-1M11 14c-1 4.5-4.3 4.3-5.5 2.4-1.3-2.2 1.4-4.5 4.3-4.4"/>'),
    rgb:()=>'<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="1.7"/><path d="M12 3a9 9 0 0 1 7.8 4.5L12 12Z" fill="#ff5d73"/><path d="M19.8 7.5A9 9 0 0 1 12 21v-9Z" fill="#52e985"/><path d="M12 21A9 9 0 0 1 12 3v9Z" fill="#4bb8ff"/></svg>',
    screen:()=>svg('<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M7 14c2-4 3 2 5-2s3 2 5-3M8 21h8"/>'),
    startup:()=>svg('<circle cx="12" cy="12" r="9"/><path d="M12 7v5l4 2M12 3v3M3 12h3"/>'),
    download:()=>svg('<path d="M12 3v12m-5-5 5 5 5-5"/><path d="M4 18v3h16v-3"/>'),
    list:()=>svg('<path d="M9 6h12M9 12h12M9 18h12"/><circle cx="4.5" cy="6" r="1"/><circle cx="4.5" cy="12" r="1"/><circle cx="4.5" cy="18" r="1"/>'),
    disk:()=>svg('<ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>'),
    networkBars:()=>svg('<path d="M5 20v-5m5 5V11m5 9V7m5 13V3"/>'),
    terminal:()=>svg('<path d="m4 7 5 5-5 5M11 17h8"/>'),
    database:()=>svg('<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>'),
    radar:()=>svg('<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="4"/><path d="M12 12 19 8M12 3v2M3 12h2M19 12h2"/>'),
    wol:()=>svg('<path d="M12 2v9"/><path d="M7 5.8a8 8 0 1 0 10 0"/>'),
    transfer:()=>svg('<path d="M4 7h14m0 0-4-4m4 4-4 4M20 17H6m0 0 4-4m-4 4 4 4"/>'),
    clipboard:()=>svg('<path d="M8 5h8l1 3h3v13H4V8h3l1-3Z"/><path d="M9 5V3h6v2M8 12h8m-8 4h6"/>'),
    phone:()=>svg('<rect x="7" y="2" width="10" height="20" rx="2"/><path d="M10 5h4m-3 14h2"/>'),
    webcam:()=>svg('<circle cx="12" cy="11" r="7"/><circle cx="12" cy="11" r="2.5"/><path d="M8 20h8m-4-2v2"/>'),
    stream:()=>svg('<rect x="3" y="5" width="18" height="12" rx="2"/><path d="m10 9 5 2-5 2V9Zm-2 12h8"/>'),
    pdf:()=>svg('<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v5h5M8 13h8M8 16h6"/>'),
    image:()=>svg('<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m5 18 5-5 3 3 2-2 4 4"/>'),
    media:()=>svg('<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m10 9 5 3-5 3V9Z"/>'),
    subtitle:()=>svg('<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M6 14h5m2 0h5M7 17h4m2 0h4"/>'),
    screenshot:()=>svg('<path d="M8 4H4v4m12-4h4v4M8 20H4v-4m12 4h4v-4"/><rect x="7" y="8" width="10" height="8" rx="1"/>'),
    ocr:()=>svg('<path d="M8 4H4v4m12-4h4v4M8 20H4v-4m12 4h4v-4"/><path d="M8 14V9h2.5a2.5 2.5 0 0 1 0 5H8Zm6-5v5m0-5h4"/>'),
    gear:()=>svg('<circle cx="12" cy="12" r="3"/><path d="M12 3v3m0 12v3M3 12h3m12 0h3M5.6 5.6l2.1 2.1m8.6 8.6 2.1 2.1m0-12.8-2.1 2.1m-8.6 8.6-2.1 2.1"/>'),
    phoneAction:()=>svg('<rect x="7" y="2" width="10" height="20" rx="2"/><path d="m10 13 2-5 2 4h-2l2 4"/>'),
    command:()=>svg('<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m7 10 3 2-3 2m5 1h5"/>'),
    report:()=>svg('<path d="M6 3h9l3 3v15H6z"/><path d="M9 10h6m-6 4h6m-6 4h4"/>'),
    lock:()=>svg('<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>'),
    activity:()=>svg('<path d="M3 12h4l2-5 4 10 2-5h6"/>'),
    hash:()=>svg('<path d="M9 3 7 21m10-18-2 18M4 9h16M3 15h16"/>'),
    archive:()=>svg('<path d="M5 4h14v4H5zM6 8h12v13H6z"/><path d="M10 12h4"/>'),
    plus:()=>svg('<path d="M12 5v14M5 12h14"/>'),
    pin:()=>svg('<path d="m9 3 6 6-2 2 3 3-2 2-3-3-2 2-6-6 2-2 3 3 2-2-3-3 2-2Z"/><path d="m8 16-5 5"/>'),
    moon:()=>svg('<path d="M20 15.5A8.5 8.5 0 0 1 8.5 4 8.5 8.5 0 1 0 20 15.5Z"/>'),
    refresh:()=>svg('<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6.1 8A7 7 0 0 1 18 6l2 6m-2.1 4A7 7 0 0 1 6 18l-2-6"/>'),
    layout:()=>svg('<rect x="3" y="4" width="8" height="16" rx="1"/><rect x="13" y="4" width="8" height="7" rx="1"/><rect x="13" y="13" width="8" height="7" rx="1"/>'),
    chevron:()=>svg('<path d="m9 6 6 6-6 6"/>')
  };

  function icon(name){return (icons[name]||icons.command)()}
  function hydrateIcons(root=document){root.querySelectorAll('[data-icon]').forEach(el=>{el.innerHTML=icon(el.dataset.icon)})}
  function toolById(id){return tools.find(t=>t.id===id)}
  function unique(arr){return [...new Set(arr)]}
  function esc(text=''){return String(text).replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}

  function setActiveNav(match){
    document.querySelectorAll('.nav-item').forEach(b=>b.classList.remove('active'));
    if(match?.view){document.querySelector(`.nav-item[data-view="${match.view}"]`)?.classList.add('active')}
    if(match?.category){[...document.querySelectorAll('.nav-item[data-category]')].find(b=>b.dataset.category===match.category)?.classList.add('active')}
  }

  function hero(title,subtitle,eyebrow='WELCOME TO',quote='More control.\nA more private Windows.'){
    const q=quote.split('\n');
    return `<section class="hero"><div class="hero-copy"><div class="eyebrow">${esc(eyebrow)}</div><h1>${esc(title)}</h1><p>${esc(subtitle)}</p></div><div class="hero-quote">${esc(q[0]||'')}<span>${esc(q[1]||'')}</span></div></section>`;
  }

  function searchAndFilters(active='All'){
    const chips=['All',...categories].map(c=>`<button class="chip ${c===active?'active':''}" data-filter="${esc(c)}">${esc(c.replace('Files & Media','Files').replace('Privacy & Security','Privacy'))}</button>`).join('');
    return `<div class="search-row"><div class="search-wrap"><span class="search-icon">${icon('search')}</span><input id="global-search" autocomplete="off" spellcheck="false" placeholder="Search tools, features, or type a command..." value="${esc(state.query)}"><span class="search-key">Ctrl + K</span></div></div><div class="toolbar-row"><div class="chip-row">${chips}</div><div class="view-actions"><button class="mini-button icon-button" title="Grid view">${icon('grid')}</button><button class="mini-button" data-view="workspace">${icon('layout')}<span>Customize Layout</span></button></div></div>`;
  }

  function toolCard(t){
    const fav=state.favorites.has(t.id), pin=state.pinned.has(t.id), accent=categoryAccent[t.category]||'#4fb8ff';
    return `<article class="tool-card" data-tool="${t.id}" style="--accent:${accent}"><div class="tool-icon">${icon(t.icon)}</div><div class="tool-actions"><button class="card-action ${pin?'active':''}" data-pin="${t.id}" title="${pin?'Unpin':'Pin'}">${icon('pin')}</button><button class="card-action ${fav?'active':''}" data-fav="${t.id}" title="${fav?'Remove from favorites':'Add to favorites'}">${fav?icons.starFill():icon('star')}</button></div><h3>${esc(t.title)}</h3><p>${esc(t.desc)}</p><div class="tag-row">${t.tags.map(tag=>`<span class="tag">${esc(tag)}</span>`).join('')}</div></article>`;
  }

  function groupMarkup(category,list,limit=4){
    const items=list.slice(0,limit), accent=categoryAccent[category]||'#4fb8ff';
    const groupIcon={Controllers:'gamepad',Hardware:'chip',Drivers:'drive',Network:'globe','Files & Media':'folder',Automation:'play','Privacy & Security':'shield',Utilities:'wrench'}[category]||'tiles';
    return `<section class="group" data-group="${esc(category)}"><div class="group-header"><div class="group-title"><span class="group-icon" style="color:${accent}">${icon(groupIcon)}</span><h2>${esc(category)}</h2><small>${list.length} tools</small></div><button class="view-all" data-category="${esc(category)}">View all →</button></div><div class="tool-grid">${items.map(toolCard).join('')}</div></section>`;
  }

  function filteredTools(){
    let list=tools;
    if(state.category!=='All') list=list.filter(t=>t.category===state.category);
    if(state.query.trim()){
      const q=state.query.trim().toLowerCase();
      list=list.filter(t=>[t.title,t.category,t.desc,...t.tags].join(' ').toLowerCase().includes(q));
    }
    return list;
  }

  function renderCommand(){
    state.view='command'; state.category='All'; setActiveNav({view:'command'});
    const qlist=filteredTools();
    main.innerHTML=`${hero('Command Center','Find the right tool. Get things done locally on your device.')}${searchAndFilters('All')}<div id="groups">${categories.slice(0,6).map(c=>groupMarkup(c,qlist.filter(t=>t.category===c),4)).join('')}</div>`;
    renderRail(); bindMain();
  }

  function renderLibrary(category='All',title='Tool Library'){
    state.view='all'; state.category=category; setActiveNav(category==='All'?{view:'all'}:{category});
    const list=filteredTools();
    main.innerHTML=`${hero(title,category==='All'?`Explore ${tools.length} local-first tools and module interfaces.`:`Explore every ${category} tool in DK Desktop Essentials.`,'ALL TOOLS','All the tools.\nAll in one place.')}${searchAndFilters(category)}<div id="library-content" class="library-grid">${category==='All'?categories.map(c=>groupMarkup(c,list.filter(t=>t.category===c),99)).join(''):groupMarkup(category,list,99)}</div>`;
    renderRail('library'); bindMain();
  }

  function renderFavorites(){
    state.view='favorites'; state.category='All'; setActiveNav({view:'favorites'});
    const list=tools.filter(t=>state.favorites.has(t.id));
    main.innerHTML=`${hero('Favorites','Your most-used tools, one click away.','FAVORITES','The tools you choose.\nNothing else.')}${searchAndFilters('All')}<section class="group"><div class="group-header"><div class="group-title"><span class="group-icon">${icon('star')}</span><h2>Favorite tools</h2><small>${list.length} tools</small></div></div><div class="tool-grid">${list.map(toolCard).join('')||'<div class="empty-state">Star a tool to keep it here.</div>'}</div></section>`;
    renderRail(); bindMain();
  }

  function renderRecent(){
    state.view='recent'; setActiveNav({view:'recent'});
    const list=state.recent.map(toolById).filter(Boolean);
    main.innerHTML=`${hero('Recent','Jump back into tools you opened recently.','RECENT TOOLS','Pick up where you left off.\nAll local.')}${searchAndFilters('All')}<section class="group"><div class="group-header"><div class="group-title"><span class="group-icon">${icon('clock')}</span><h2>Recently opened</h2><small>${list.length} tools</small></div><button id="clear-recent" class="view-all">Clear history</button></div><div class="tool-grid">${list.map(toolCard).join('')||'<div class="empty-state">No recent tools yet.</div>'}</div></section>`;
    renderRail(); bindMain();
    document.getElementById('clear-recent')?.addEventListener('click',()=>{state.recent=[];saveState();renderRecent();toast('Recent history cleared')});
  }

  const widgets = {
    'hardware-widget':{title:'Hardware Stats',icon:'chip',span:'span-6',body:()=>`<div class="metric-row">${[['32','CPU','3.6 GHz'],['68','GPU','RTX 4070'],['54','RAM','8.6 / 16 GB'],['37','Disk','356 / 953 GB']].map(m=>`<div class="metric"><div class="ring" style="--v:${m[0]}"><b>${m[0]}%</b></div><strong>${m[1]}</strong><small>${m[2]}</small></div>`).join('')}</div><div class="spark"><svg viewBox="0 0 300 40" preserveAspectRatio="none"><path d="M0 30 C25 10,40 34,65 16 S105 32,135 14 S180 34,210 15 S250 29,300 17" fill="none" stroke="#40afff" stroke-width="2"/></svg></div>`},
    'network-widget':{title:'Network Devices',icon:'share',span:'span-4',body:()=>deviceList()},
    'driver-widget':{title:'Driver Status',icon:'database',span:'span-4',body:()=>`<div class="driver-donut"><div class="donut-wrap"><div class="donut"></div><div class="donut-label"><div><b>12</b><br><small>Drivers</small></div></div></div><div class="simple-list"><div class="simple-line"><span class="online"><b></b></span><span>10 Up to date</span></div><div class="simple-line"><span style="color:#f1d43a">●</span><span>2 Updates available</span></div><div class="simple-line"><span style="color:#6d8497">●</span><span>0 Issues</span></div></div></div>`},
    'controller-widget':{title:'Controller Tools',icon:'gamepad',span:'span-4',body:()=>`<div class="controller-art">${icon('gamepad')}</div><div style="text-align:center;font-size:11px"><strong>Controller Connected</strong><br><span class="online" style="justify-content:center;margin-top:5px"><b></b> Xbox Wireless Controller</span></div>`},
    'files-widget':{title:'File Tools',icon:'folder',span:'span-3',body:()=>`<div class="quick-grid">${[['pdf','PDF Toolkit'],['image','Image Toolkit'],['ocr','OCR'],['transfer','File Transfer']].map(x=>`<div class="quick-tile"><span class="ri-icon">${icon(x[0])}</span>${x[1]}</div>`).join('')}</div>`},
    'quick-widget':{title:'Quick Actions',icon:'activity',span:'span-3',body:()=>`<div class="quick-grid">${[['download','Clean Temp Files'],['gauge','Optimize PC'],['refresh','Scan Updates'],['globe','Check Network']].map(x=>`<div class="quick-tile"><span class="ri-icon">${icon(x[0])}</span>${x[1]}</div>`).join('')}</div>`},
    'notes-widget':{title:'Notes / Shortcuts',icon:'report',span:'span-3',body:()=>'<div class="notes">– Finish driver updates<br>– Test new controller profile<br>– Back up important files<br>– Optimize system this week</div>'}
  };

  function deviceList(){return `<div class="device-list">${[['monitor','DESKTOP-PC','This device'],['screen','Living Room TV','Online'],['gamepad','PlayStation 5','Online'],['report','Office Printer','Online']].map(d=>`<div class="device-line"><span class="ri-icon">${icon(d[0])}</span><span>${d[1]}</span><span class="online"><b></b>${d[2]}</span></div>`).join('')}</div>`}

  function widgetCard(id){const w=widgets[id];if(!w)return'';return `<section class="widget ${w.span}" draggable="true" data-widget="${id}"><h3><span class="ri-icon">${icon(w.icon)}</span>${w.title}</h3><span class="widget-menu">⋮</span>${w.body()}</section>`}

  function renderWorkspace(){
    state.view='workspace'; setActiveNav({view:'workspace'});
    main.innerHTML=`${hero('Workspace','Create your perfect layout. Drag, drop, resize and customize your tools.','WORKSPACE','Your workspace.\nYour rules.')}<div class="workspace-toolbar"><div class="workspace-actions"><button id="save-layout" class="primary-button">${icon('save')} Save Layout</button><button id="reset-layout" class="ghost-button">${icon('refresh')} Reset Layout</button></div><button class="ghost-button" data-view="all">${icon('tiles')} Add widgets</button></div><div class="workspace-canvas" id="workspace-canvas">${state.workspace.map(widgetCard).join('')}<div class="widget-drop"><div><span>＋</span><br>Drop widgets here</div></div></div>`;
    renderRail('workspace'); bindMain(); bindWorkspace();
  }

  function bindWorkspace(){
    const canvas=document.getElementById('workspace-canvas'); if(!canvas)return;
    let dragged=null;
    canvas.querySelectorAll('.widget').forEach(w=>{
      w.addEventListener('dragstart',e=>{dragged=w.dataset.widget;w.classList.add('dragging');e.dataTransfer.effectAllowed='move'});
      w.addEventListener('dragend',()=>w.classList.remove('dragging'));
      w.addEventListener('dragover',e=>e.preventDefault());
      w.addEventListener('drop',e=>{e.preventDefault();const target=w.dataset.widget;if(!dragged||dragged===target)return;const arr=[...state.workspace],from=arr.indexOf(dragged),to=arr.indexOf(target);arr.splice(from,1);arr.splice(to,0,dragged);state.workspace=arr;saveState();renderWorkspace();});
    });
    document.getElementById('save-layout')?.addEventListener('click',()=>{saveState();toast('Workspace layout saved locally')});
    document.getElementById('reset-layout')?.addEventListener('click',()=>{state.workspace=['hardware-widget','network-widget','driver-widget','controller-widget','files-widget','quick-widget','notes-widget'];saveState();renderWorkspace();toast('Workspace restored')});
  }

  function renderToolDetail(id){
    const t=toolById(id);if(!t)return;
    state.view='detail'; state.recent=[id,...state.recent.filter(x=>x!==id)].slice(0,12); saveState(); setActiveNav({category:t.category});
    const accent=categoryAccent[t.category]||'#4fb8ff';
    main.innerHTML=`<div class="tool-detail"><button class="view-all" data-category="${esc(t.category)}">← Back to ${esc(t.category)}</button><div class="detail-hero" style="--accent:${accent}"><div class="detail-icon">${icon(t.icon)}</div><div><div class="eyebrow">${esc(t.category)}</div><h1>${esc(t.title)}</h1><p>${esc(t.desc)}</p></div></div><div class="privacy-banner"><span class="ri-icon">${icon('shield')}</span><div><strong>UI scaffold only — no system changes are performed yet.</strong><br><small>This module currently contains the finished application surface and interaction placeholders. The underlying Windows feature will be implemented in its own build chunk.</small></div></div><div class="detail-tabs"><button class="detail-tab active">Overview</button><button class="detail-tab">Settings</button><button class="detail-tab">History</button><button class="detail-tab">About this tool</button></div><section class="placeholder-panel"><h2>${esc(t.title)}</h2><p style="color:#94aabc">The interface foundation is ready for the real module implementation.</p><div class="placeholder-grid"><div class="placeholder-box"><h3>Primary controls</h3><p>Controls and actions for ${esc(t.title)} will be connected here when this module is implemented.</p></div><div class="placeholder-box"><h3>Status & diagnostics</h3><p>Live state, validation and system feedback will appear here without background telemetry.</p></div><div class="placeholder-box"><h3>Local settings</h3><p>Module settings will stay on this PC and remain independent of any DK account.</p></div></div></section></div>`;
    renderRail(); bindMain();
  }

  function renderSettings(){
    state.view='settings'; setActiveNav({view:'settings'});
    const rows=[['darkMode','moon','Dark mode','Use the chosen dark DK Desktop palette.'],['startWithWindows','windows','Start with Windows','Launch the suite after you sign in.'],['checkUpdates','refresh','Check for updates','User-controlled update checks only.'],['toolTips','info','Show tool tips','Display explanatory labels and hints.'],['compact','grid','Compact view','Reduce card spacing and information density.']];
    main.innerHTML=`${hero('Settings','Control the application shell, privacy defaults and local preferences.','SETTINGS','Your settings.\nStored locally.')}<div class="settings-page"><section class="settings-section"><h2>Appearance & behaviour</h2>${rows.map(r=>`<div class="form-row"><div class="form-copy"><strong>${r[2]}</strong><small>${r[3]}</small></div><button class="toggle ${state.settings[r[0]]?'on':''}" data-setting="${r[0]}" aria-pressed="${state.settings[r[0]]}"></button></div>`).join('')}</section><section class="settings-section"><h2>Privacy & data</h2><div class="form-row"><div class="form-copy"><strong>Account</strong><small>Not required. No account system is configured.</small></div><span class="tag">None</span></div><div class="form-row"><div class="form-copy"><strong>Telemetry</strong><small>The application does not include analytics or tracking code.</small></div><span class="tag">Off</span></div><div class="form-row"><div class="form-copy"><strong>UI preference storage</strong><small>Stored locally in the app's WebView2 profile.</small></div><span class="tag">Local</span></div><div class="form-row"><div class="form-copy"><strong>Network access</strong><small>The current UI build blocks navigation outside its local app host.</small></div><span class="tag">Blocked</span></div></section></div>`;
    renderRail(); bindMain();
  }

  function renderAbout(){
    state.view='about'; setActiveNav({view:'about'});
    main.innerHTML=`${hero('About','The foundation for a free, local-first Windows power-user suite.','ABOUT DK DESKTOP','Small tools.\nMore control.')}<div class="about-card"><div class="about-logo">DK</div><h1>DK Desktop Essentials</h1><p>This is the UI foundation for the modular Windows suite. The shell is functional: navigation, search, favourites, pinning, recent tools, local preference storage, a draggable workspace and module pages are already connected. System-level tool implementations will be added one module at a time.</p><div class="about-pills"><span class="about-pill">No account</span><span class="about-pill">No telemetry</span><span class="about-pill">No subscriptions</span><span class="about-pill">Local-first</span><span class="about-pill">Windows 10/11</span></div></div>`;
    renderRail(); bindMain();
  }

  function renderRail(mode='default'){
    if(mode==='workspace'){
      rail.innerHTML=`${pinnedCard()}<section class="rail-card"><div class="rail-card-header"><span class="rail-icon">${icon('layout')}</span><strong>Widget Library</strong><span class="count-badge">${Object.keys(widgets).length}</span></div><div class="rail-list">${Object.entries(widgets).map(([id,w])=>`<div class="rail-item"><span class="ri-icon">${icon(w.icon)}</span><span>${w.title}</span><span class="dots">⋮</span></div>`).join('')}</div></section>${quoteCard()}`;
    } else if(mode==='library'){
      rail.innerHTML=`${recentCard()}${favoritesCard()}<section class="rail-card"><div class="rail-card-header"><span class="rail-icon">${icon('tiles')}</span><strong>Filter by Category</strong></div><div class="rail-list">${['All',...categories].map(c=>`<div class="rail-item" data-category="${esc(c)}"><span class="ri-icon">${icon(c==='All'?'tiles':({Controllers:'gamepad',Hardware:'chip',Drivers:'drive',Network:'globe','Files & Media':'folder',Automation:'play','Privacy & Security':'shield',Utilities:'wrench'}[c]))}</span><span>${esc(c==='All'?'All Tools':c)}</span><span class="count-badge">${c==='All'?tools.length:tools.filter(t=>t.category===c).length}</span></div>`).join('')}</div></section>${quoteCard()}`;
    } else {
      rail.innerHTML=`${pinnedCard()}${favoritesCard()}${quickSettingsCard()}${quoteCard()}`;
    }
    hydrateIcons(rail); bindRail();
  }

  function pinnedCard(){const list=[...state.pinned].map(toolById).filter(Boolean).slice(0,6);return `<section class="rail-card"><div class="rail-card-header"><span class="rail-icon">${icon('pin')}</span><strong>Pinned Tools</strong><span class="count-badge">${list.length}</span></div><div class="rail-list">${list.map(railTool).join('')}</div><button class="rail-add" data-view="all">＋ Add Tools</button></section>`}
  function favoritesCard(){const list=[...state.favorites].map(toolById).filter(Boolean).slice(0,6);return `<section class="rail-card"><div class="rail-card-header"><span class="rail-icon" style="color:#ffd85a">${icons.starFill()}</span><strong>Favorites</strong><span class="count-badge">${state.favorites.size}</span></div><div class="rail-list">${list.map(railTool).join('')}</div></section>`}
  function recentCard(){const list=state.recent.map(toolById).filter(Boolean).slice(0,6);return `<section class="rail-card"><div class="rail-card-header"><span class="rail-icon">${icon('clock')}</span><strong>Recent Tools</strong><span class="count-badge">${list.length}</span></div><div class="rail-list">${list.map(railTool).join('')}</div></section>`}
  function railTool(t){return `<div class="rail-item" data-tool="${t.id}" style="--accent:${categoryAccent[t.category]}"><span class="ri-icon">${icon(t.icon)}</span><span>${esc(t.title)}</span><span class="dots">⋮</span></div>`}
  function quickSettingsCard(){const rows=[['darkMode','moon','Dark Mode'],['startWithWindows','windows','Start with Windows'],['checkUpdates','refresh','Check for Updates'],['toolTips','info','Show Tool Tips'],['compact','grid','Compact View']];return `<section class="rail-card"><div class="rail-card-header"><span class="rail-icon">${icon('settings')}</span><strong>Quick Settings</strong></div><div class="settings-list">${rows.map(r=>`<div class="setting-row"><span class="ri-icon">${icon(r[1])}</span><label>${r[2]}</label><button class="toggle ${state.settings[r[0]]?'on':''}" data-setting="${r[0]}"></button></div>`).join('')}</div></section>`}
  function quoteCard(){return `<section class="rail-card quote-card"><div class="quote-mark">“</div><p>A more capable PC<br>is a more independent you.</p><small>— DK Desktop Essentials</small><div class="quote-footer">LOCAL • PRIVATE • YOUR CONTROL</div></section>`}

  function bindMain(){
    hydrateIcons(main);
    document.getElementById('global-search')?.addEventListener('input',e=>{state.query=e.target.value;refreshCurrentList()});
    document.querySelectorAll('[data-filter]').forEach(b=>b.addEventListener('click',()=>{state.category=b.dataset.filter; state.query=''; renderLibrary(state.category,state.category==='All'?'Tool Library':state.category)}));
    document.querySelectorAll('[data-category]').forEach(b=>b.addEventListener('click',e=>{if(e.target.closest('.sidebar'))return; state.query='';renderLibrary(b.dataset.category,b.dataset.category==='All'?'Tool Library':b.dataset.category)}));
    document.querySelectorAll('[data-view]').forEach(b=>b.addEventListener('click',e=>{if(e.target.closest('.sidebar'))return; routeView(b.dataset.view)}));
    document.querySelectorAll('.tool-card[data-tool]').forEach(c=>c.addEventListener('click',e=>{if(e.target.closest('[data-fav],[data-pin]'))return;renderToolDetail(c.dataset.tool)}));
    document.querySelectorAll('[data-fav]').forEach(b=>b.addEventListener('click',e=>{e.stopPropagation();toggleFavorite(b.dataset.fav)}));
    document.querySelectorAll('[data-pin]').forEach(b=>b.addEventListener('click',e=>{e.stopPropagation();togglePin(b.dataset.pin)}));
    document.querySelectorAll('[data-setting]').forEach(b=>b.addEventListener('click',()=>toggleSetting(b.dataset.setting)));
    const search=document.getElementById('global-search'); if(search&&state.query) requestAnimationFrame(()=>search.setSelectionRange(search.value.length,search.value.length));
  }

  function refreshCurrentList(){
    const input=document.getElementById('global-search');if(input)state.query=input.value;
    if(state.view==='command'){
      const container=document.getElementById('groups');const list=filteredTools();if(container)container.innerHTML=categories.slice(0,6).map(c=>groupMarkup(c,list.filter(t=>t.category===c),4)).join('');
      bindMain();
    }else if(state.view==='all'){
      renderLibrary(state.category,state.category==='All'?'Tool Library':state.category);
    }
  }

  function toggleFavorite(id){state.favorites.has(id)?state.favorites.delete(id):state.favorites.add(id);saveState();toast(state.favorites.has(id)?'Added to favorites':'Removed from favorites');rerenderCurrent()}
  function togglePin(id){state.pinned.has(id)?state.pinned.delete(id):state.pinned.add(id);saveState();toast(state.pinned.has(id)?'Pinned to Command Center':'Unpinned');rerenderCurrent()}
  function toggleSetting(key){state.settings[key]=!state.settings[key];saveState();applySettings();toast(`${key==='compact'?'Compact view':key==='darkMode'?'Dark mode':'Setting'} ${state.settings[key]?'enabled':'disabled'}`);rerenderCurrent()}
  function applySettings(){app.classList.toggle('compact',!!state.settings.compact)}
  function rerenderCurrent(){if(state.view==='workspace')renderWorkspace();else if(state.view==='favorites')renderFavorites();else if(state.view==='recent')renderRecent();else if(state.view==='settings')renderSettings();else if(state.view==='all')renderLibrary(state.category,state.category==='All'?'Tool Library':state.category);else if(state.view==='detail'){renderRail()}else renderCommand()}
  function routeView(view){state.query=''; if(view==='command')renderCommand();else if(view==='workspace')renderWorkspace();else if(view==='all')renderLibrary('All');else if(view==='favorites')renderFavorites();else if(view==='recent')renderRecent();else if(view==='settings')renderSettings();else if(view==='about')renderAbout()}

  function bindRail(){
    rail.querySelectorAll('[data-tool]').forEach(el=>el.addEventListener('click',()=>renderToolDetail(el.dataset.tool)));
    rail.querySelectorAll('[data-view]').forEach(el=>el.addEventListener('click',()=>routeView(el.dataset.view)));
    rail.querySelectorAll('[data-category]').forEach(el=>el.addEventListener('click',()=>renderLibrary(el.dataset.category,el.dataset.category==='All'?'Tool Library':el.dataset.category)));
    rail.querySelectorAll('[data-setting]').forEach(el=>el.addEventListener('click',()=>toggleSetting(el.dataset.setting)));
  }

  function toast(message){const el=document.createElement('div');el.className='toast';el.textContent=message;toastRegion.appendChild(el);setTimeout(()=>el.remove(),2400)}

  document.querySelectorAll('.sidebar [data-view]').forEach(b=>b.addEventListener('click',()=>routeView(b.dataset.view)));
  document.querySelectorAll('.sidebar [data-category]').forEach(b=>b.addEventListener('click',()=>{state.query='';renderLibrary(b.dataset.category,b.dataset.category)}));

  document.querySelectorAll('[data-window]').forEach(b=>b.addEventListener('click',()=>window.chrome?.webview?.postMessage(`window:${b.dataset.window}`)));
  document.getElementById('titlebar')?.addEventListener('dblclick',e=>{if(!e.target.closest('button'))window.chrome?.webview?.postMessage('window:maximize')});
  document.getElementById('titlebar')?.addEventListener('pointerdown',e=>{if(e.button===0&&!e.target.closest('button'))window.chrome?.webview?.postMessage('window:drag')});

  document.addEventListener('keydown',e=>{
    if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){
      e.preventDefault();
      const input=document.getElementById('global-search');
      if(input){input.focus();input.select()}else{renderCommand();requestAnimationFrame(()=>document.getElementById('global-search')?.focus())}
    }
    if(e.key==='Escape'&&state.view==='detail') renderCommand();
  });

  hydrateIcons();
  applySettings();
  renderCommand();
})();
