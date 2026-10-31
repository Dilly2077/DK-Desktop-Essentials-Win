/* In-memory demo adapter for Slice 32.6A. No network calls, filesystem writes or persistence. */
(function (root, factory) {
  const make = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = make;
  else root.DKDownloadsMock = make;
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';
  const MIB = 1024 * 1024;
  const FIXED = '2026-10-16T18:00:00.000Z';
  function fixture(id, fileName, state, done, total, extra = {}) {
    return {
      id:'demo-' + id, fileName, displayHost:'example.org', state,
      transferredBytes:done, expectedBytes:total, bytesPerSecond:0,
      createdUtc:FIXED, updatedUtc:FIXED, priority:'Normal',
      resumeCapability:'Unknown', revision:0, ...extra
    };
  }
  const SCENARIOS = Object.freeze({
    mixed:[
      fixture(1,'Windows_Installer_2026.iso','Downloading',680*MIB,2200*MIB,{bytesPerSecond:12*MIB,resumeCapability:'Supported',displayHost:'mirror.example.org'}),
      fixture(2,'reference-materials.zip','Downloading',91*MIB,150*MIB,{bytesPerSecond:3*MIB,resumeCapability:'Supported'}),
      fixture(3,'lecture-notes.pdf','Queued',0,44*MIB),
      fixture(4,'archive-backup.7z','Paused',400*MIB,900*MIB,{resumeCapability:'Supported'}),
      fixture(5,'release-notes.txt','Completed',2*MIB,2*MIB),
      fixture(6,'outdated-resource.pdf','Failed',20*MIB,30*MIB,{errorCode:'HttpNotFound'}),
      fixture(7,'unknown-size-file.bin','Downloading',8*MIB,null,{bytesPerSecond:0,resumeCapability:'Unknown'}),
      fixture(8,'long-' + 'course-document-'.repeat(13) + '2026.pdf','Retrying',2*MIB,80*MIB,{errorCode:'Timeout'})
    ],
    empty:[],
    missing:[
      fixture(9,'unnamed','Downloading',0,null,{displayHost:'Unknown source',bytesPerSecond:null,resumeCapability:'Unknown'}),
      fixture(10,'bad-resume.zip','Paused',12*MIB,22*MIB,{resumeCapability:'Unsupported'}),
      fixture(11,'write-access.dat','Failed',3*MIB,13*MIB,{errorCode:'PermissionDenied'}),
      fixture(12,'corrupt.part','Failed',12*MIB,12*MIB,{errorCode:'ChecksumMismatch'})
    ],
    many:Array.from({length:250},(_,i) => fixture(1000+i,'sample_' + String(i).padStart(3,'0') + '.zip',
      i%9===0?'Failed':i%5===0?'Paused':i%4===0?'Completed':i%3===0?'Downloading':'Queued',
      i*MIB,500*MIB,{bytesPerSecond:i%3===0?3*MIB:null,resumeCapability:i%5===0?'Supported':'Unknown'}))
  });
  const clone = value => JSON.parse(JSON.stringify(value));
  function createMockAdapter(scenario = 'mixed') {
    let jobs = clone(SCENARIOS[scenario] || SCENARIOS.mixed);
    let counter = 5000;
    const listeners = new Set();
    const caps = Object.freeze({
      mode:'mock', browseDestination:false, openFolder:false,
      start:true, pause:true, resume:true, retry:true, cancel:true, remove:true
    });
    function notify() { for (const f of listeners) f(); }
    function find(id) {
      const j = jobs.find(x => x.id === id);
      if (!j) throw new Error('The demo job is no longer in the list.');
      return j;
    }
    async function list() { return clone(jobs); }
    async function get(id) { return clone(find(id)); }
    async function enqueue(request) {
      const core = typeof module !== 'undefined' && module.exports && typeof require === 'function'
        ? require('./downloads-core.js') : root.DKDownloadsCore;
      const parsed = core.validateSource(request.url);
      if (!parsed.ok) throw new Error(parsed.error);
      if (!String(request.destinationDirectory||'').trim()) throw new Error('Choose a destination folder.');
      const job = fixture(++counter,core.decodeFilename(request.fileName||parsed.filename),
        request.startNow?'Downloading':'Queued',0,40*MIB,{
          displayHost:parsed.host, resumeCapability:'Supported',
          bytesPerSecond:request.startNow?2*MIB:0,
          destinationDirectory:String(request.destinationDirectory).trim()
        });
      jobs = [job,...jobs]; notify(); return clone(job);
    }
    async function action(method,id) {
      const j = find(id);
      const core = typeof module !== 'undefined' && module.exports && typeof require === 'function'
        ? require('./downloads-core.js') : root.DKDownloadsCore;
      if (method === 'openFolder') throw new Error('Folder opening is unavailable in demo mode. No file was created.');
      if (!core.actions(j,caps)[method]) throw new Error('That control is unavailable for this download.');
      if (method === 'remove') { jobs = jobs.filter(x => x.id !== id); notify(); return null; }
      if (method === 'start' || method === 'resume') {
        j.state = 'Downloading'; j.bytesPerSecond = 2*MIB;
      } else if (method === 'retry') {
        j.state = 'Queued'; j.bytesPerSecond = 0; j.errorCode = null;
      } else if (method === 'pause') {
        j.state = 'Paused'; j.bytesPerSecond = 0;
      } else if (method === 'cancel') {
        j.state = 'Cancelled'; j.bytesPerSecond = 0;
      } else throw new Error('Unsupported demo action.');
      j.revision++; j.updatedUtc = new Date(Date.parse(FIXED)+j.revision*1000).toISOString();
      notify(); return clone(j);
    }
    function advance(seconds=10) {
      const n = Number(seconds);
      if (!Number.isFinite(n) || n < 0 || n > 3600) throw new Error('Invalid simulated duration.');
      for (const j of jobs) {
        if (j.state !== 'Downloading' || !j.bytesPerSecond) continue;
        j.transferredBytes += j.bytesPerSecond*n;
        if (j.expectedBytes !== null && j.transferredBytes >= j.expectedBytes) {
          j.transferredBytes = j.expectedBytes; j.state = 'Completed'; j.bytesPerSecond = 0;
        }
        j.revision++;
      }
      notify();
    }
    return {
      capabilities:caps,
      list,get,enqueue,advance,
      start:id=>action('start',id), pause:id=>action('pause',id),
      resume:id=>action('resume',id), retry:id=>action('retry',id),
      cancel:id=>action('cancel',id), remove:id=>action('remove',id),
      openFolder:id=>action('openFolder',id),
      chooseFolder:async()=>{ throw new Error('Windows folder picker is not available in demo mode. Type a sample folder path.'); },
      subscribe(fn){ listeners.add(fn); return ()=>listeners.delete(fn); },
      setScenario(name){ if (!Object.hasOwn(SCENARIOS,name)) throw new Error('Unknown demo scenario.'); jobs=clone(SCENARIOS[name]); notify(); },
      getScenarioNames(){ return Object.keys(SCENARIOS); }
    };
  }
  return Object.freeze({createMockAdapter, SCENARIOS});
});
