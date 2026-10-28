import fs from 'node:fs';
import assert from 'node:assert/strict';
import {workFixture} from './work_dom_fixture.mjs';
const checks=[],check=(name,fn)=>{fn();checks.push({name,passed:true});};
const row=(id,extra={})=>({id,field:'Term',state:'completed',step:'verify',attempt_count:1,request_ms:1,save_ms:1,display_ms:0,updated_utc:'one',original:'summary',original_truncated:0,result_text:'saved',result_truncated:0,...extra});
let f=workFixture(),pending=[];f.setHandler(route=>new Promise(resolve=>pending.push({route,resolve})));
f.c.workOffset=30;const first=f.c.loadWork();f.c.workOffset=60;const second=f.c.loadWork();pending[1].resolve([row('page-60')]);await second;pending[0].resolve([row('page-30')]);await first;
check('late manual page cannot replace the current work page',()=>{assert(f.list.innerHTML.includes('page-60'));assert(!f.list.innerHTML.includes('page-30'));assert(f.calls[0].signal.aborted);});
for(const [method,key,selector,response] of [
 ['loadTerms','termOffset','#luTermList',id=>[{id,original:id}]],
 ['loadPeople','personOffset','#luPeopleList',id=>({total:100,items:[{id,original:id}]})],
 ['loadIssues','issueOffset','#luIssueList',id=>({total:100,items:[{name:id,media_id:id,field:'Genres',error_code:'failed',target_kind:'media'}]})]
]){
 f=workFixture();pending=[];f.setHandler(()=>new Promise(resolve=>pending.push(resolve)));f.c[key]=30;const older=f.c[method]();f.c[key]=60;const newer=f.c[method]();pending[1](response('newest-page'));await newer;pending[0](response('obsolete-page'));await older;
 check(method+' fences reversed pagination responses',()=>{assert(f.get(selector).innerHTML.includes('newest-page'));assert(!f.get(selector).innerHTML.includes('obsolete-page'));assert(f.calls[0].signal.aborted);});
}
f=workFixture();let current=row('unknown',{state:'paused'}),unknown=true;f.setHandler(async(route,body)=>{
 if(route==='retry'){if(unknown){const error=new Error('unknown');error.code='UnknownOutcomeConsentRequired';throw error;}current={...current,state:'queued',updated_utc:'two'};return {reused:false};}
 return [current];
});await f.c.loadWork();await assert.rejects(f.c.retryWithConsent('unknown'));const version=f.c.workVersion(current);
check('unknown retry stores a version-bound explicit confirmation',()=>{assert.equal(f.c.workView().consents.get('unknown'),version);assert(f.list.innerHTML.includes('data-consent="unknown"'));assert(f.list.innerHTML.includes('data-cancel-consent="unknown"'));});
for(let i=0;i<3;i++)await f.c.loadWork(true);
check('multiple polls retain confirmation without sending a retry',()=>{assert(f.list.innerHTML.includes('data-consent="unknown"'));assert.equal(f.calls.filter(x=>x.route==='retry').length,1);});
await f.c.cancelWorkConsent('unknown');
check('cancel confirmation removes it without acknowledging or resending',()=>{assert(!f.list.innerHTML.includes('data-consent="unknown"'));assert.equal(f.calls.filter(x=>x.route==='retry').length,1);});
await assert.rejects(f.c.retryWithConsent('unknown'));unknown=false;let acceptedBody;f.setHandler(async(route,body)=>{if(route==='retry'){acceptedBody=body;current={...current,state:'queued',updated_utc:'two'};return {reused:false};}return [current];});await f.c.retryWithConsent('unknown',true,version);
check('explicit current confirmation sends its expected version once',()=>{assert.equal(acceptedBody.expectedVersion,version);assert.equal(acceptedBody.acceptUnknown,true);assert(!f.c.workView().consents.has('unknown'));});
let sent=0;f.setHandler(async()=>{sent++;return [current];});await assert.rejects(f.c.retryWithConsent('unknown',true,version));
check('stale confirmation cannot send a request',()=>assert.equal(sent,0));
f=workFixture();current=row('versioned',{state:'paused'});f.setHandler(async(route)=>{if(route==='retry'){const error=new Error('unknown');error.code='UnknownOutcomeConsentRequired';throw error;}return [current];});await f.c.loadWork();await assert.rejects(f.c.retryWithConsent('versioned'));current={...current,updated_utc:'changed',attempt_count:2};await f.c.loadWork(true);
check('work version change clears old confirmation before another decision',()=>assert(!f.list.innerHTML.includes('data-consent="versioned"')));
f=workFixture();const history=Array.from({length:30},(_,n)=>row('history-'+n,{original_truncated:1}));let page=history;const allRows=new Map(history.map(x=>[x.id,x]));f.setHandler(async route=>{if(route.startsWith('work/'))return {original:'full source',result_text:'saved'};const q=new URLSearchParams(route.split('?')[1]);return q.has('workId')?[allRows.get(q.get('workId'))]:page;});await f.c.loadWork();const reading=f.all('[data-work-text]').find(x=>x.dataset.workText==='history-29');reading.open=true;page=[row('new-running',{state:'running'}),...history].slice(0,30);await f.c.loadWork(true);
check('expanded reader stays pinned when new work pushes it off thirty rows',()=>{assert.equal(f.all('[data-work-text]').find(x=>x.dataset.workText==='history-29'),reading);assert(reading.open);assert(f.list.innerHTML.includes('正在查看 · 已移出当前分页'));assert.equal(f.c.workView().rows.size,31);assert.equal(f.get('#luWorkNext').disabled,false);assert(f.calls.some(x=>x.route.includes('workId=history-29')));});
let anchorTop=150;reading.isConnected=true;reading.getBoundingClientRect=()=>({top:anchorTop,bottom:anchorTop+100});f.get('#luWorkDialog').getBoundingClientRect=()=>({top:0,bottom:500});f.get('#luWorkDialog').scrollTop=50;const descriptor=Object.getOwnPropertyDescriptor(f.list,'innerHTML');Object.defineProperty(f.list,'innerHTML',{get:descriptor.get,set(value){descriptor.set.call(this,value);anchorTop=1000;},configurable:true});await f.c.loadWork(true);
check('poll restores the visible reader scroll anchor after DOM movement',()=>assert.equal(f.get('#luWorkDialog').scrollTop,900));
reading.open=false;await f.c.loadWork(true);
check('closing the pinned reader releases it on the next refresh',()=>{assert(!f.c.workView().rows.has('history-29'));assert.equal(f.c.workView().rows.size,30);});
f=workFixture();let resolve;f.setHandler(()=>new Promise(done=>resolve=done));const inFlight=f.c.loadWork();f.c.cancelRead();resolve([row('hidden')]);await inFlight;
check('leaving a view cancels manual reads and suppresses their late DOM writes',()=>{assert(f.calls[0].signal.aborted);assert.equal(f.list.innerHTML,'initial');});
const result={status:'passed',checks,realModelRequests:0};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
