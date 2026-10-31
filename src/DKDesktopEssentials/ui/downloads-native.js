/* Module 32 native boundary. No fetch, shell commands, browser storage or payload logging. */
(function(root,factory){
  const api=factory(root);
  if(typeof module!=='undefined'&&module.exports)module.exports=api;
  else {root.DKDownloadsNative=api;root.DKDownloadsHost=api.host;}
})(typeof window!=='undefined'?window:globalThis,function(root){
  'use strict';
  const verbs=['start','pause','resume','retry','cancel','remove','openFolder'];
  const states=['Queued','Resolving','Downloading','Paused','Retrying','Verifying','Completed','Failed','Cancelled'];
  const errors={
    InvalidUrl:'Enter a valid HTTP or HTTPS link.',UnsupportedProtocol:'Only direct HTTP and HTTPS links are supported.',
    DestinationInvalid:'Choose an existing local folder and a valid Windows filename.',DestinationExists:'That filename exists or is reserved. Choose another filename.',
    PermissionDenied:'Windows denied access to this destination.',DiskFull:'There is not enough space. Free space, then choose Retry.',
    NetworkUnavailable:'The server could not be reached. Check your connection.',Timeout:'The server stopped responding. Check your connection and retry.',
    DnsFailure:'The server name could not be resolved.',TlsFailure:'The secure connection could not be verified.',
    HttpUnauthorized:'The server requires authentication. Submit a valid direct link.',HttpForbidden:'The server denied access. The link may have expired.',
    HttpNotFound:'The file was not found on the server.',HttpRateLimited:'The server limited requests. Wait before retrying.',HttpServerError:'The server reported an error.',
    RangeNotSupported:'This partial download cannot be resumed safely. Retained files were not removed; create a new download.',
    RemoteContentChanged:'The remote file changed. The partial cannot be continued; create a new download.',
    PartialFileInvalid:'The retained partial could not be validated. Create a new download.',ContentLengthMismatch:'The downloaded size did not match the expected size.',
    ChecksumMismatch:'The checksum did not match. The file was not completed.',StorageFailure:'Local queue data or file safety metadata is unavailable. Retained files were not removed.',
    EngineUnavailable:'The action is busy or the app is shutting down. Refresh and try again.',UnexpectedFailure:'The native operation could not be completed.',
    QueueFull:'The queue has reached its safety limit. Cancel and remove older waiting records before adding more.'
  };
  const safeError=code=>Object.hasOwn(errors,code)?errors[code]:errors.UnexpectedFailure;
  const failure=(code='UnexpectedFailure')=>Object.assign(new Error(safeError(code)),{code:Object.hasOwn(errors,code)?code:'UnexpectedFailure'});
  function project(job){
    const id=job?.id;
    if(typeof id!=='string'||!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(id)||!states.includes(job.state)||
      typeof job.fileName!=='string'||job.fileName.length>255||/[\\/\x00-\x1f]/.test(job.fileName)||
      typeof job.displayHost!=='string'||!/^[a-z0-9.:[\]-]{1,255}$/i.test(job.displayHost)||
      !Number.isFinite(job.transferredBytes)||job.transferredBytes<0||
      (job.expectedBytes!=null&&(!Number.isFinite(job.expectedBytes)||job.expectedBytes<0)))throw failure();
    const capabilities=Object.fromEntries(verbs.map(verb=>[verb,job.capabilities?.[verb]===true]));
    return {id,state:job.state,fileName:job.fileName,displayHost:job.displayHost,
      transferredBytes:job.transferredBytes,expectedBytes:job.expectedBytes??null,
      bytesPerSecond:job.state==='Downloading'&&Number.isFinite(job.bytesPerSecond)?Math.max(0,job.bytesPerSecond):null,
      createdUtc:job.createdUtc??null,updatedUtc:job.updatedUtc??null,priority:job.priority,
      errorCode:Object.hasOwn(errors,job.errorCode)?job.errorCode:null,
      safeErrorDetail:job.errorCode?safeError(job.errorCode):null,
      resumeCapability:job.resumeCapability,revision:job.revision,capabilities,
      attempts:job.attempts,nextAttemptUtc:job.nextAttemptUtc,queuePosition:job.queuePosition};
  }
  async function connect(bridge=root.chrome?.webview,{timeoutMs=15000,idFactory=()=>root.crypto.randomUUID(),signal}={}){
    if(!bridge?.postMessage||!bridge.addEventListener||!bridge.removeEventListener)throw failure('EngineUnavailable');
    const pending=new Map(),listeners=new Set();let dead=false,handshake=false,grant=null,subscribed=false;
    const notify=()=>{for(const callback of listeners)callback();};
    function receive(event){
      let data=event.data;
      if(typeof data==='string'){try{data=JSON.parse(data);}catch{return;}}
      if(!data||typeof data!=='object')return;
      if(data.type==='downloads:event'){if(handshake&&data.reason==='refresh')notify();return;}
      const task=pending.get(data.requestId);
      if(data.type!=='downloads:response'||!task||data.command!==`downloads:${task.command}`||typeof data.ok!=='boolean')return;
      pending.delete(data.requestId);clearTimeout(task.timer);
      if(data.ok)task.resolve(data.result);else task.reject(failure(data.errorCode));
    }
    function request(command,payload={}){
      if(dead)return Promise.reject(failure('EngineUnavailable'));
      return new Promise((resolve,reject)=>{
        const requestId=idFactory();
        const timer=command==='chooseFolder'?null:setTimeout(()=>{
          pending.delete(requestId);
          reject(Object.assign(new Error('The native action did not respond. Refresh the list before trying again; the action may already have been accepted.'),{code:'NativeTimeout'}));
        },timeoutMs);
        pending.set(requestId,{command,resolve,reject,timer});
        try{bridge.postMessage(JSON.stringify({type:`downloads:${command}`,requestId,payload}));}
        catch{clearTimeout(timer);pending.delete(requestId);reject(failure('EngineUnavailable'));}
      });
    }
    function dispose(){
      if(dead)return;
      if(subscribed){try{bridge.postMessage(JSON.stringify({type:'downloads:subscribe',requestId:idFactory(),payload:{enabled:false}}));}catch{}}
      dead=true;bridge.removeEventListener('message',receive);signal?.removeEventListener('abort',dispose);
      for(const task of pending.values()){clearTimeout(task.timer);task.reject(failure('EngineUnavailable'));}
      pending.clear();listeners.clear();grant=null;
    }
    bridge.addEventListener('message',receive);signal?.addEventListener('abort',dispose,{once:true});
    if(signal?.aborted){dispose();throw failure('EngineUnavailable');}
    try{
      const caps=await request('capabilities');
      if(caps?.protocolVersion!==1||caps.mode!=='native'||typeof caps.available!=='boolean'||caps.protectedDestinations!==true||caps.events!==true||
        ['enqueue','browseDestination',...verbs].some(key=>typeof caps[key]!=='boolean'))throw failure('EngineUnavailable');
      handshake=true;
      const api={
        capabilities:Object.freeze({...Object.fromEntries(['enqueue','browseDestination',...verbs].map(key=>[key,caps[key]])),mode:'native',
          safeMessage:caps.available===false?errors.StorageFailure:null}),
        list:async()=>{const jobs=await request('list');if(!Array.isArray(jobs)||jobs.length>1000)throw failure();return jobs.map(project);},
        get:async id=>project(await request('get',{id})),
        chooseFolder:async()=>{const chosen=await request('chooseFolder');if(chosen===null)return null;
          if(typeof chosen.destinationToken!=='string'||!/^[0-9a-f]{32}$/i.test(chosen.destinationToken)||typeof chosen.displayName!=='string')throw failure('DestinationInvalid');
          grant={token:chosen.destinationToken,name:chosen.displayName};return grant.name;},
        enqueue:async({url,destinationDirectory,fileName,startNow})=>{
          if(!grant||destinationDirectory!==grant.name)throw failure('DestinationInvalid');
          return project(await request('enqueue',{url,fileName,destinationToken:grant.token,startNow:startNow===true}));
        },
        subscribe(callback){listeners.add(callback);if(!subscribed){subscribed=true;request('subscribe',{enabled:true}).then(notify,notify);}
          return ()=>{listeners.delete(callback);if(!listeners.size&&subscribed){subscribed=false;request('subscribe',{enabled:false}).catch(()=>{});}};},
        dispose
      };
      for(const verb of verbs)api[verb]=async id=>{
        if(!api.capabilities[verb])throw failure('EngineUnavailable');
        const result=await request(verb,{id});return result===null?null:project(result);
      };
      return Object.freeze(api);
    }catch(error){dispose();throw error;}
  }
  let current=null;
  const host={
    async mount(element,{onBack}={}){
      host.dispose();const instance={element,onBack,controller:new AbortController(),adapter:null};current=instance;
      element.innerHTML='<section class="dk-downloads"><div class="dkd-notice" role="status">Connecting to the local download manager…</div></section>';
      try{
        const adapter=await connect(root.chrome?.webview,{signal:instance.controller.signal});
        if(current!==instance){adapter.dispose();return;}
        instance.adapter=adapter;
        root.DKDownloadsUI.mount(element,{adapter,onBack,onDemo:()=>host.demo(instance),onNative:()=>host.mount(element,{onBack})});
      }catch{
        if(current!==instance)return;
        element.innerHTML='<section class="dk-downloads"><h1>Download Manager</h1><div class="dkd-error" role="alert">The native download manager is unavailable. No demo records have been substituted.</div><button type="button" class="dkd-soft" data-host-retry>Retry connection</button> <button type="button" class="dkd-soft" data-host-demo>Development demo (simulated)</button> <button type="button" class="dkd-soft" data-host-back>Back</button></section>';
        element.querySelector('[data-host-retry]').onclick=()=>host.mount(element,{onBack});
        element.querySelector('[data-host-demo]').onclick=()=>host.demo(instance);
        element.querySelector('[data-host-back]').onclick=()=>onBack?.();
      }
    },
    demo(instance=current){
      if(!instance||instance!==current)return;
      const {element,onBack}=instance;instance.controller.abort();instance.adapter?.dispose();root.DKDownloadsUI.dispose();
      instance.adapter=root.DKDownloadsMock.createMockAdapter();
      root.DKDownloadsUI.mount(element,{adapter:instance.adapter,onBack,onNative:()=>host.mount(element,{onBack})});
    },
    dispose(){const instance=current;current=null;if(instance){instance.controller.abort();root.DKDownloadsUI?.dispose();instance.adapter?.dispose?.();}}
  };
  return Object.freeze({connect,project,host});
});
