import test from 'node:test';
import assert from 'node:assert/strict';
import {
  POLICY_VERSION, DEFAULT_CAPTURE_SETTINGS, normalizeCaptureSettings,
  classifyDownloadUrl, evaluateCapture, makeManualOffer,
  validateBrowserMessage, redactedOfferSummary
} from '../contract/capture-policy.mjs';

const ID = '123e4567-e89b-42d3-a456-426614174000';
const target = 'https://files.example.net/package.zip?signature=secret-value';
const e = (extra = {}) => ({
  trigger: 'context_menu_link', linkUrl: target, incognito: false, ...extra
});

test('default policy is manual-only with no websites', () => {
  assert.equal(DEFAULT_CAPTURE_SETTINGS.schemaVersion, POLICY_VERSION);
  assert.equal(DEFAULT_CAPTURE_SETTINGS.mode, 'manual');
  assert.deepEqual(DEFAULT_CAPTURE_SETTINGS.allowedPageOrigins, []);
  assert.equal(DEFAULT_CAPTURE_SETTINGS.allowPrivateManual, false);
});

test('explicit HTTPS link can be offered', () => {
  assert.deepEqual(evaluateCapture(e()), {
    decision: 'offer', reason: 'EXPLICIT_USER_ACTION', displayHost: 'files.example.net'
  });
});

test('manual link never needs downloads API permission', () => {
  assert.equal(evaluateCapture(e({downloadsPermission:false})).decision, 'offer');
});

test('invalid schemes and credentials are rejected', () => {
  for (const url of [
    'javascript:alert(1)','data:text/plain,a','blob:https://x.test/uuid',
    'file:///tmp/file.txt','ftp://files.example.net/archive.zip',
    'https://user:pass@files.example.net/a.zip','/relative/path','',
  ]) {
    assert.equal(classifyDownloadUrl(url).ok, false, url);
    assert.equal(evaluateCapture(e({linkUrl:url})).decision, 'ignore', url);
  }
});

test('private browsing ignored by default', () => {
  assert.equal(evaluateCapture(e({incognito:true})).reason, 'PRIVATE_MODE_DISABLED');
});

test('private manual requires explicit config, not suggestions', () => {
  assert.equal(evaluateCapture(e({incognito:true}), {allowPrivateManual:true}).decision, 'offer');
  assert.equal(evaluateCapture({
    trigger:'browser_download_created', linkUrl:target,
    incognito:true, downloadsPermission:true,
    initiatingPageOrigin:'https://example.org'
  },{mode:'suggest', allowedPageOrigins:['https://example.org']}).decision,'ignore');
});

test('HTTP requires explicit confirmation unless user enables it', () => {
  const url='http://files.example.net/a.zip';
  assert.deepEqual(evaluateCapture(e({linkUrl:url})), {
    decision:'confirm',reason:'INSECURE_HTTP',displayHost:'files.example.net'
  });
  assert.equal(evaluateCapture(e({linkUrl:url}),{allowInsecureHttpManual:true}).decision,'offer');
});

test('local network literals require confirmation', () => {
  for (const host of ['localhost','127.0.0.1','192.168.1.5','10.1.2.3','172.16.4.1','169.254.5.6','[::1]']) {
    const result=evaluateCapture(e({linkUrl:'https://'+host+'/x'}));
    assert.equal(result.decision,'confirm', host);
  }
});

test('default browser download events are ignored', () => {
  assert.equal(evaluateCapture({
    trigger:'browser_download_created', linkUrl:target,downloadsPermission:true
  }).reason,'SUGGESTIONS_DISABLED');
});

test('optional downloads permission must actually be granted', () => {
  const event={trigger:'browser_download_created',linkUrl:target,downloadsPermission:false,
    initiatingPageOrigin:'https://site.test'};
  assert.equal(evaluateCapture(event,{mode:'suggest',allowedPageOrigins:['https://site.test']}).reason,
    'PERMISSION_NOT_GRANTED');
});

test('suggestions require exact initiating-page origin', () => {
  const settings={mode:'suggest',allowedPageOrigins:['https://site.test']};
  const event={trigger:'browser_download_created',linkUrl:target,downloadsPermission:true,
    initiatingPageOrigin:'https://site.test'};
  assert.equal(evaluateCapture(event,settings).decision,'suggest');
  for(const origin of [undefined,'https://sub.site.test','http://site.test',
    'https://site.test.evil.example','https://site.test/path']) {
    assert.equal(evaluateCapture({...event,initiatingPageOrigin:origin},settings).decision,'ignore',origin);
  }
});

