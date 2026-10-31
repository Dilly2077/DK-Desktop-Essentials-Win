const {test}=require('node:test');
const assert=require('node:assert/strict');
const {connect,project}=require('../../src/DKDesktopEssentials/ui/downloads-native.js');
const verbs=['start','pause','resume','retry','cancel','remove','openFolder'];
const caps={protocolVersion:1,mode:'native',available:true,protectedDestinations:true,events:true,enqueue:true,browseDestination:true,...Object.fromEntries(verbs.map(v=>[v,true]))};
const job=()=>({id:'12345678-1234-1234-1234-123456789abc',state:'Queued',fileName:'payload.bin',displayHost:'example.org',transferredBytes:0,expectedBytes:100,capabilities:{start:true}});
class Bridge {
  listeners=new Set(); sent=[]; response=(command)=>command==='capabilities'?caps:command==='list'?[]:null;
  addEventListener(type,callback){assert.equal(type,'message');this.listeners.add(callback);}
  removeEventListener(type,callback){this.listeners.delete(callback);}
  emit(data){for(const callback of this.listeners)callback({data});}
  postMessage(raw){const message=JSON.parse(raw);this.sent.push(message);const result=this.response(message.type.slice(10),message.payload);
    if(result!==undefined)queueMicrotask(()=>this.emit({type:'downloads:response',command:message.type,requestId:message.requestId,ok:true,result}));}
}
const options=()=>{let n=0;return {timeoutMs:50,idFactory:()=>`test-${++n}`};};
test('native requires a successful capability handshake and never substitutes demo data',async()=>{
  await assert.rejects(connect(null),/busy|shutting down/);
  for(const changed of [{protocolVersion:2},{mode:'mock'},{protectedDestinations:false},{events:false},{available:null},{resume:undefined}]){
    const bridge=new Bridge();bridge.response=()=>({...caps,...changed});
    await assert.rejects(connect(bridge,options()));assert.equal(bridge.listeners.size,0);
  }
});
test('native commands only begin after handshake; missing per-job capabilities stay disabled',async()=>{
  const bridge=new Bridge();const api=await connect(bridge,options());
  assert.deepEqual(bridge.sent.map(x=>x.type),['downloads:capabilities']);
  assert.equal(api.capabilities.mode,'native');assert.equal(project(job()).capabilities.pause,false);
  for(const verb of verbs){bridge.response=()=>null;await api[verb](job().id);assert.equal(bridge.sent.at(-1).type,`downloads:${verb}`);}
  api.dispose();assert.equal(bridge.listeners.size,0);
});
test('native rejects uncorrelated malformed messages and times out with an honest uncertain outcome',async()=>{
  const bridge=new Bridge();const api=await connect(bridge,options());bridge.response=()=>undefined;
  const list=api.list();const request=bridge.sent.at(-1);
  bridge.emit('{broken');bridge.emit(null);bridge.emit({type:'downloads:response',requestId:request.requestId,command:'downloads:remove',ok:true,result:[]});
  bridge.emit({type:'downloads:response',requestId:'unknown',command:request.type,ok:true,result:[]});
  await assert.rejects(list,e=>e.code==='NativeTimeout'&&e.message.includes('may already have been accepted'));
  api.dispose();
});
test('native strips source URLs credentials paths and raw server errors from returned views',async()=>{
  const leaked={...job(),sourceUrl:'https://example.org/private?token=secret',destinationPath:'C:\\secret',temporaryPath:'C:\\partial',safeErrorDetail:'secret',errorCode:'HttpForbidden'};
  const view=project(leaked);const text=JSON.stringify(view);
  assert(!text.includes('secret')&&!text.includes('private')&&!text.includes('sourceUrl')&&!text.includes('destinationPath'));
  const bridge=new Bridge();const api=await connect(bridge,options());bridge.response=()=>undefined;
  const pending=api.pause(job().id);const req=bridge.sent.at(-1);
  bridge.emit({type:'downloads:response',command:req.type,requestId:req.requestId,ok:false,error:'https://private?token=secret',errorCode:'HttpForbidden'});
  await assert.rejects(pending,e=>e.code==='HttpForbidden'&&!e.message.includes('secret'));api.dispose();
});
test('native accepts only validated redacted job shapes and honest speed',()=>{
  for(const changed of [{id:'bad'},{state:'Ready'},{fileName:'../secret'},{displayHost:'https://host?token=secret'},{transferredBytes:-1},{expectedBytes:Infinity}])assert.throws(()=>project({...job(),...changed}));
  assert.equal(project({...job(),displayHost:'[::1]'}).displayHost,'[::1]');
  assert.equal(project({...job(),state:'Verifying',bytesPerSecond:100}).bytesPerSecond,null);
});
test('native uses an opaque picker grant and never authorizes a typed destination path',async()=>{
  const bridge=new Bridge();const api=await connect(bridge,options());
  await assert.rejects(api.enqueue({destinationDirectory:'C:\\typed'}),e=>e.code==='DestinationInvalid');
  bridge.response=(command)=>command==='chooseFolder'?{destinationToken:'a'.repeat(32),displayName:'Downloads'}:job();
  assert.equal(await api.chooseFolder(),'Downloads');
  await api.enqueue({url:'https://example.org/signed?token=secret',fileName:'payload.bin',destinationDirectory:'Downloads',startNow:false});
  const payload=bridge.sent.at(-1).payload;assert.equal(payload.destinationToken,'a'.repeat(32));assert.equal(payload.startNow,false);assert(!('destinationDirectory' in payload));
  bridge.response=()=>null;assert.equal(await api.chooseFolder(),null);
  await assert.rejects(api.enqueue({destinationDirectory:'C:\\typed'}));api.dispose();
});
test('native subscriptions attach once, invalidate on events and dispose pending requests',async()=>{
  const bridge=new Bridge();const api=await connect(bridge,options());let a=0,b=0;
  const unsubA=api.subscribe(()=>a++),unsubB=api.subscribe(()=>b++);await new Promise(resolve=>setImmediate(resolve));
  assert.equal(bridge.sent.filter(x=>x.type==='downloads:subscribe').length,1);
  const before=a;bridge.emit({type:'downloads:event',reason:'refresh'});assert.equal(a,before+1);
  unsubA();assert.equal(bridge.sent.filter(x=>x.type==='downloads:subscribe').length,1);
  unsubB();assert.equal(bridge.sent.at(-1).payload.enabled,false);
  bridge.response=()=>undefined;const pending=api.list();api.dispose();await assert.rejects(pending);
  assert.equal(bridge.listeners.size,0);const count=b;bridge.emit({type:'downloads:event',reason:'refresh'});assert.equal(b,count);
});
test('native aborts handshake and unregisters the listener',async()=>{
  const bridge=new Bridge();bridge.response=()=>undefined;const controller=new AbortController();
  const pending=connect(bridge,{...options(),signal:controller.signal});controller.abort();await assert.rejects(pending);assert.equal(bridge.listeners.size,0);
});
