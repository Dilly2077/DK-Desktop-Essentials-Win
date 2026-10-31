const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const core=require('../../src/DKDesktopEssentials/ui/downloads-core.js');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
// Minimal rendering surface: exercises the actual renderer/lifecycle without
// pretending to test browser layout, focus containment, or native integration.
function fixture(jobs){
  const handlers=new Map();let notify,disposed=0,calls=[];
  const host={innerHTML:'',scrollTop:0,open:false,contains:()=>false,
    addEventListener:(name,handler)=>handlers.set(name,handler),removeEventListener:(name)=>handlers.delete(name),
    querySelector:selector=>selector==='dialog[open]'&&host.open?{}:null,querySelectorAll:()=>[]};
  const window={DKDownloadsCore:core};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/DKDesktopEssentials/ui/downloads-ui.js'),'utf8'),{window,document:{activeElement:null}});
  const adapter={capabilities:{mode:'native',enqueue:true},list:async()=>jobs,
    subscribe:callback=>{notify=callback;return ()=>disposed++;},start:async id=>calls.push(id)};
  const mounted=window.DKDownloadsUI.mount(host,{adapter});
  return {host,handlers,mounted,ui:window.DKDownloadsUI,notify:()=>notify(),calls,disposed:()=>disposed,
    click:dataset=>handlers.get('click')({target:{closest:()=>({dataset})}})};
}
function job(n,state='Queued'){return {id:`job-${n}`,fileName:`file-${n}.bin`,displayHost:'example.org',state,transferredBytes:0,expectedBytes:null,capabilities:{start:true}};}
test('renderer escapes untrusted names and error text while exposing accessible progress',async()=>{
  const f=fixture([{...job(1),fileName:'<img src=x onerror=attack()> & "payload"',safeErrorDetail:'<script>attack()</script>',errorCode:'UnexpectedFailure'}]);await tick();
  assert(!f.host.innerHTML.includes('<img src=x'));assert(!f.host.innerHTML.includes('<script>attack'));
  assert(f.host.innerHTML.includes('&lt;img'));assert(f.host.innerHTML.includes('role="progressbar"'));assert(f.host.innerHTML.includes('aria-valuetext="Unknown total size"'));f.ui.dispose();
});
test('renderer pages 250 jobs in groups of 50 and filters an empty result',async()=>{
  const f=fixture(Array.from({length:250},(_,n)=>job(n)));await tick();
  assert.equal((f.host.innerHTML.match(/class="dkd-download"/g)||[]).length,50);assert(f.host.innerHTML.includes('200 remaining'));
  f.click({dkd:'more'});assert.equal((f.host.innerHTML.match(/class="dkd-download"/g)||[]).length,100);
  f.click({dkdFilter:'failed'});assert(f.host.innerHTML.includes('No matching downloads'));f.ui.dispose();
});
test('renderer preserves an open dialog across subscription refresh and detaches on leaving',async()=>{
  const jobs=[job(1)];const f=fixture(jobs);await tick();f.host.open=true;
  const before=f.host.innerHTML;jobs.push(job(2));f.notify();await tick();assert.equal(f.host.innerHTML,before);
  f.host.open=false;f.handlers.get('close')();assert(f.host.innerHTML.includes('2 of 2 shown'));
  f.ui.dispose();assert.equal(f.disposed(),1);assert.equal(f.handlers.size,0);const after=f.host.innerHTML;f.notify();await tick();assert.equal(f.host.innerHTML,after);
});
test('renderer uses explicit native labels, disables unsafe resume and never auto-selects mock',async()=>{
  const f=fixture([{...job(1,'Paused'),resumeCapability:'Unsupported',capabilities:{resume:false}}]);await tick();
  assert(f.host.innerHTML.includes('Native download manager'));assert(!f.host.innerHTML.includes('DEMO MODE'));
  assert(/data-job-action="resume"[^>]*disabled/.test(f.host.innerHTML));assert(f.host.innerHTML.includes('cannot safely continue'));
  f.ui.dispose();assert.throws(()=>f.ui.mount(f.host),/explicit native or development adapter/);
});
test('renderer passes only the selected eligible start to its adapter',async()=>{
  const f=fixture([job(1),job(2)]);await tick();f.click({jobAction:'start',id:'job-2'});await tick();assert.deepEqual(f.calls,['job-2']);f.ui.dispose();
});
