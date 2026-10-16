/* node --test tests/DownloadUI.Tests/downloads.test.cjs
   Pure frontend-only tests; never starts the WPF app or opens the network. */
const {test} = require('node:test');
const assert = require('node:assert/strict');
const core = require('../../src/DKDesktopEssentials/ui/downloads-core.js');
const {createMockAdapter} = require('../../src/DKDesktopEssentials/ui/downloads-mock.js');

test('formatting handles known and unknown sizes',()=>{
  assert.equal(core.bytes(null),'Unknown');
  assert.equal(core.bytes(0),'0 B');
  assert.equal(core.bytes(1536),'1.50 KB');
  assert.equal(core.bytes(1024*1024),'1.00 MB');
  assert.equal(core.bytes(NaN),'Unknown');
});
test('progress clamps and allows unknown total',()=>{
  assert.equal(core.progress({transferredBytes:5,expectedBytes:10}),50);
  assert.equal(core.progress({transferredBytes:999,expectedBytes:10}),100);
  assert.equal(core.progress({transferredBytes:0,expectedBytes:null}),null);
});
test('ETA never invents speed or missing total',()=>{
  assert.equal(core.eta({state:'Downloading',expectedBytes:200,transferredBytes:100,bytesPerSecond:25}),4);
  assert.equal(core.eta({state:'Downloading',expectedBytes:null,transferredBytes:100,bytesPerSecond:25}),null);
  assert.equal(core.eta({state:'Downloading',expectedBytes:200,transferredBytes:100,bytesPerSecond:0}),null);
  assert.equal(core.eta({state:'Paused',expectedBytes:200,transferredBytes:100,bytesPerSecond:25}),null);
});
test('status groups include verifying and retrying',()=>{
  assert.equal(core.statusGroup({state:'Verifying'}),'active');
  assert.equal(core.statusGroup({state:'Retrying'}),'queued');
  assert.equal(core.statusGroup({state:'Cancelled'}),'cancelled');
});
test('search and filters are case insensitive',()=>{
  const jobs=[core.normalize({id:'1',fileName:'Notes.PDF',displayHost:'example.org',state:'Completed'}),
    core.normalize({id:'2',fileName:'movie.mp4',displayHost:'mirror.test',state:'Downloading'})];
  assert.equal(core.filter(jobs,'notes').length,1);
  assert.equal(core.filter(jobs,'mirror','active').length,1);
  assert.equal(core.filter(jobs,'missing','all').length,0);
});
test('no open-folder operation unless completed and supported',()=>{
  const cap={openFolder:false};
  assert.equal(core.actions(core.normalize({state:'Completed'}),cap).openFolder,false);
  assert.equal(core.actions(core.normalize({state:'Completed'}),{}).openFolder,true);
  assert.equal(core.actions(core.normalize({state:'Queued'}),{}).openFolder,false);
});
test('unsupported resume is visibly unavailable',()=>{
  const paused=core.normalize({state:'Paused',resumeCapability:'Unsupported'});
  assert.equal(core.actions(paused,{}).resume,false);
  assert.equal(core.actions({...paused,resumeCapability:'Unknown'},{}).resume,true);
});
test('URL validation enforces HTTP(S) and excludes embedded credentials',()=>{
  assert.equal(core.validateSource('https://example.org/a.zip').ok,true);
  assert.equal(core.validateSource('file:///etc/passwd').ok,false);
  assert.equal(core.validateSource('ftp://example.org').ok,false);
  assert.equal(core.validateSource('javascript:alert(1)').ok,false);
  assert.equal(core.validateSource('https://user:pass@example.org/a').ok,false);
  assert.equal(core.validateSource('garbage').ok,false);
});
test('filename hints are sanitised',()=>{
  const p=core.validateSource('https://example.org/hello%20world%3F.zip?token=secret');
  assert.equal(p.filename,'hello world_.zip');
  assert.equal(core.decodeFilename('../../bad\\file.exe'),'.._.._bad_file.exe');
});
test('summary groups report counts',()=>{
  const jobs=['Queued','Downloading','Paused','Failed','Completed','Cancelled'].map(state=>core.normalize({state}));
  assert.deepEqual(core.summary(jobs),{all:6,active:1,queued:1,paused:1,completed:1,failed:1,cancelled:1});
});
test('mixed demo contains representative states',async()=>{
  const api=createMockAdapter('mixed');
  const jobs=await api.list();
  assert.equal(jobs.length,8);
  assert.ok(jobs.some(j=>j.state==='Downloading'));
  assert.ok(jobs.some(j=>j.state==='Failed'));
});
test('empty and many-job fixtures remain deterministic',async()=>{
  assert.equal((await createMockAdapter('empty').list()).length,0);
  assert.equal((await createMockAdapter('many').list()).length,250);
});
test('paused demo job can resume and pause',async()=>{
  const api=createMockAdapter('mixed');
  assert.equal((await api.get('demo-4')).state,'Paused');
  await api.resume('demo-4');
  assert.equal((await api.get('demo-4')).state,'Downloading');
  await api.pause('demo-4');
  assert.equal((await api.get('demo-4')).state,'Paused');
});
test('failed demo job must retry before starting',async()=>{
  const api=createMockAdapter('mixed');
  await assert.rejects(api.start('demo-6'));
  await api.retry('demo-6');
  assert.equal((await api.get('demo-6')).state,'Queued');
  await api.start('demo-6');
  assert.equal((await api.get('demo-6')).state,'Downloading');
});
test('new URL is validated and only host retained in demo record',async()=>{
  const api=createMockAdapter('empty');
  const item=await api.enqueue({url:'https://example.org/file.zip?secret=token',destinationDirectory:'C:\\Downloads',startNow:false});
  assert.equal(item.state,'Queued');
  assert.equal(item.fileName,'file.zip');
  assert.equal(item.displayHost,'example.org');
  assert.equal(JSON.stringify(await api.list()).includes('secret=token'),false);
  await assert.rejects(api.enqueue({url:'file:///secret',destinationDirectory:'C:\\Downloads'}));
});
test('mock progresses only when explicitly advanced',async()=>{
  const api=createMockAdapter('mixed');
  const before=await api.get('demo-1');
  assert.equal((await api.get('demo-1')).transferredBytes,before.transferredBytes);
  api.advance(10);
  assert.ok((await api.get('demo-1')).transferredBytes>before.transferredBytes);
});
test('cancel, remove and open folder limitations',async()=>{
  const api=createMockAdapter('mixed');
  await api.cancel('demo-3');
  assert.equal((await api.get('demo-3')).state,'Cancelled');
  await api.remove('demo-3');
  await assert.rejects(api.get('demo-3'));
  await assert.rejects(api.openFolder('demo-5'),/unavailable/);
});
test('subscriptions receive updates and can be disposed',async()=>{
  const api=createMockAdapter('empty');let changes=0;
  const unsub=api.subscribe(()=>changes++);
  await api.enqueue({url:'https://example.org/a',destinationDirectory:'C:\\Downloads'});
  assert.equal(changes,1);
  unsub();
  api.setScenario('mixed');
  assert.equal(changes,1);
});