test('suggestions never silently hand off, even on allowlisted sites', () => {
  assert.equal(evaluateCapture({
    trigger:'browser_download_created',linkUrl:target,downloadsPermission:true,
    initiatingPageOrigin:'https://site.test'
  },{mode:'suggest',allowedPageOrigins:['https://site.test']}).decision,'suggest');
});

test('HTTP and local targets are never suggested automatically', () => {
  const settings={mode:'suggest',allowedPageOrigins:['https://site.test']};
  const event={trigger:'browser_download_created',downloadsPermission:true,
    initiatingPageOrigin:'https://site.test'};
  for(const url of ['http://public.example/file','https://127.0.0.1/file']) {
    assert.equal(evaluateCapture({...event,linkUrl:url},settings).reason,'SENSITIVE_TARGET');
  }
});

test('settings strip wildcard origins, paths, and duplicates', () => {
  const settings=normalizeCaptureSettings({
    mode:'automatic',allowPrivateManual:1,allowedPageOrigins:[
      'https://site.test/','https://site.test','https://site.test/path',
      'https://*.site.test/','https://site.test?token=private','ftp://site.test/'
    ]
  });
  assert.equal(settings.mode,'manual');
  assert.equal(settings.allowPrivateManual,false);
  assert.deepEqual(settings.allowedPageOrigins,['https://site.test']);
  assert.equal(settings.confirmLocalNetwork,true);
});

test('manual offer has protocol version, safe hint, no fragment', () => {
  const message=makeManualOffer({
    id:ID,linkUrl:target+'#do-not-leak',fileName:'../bad\\file<>.zip'
  });
  assert.equal(message.protocolVersion,1);
  assert.equal(message.type,'download.offer');
  assert.equal(message.payload.trigger,'context_menu_link');
  assert.equal(message.payload.url,target);
  assert.equal(message.payload.suggestedFileName,'.._bad_file__.zip');
  assert.equal(validateBrowserMessage(message).ok,true);
});

test('offer retains signed query only in transient payload, not summaries', () => {
  const m=makeManualOffer({id:ID,linkUrl:target});
  const summary=redactedOfferSummary(m);
  assert.equal(summary.host,'files.example.net');
  assert.equal(JSON.stringify(summary).includes('secret-value'),false);
  assert.equal(JSON.stringify(summary).includes('package.zip'),false);
  assert.equal(m.payload.url.includes('secret-value'),true);
});

test('native request rejects unknown commands and missing ID', () => {
  assert.equal(validateBrowserMessage({protocolVersion:1,id:ID,type:'shell.run'}).ok,false);
  assert.equal(validateBrowserMessage({protocolVersion:1,type:'bridge.hello'}).ok,false);
  assert.equal(validateBrowserMessage({protocolVersion:2,id:ID,type:'bridge.hello'}).ok,false);
  assert.equal(validateBrowserMessage({protocolVersion:1,id:ID,type:'bridge.hello'}).ok,true);
});

test('native request rejects extra privileges or hidden payloads', () => {
  const msg=makeManualOffer({id:ID,linkUrl:target});
  assert.equal(validateBrowserMessage({...msg,payload:{...msg.payload,headers:{Cookie:'x'}}}).ok,false);
  assert.equal(validateBrowserMessage({...msg,payload:{...msg.payload,trigger:'automatic'}}).ok,false);
  assert.equal(validateBrowserMessage({...msg,payload:{...msg.payload,url:'file:///x'}}).ok,false);
  assert.equal(validateBrowserMessage({...msg,payload:{...msg.payload,suggestedFileName:'..\\evil'}}).ok,false);
  assert.equal(validateBrowserMessage({protocolVersion:1,id:ID,type:'bridge.hello',payload:{url:'x'}}).ok,false);
});

test('request IDs must be UUIDs',()=>{
  assert.throws(()=>makeManualOffer({id:'not-uuid',linkUrl:target}));
  assert.equal(validateBrowserMessage({protocolVersion:1,id:'x',type:'bridge.hello'}).ok,false);
});

test('oversized or invalid link input is refused',()=>{
  const massive='https://files.example.net/'+('x'.repeat(8300));
  assert.equal(classifyDownloadUrl(massive).ok,false);
  assert.throws(()=>makeManualOffer({id:ID,linkUrl:massive}));
});

test('unknown triggers do not initiate transfers',()=>{
  assert.equal(evaluateCapture(e({trigger:'page_script'})).reason,'UNSUPPORTED_TRIGGER');
});
