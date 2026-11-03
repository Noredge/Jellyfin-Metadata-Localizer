import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
import {functionSource,bindingSource,pollingSource} from './web_sources.mjs';
import {html,workFixture} from './work_dom_fixture.mjs';
const allChecks=[];

async function issueLinks(){
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const checks=[];const check=(name,fn)=>{fn();checks.push({name,passed:true});};
const extract = functionSource;
const fixture=workFixture(),{c,get}=fixture;let response;const calls=[];
c.api=async route=>{calls.push(route);return response;};c.labels={Name:'标题',Overview:'简介',Genres:'分类'};c.recentBatches=()=>'';
vm.runInContext(extract('loadOverview'),c);
response={items:[{target_kind:'term',name:'测试词条',field:'Term',work_id:'term-work',media_id:null,error_code:'synthetic_failure'}],total:1};await c.loadIssues();
check('independent failure opens a term task instead of a null movie',()=>{const s=get('#luIssueList').innerHTML;assert(s.includes('data-issue-work="term-work"'));assert(!s.includes('data-issue-movie'));assert(s.includes('查看词条任务'));});
response={items:[{target_kind:'media',name:'影片',field:'Genres',work_id:'shared',media_id:'movie-id',error_code:'synthetic_failure'}],total:1};await c.loadIssues();
check('linked failure retains the direct movie and field entry',()=>assert(get('#luIssueList').innerHTML.includes('data-issue-movie="movie-id" data-issue-field="Genres"')));
const row={id:'term-work',field:'Term',state:'failed',step:'request',original:'测试词条',request_ms:12,save_ms:0,save_timing_measured:0,display_ms:0,attempt_count:1};
response=[row];await c.openWork(null,'term-work');
check('term entry requests only that exact work and opens its dialog',()=>{assert(calls.at(-1).includes('workId=term-work'));assert(get('#luWorkDialog').open);});
check('old zero save time is clearly unmeasured',()=>{assert(get('#luWorkList').innerHTML.includes('保存 未计量'));assert(!get('#luWorkList').innerHTML.includes('保存 0 ms'));});
response=[{...row,save_ms:4,save_timing_measured:1}];await c.loadWork();
check('new measured save time displays its actual milliseconds',()=>assert(get('#luWorkList').innerHTML.includes('保存 4 ms')));
response={reused:true,workId:'replacement-work'};await c.retryWork('term-work');
check('retry reuse redirects an exact-work view to the active replacement',()=>assert.equal(c.workTarget,'replacement-work'));
response=[];await c.openWork('recent-batch');
check('opening another batch clears the exact-work filter',()=>{assert(calls.at(-1).includes('batchId=recent-batch'));assert(!calls.at(-1).includes('workId='));});
response={fields:[],batches:[],issues:[{issue_kind:'translation',error_code:'synthetic_failure',count:1}]};await c.loadOverview();
check('overview uses a neutral issue entry for mixed target types',()=>{assert(get('#luIssues').innerHTML.includes('查看问题'));assert(!get('#luIssues').innerHTML.includes('查看影片'));});
check('delegated term action opens exact work without generating translation',()=>{assert.match(html,/await openWork\(null,\s*el\.dataset\.issueWork\)/);assert(html.includes("$('#luIssueDialog').close()"));});
// Parse the entire embedded application in addition to the isolated function tests.
const scripts=[...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)];for(const script of scripts)new vm.Script(script[1]);
check('whole embedded application parses',()=>assert(scripts.length>0));

allChecks.push(...checks);
}
await issueLinks();

async function boundedDetails(){
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const checks=[];const check=(name,fn)=>{fn();checks.push({name,passed:true});};
const extract = functionSource;
const fixture=workFixture(),{c,get}=fixture;const calls=[];let handler=async()=>[];
c.api=async route=>{calls.push(route);return handler(route);};
const row={id:'work-fixture',field:'Overview',state:'completed',step:'verify',original:'原'.repeat(180),original_truncated:1,result_text:'译'.repeat(180),result_truncated:1,request_ms:5,save_ms:3,save_timing_measured:1,display_ms:7,attempt_count:1};
handler=async()=>[row];await c.loadWork();
check('truncated work summary offers collapsed full text without a fetch',()=>{assert(get('#luWorkList').innerHTML.includes('data-work-text="work-fixture"'));assert(get('#luWorkList').innerHTML.includes('完整原文与结果'));assert.equal(calls.length,1);});
handler=async()=>[{...row,original_truncated:0,result_truncated:0}];await c.loadWork();
check('short work records need no extra full text control',()=>assert(!get('#luWorkList').innerHTML.includes('data-work-text')));
const originalNode={textContent:''},resultNode={textContent:''};
const el={open:false,dataset:{workText:'work/fixture'},querySelector:s=>s==='[data-full-original]'?originalNode:resultNode};
const before=calls.length;await c.loadWorkText(el);
check('collapsed full text makes no network request',()=>assert.equal(calls.length,before));
let resolve;handler=()=>new Promise(done=>resolve=done);el.open=true;const pending=c.loadWorkText(el);await c.loadWorkText(el);
check('repeated expansion coalesces pending exact detail reads',()=>{assert.equal(calls.length,before+1);assert.equal(calls.at(-1),'work/work%2Ffixture');});
const original='<script>unsafe()</script>'+'🧭'.repeat(220),saved='<img src=x onerror=unsafe()>'+'译'.repeat(500);
el.open=false;resolve({original,result_text:saved});await pending;
check('full original and result are preserved as text without reopening',()=>{assert.equal(originalNode.textContent,original);assert.equal(resultNode.textContent,saved);assert(!el.open);assert.equal(el.dataset.loaded,'true');assert(!el.dataset.loading);});
el.open=true;await c.loadWorkText(el);
check('reopening an already loaded record reuses its full text',()=>assert.equal(calls.length,before+1));
delete el.dataset.loaded;handler=async()=>{throw new Error('temporary detail error');};await assert.rejects(c.loadWorkText(el));
check('failed detail read releases its gate and keeps a retry possible',()=>{assert(!el.dataset.loading);assert(!el.dataset.loaded);});
handler=async()=>({original:'recovered',result_text:null});await c.loadWorkText(el);
check('a later detail retry safely renders a missing result',()=>{assert.equal(originalNode.textContent,'recovered');assert.equal(resultNode.textContent,'');});
check('full source and result stay outside automatic interface translation',()=>{assert(html.includes('class="lu-data" data-full-original'));assert(html.includes('class="lu-data" data-full-result'));});

allChecks.push(...checks);
}
await boundedDetails();

async function liveDetails(){
const checks=[],check=(name,fn)=>{fn();checks.push({name,passed:true});};
const row={id:'active',field:'Overview',state:'running',step:'request',attempt_count:1,request_ms:0,save_ms:0,display_ms:0,original:'原'.repeat(180),original_truncated:1,result_text:null,result_truncated:0,updated_utc:'one'};
let current={...row},saved=null;const f=workFixture();f.setHandler(async route=>route.startsWith('work?')?[current]:route.startsWith('work/')?{...current,original:'完整原文',result_text:saved}:[{state:current.state}]);
await f.c.loadWork();const el=f.all('[data-work-text]')[0];el.open=true;await f.c.loadWorkText(el);
check('running empty result is displayed without becoming a permanent cache',()=>{assert.equal(el.querySelector('[data-full-result]').textContent,'');assert(!el.dataset.loaded);});
current={...row,state:'completed',step:'verify',updated_utc:'two',result_text:'新结果'};saved='<b>完整新结果</b>';await f.c.loadWork(true);
check('completion refreshes result while preserving the expanded DOM node',()=>{assert.equal(f.all('[data-work-text]')[0],el);assert(el.open);assert.equal(el.querySelector('[data-full-result]').textContent,saved);assert.equal(el.dataset.loaded,'true');assert(f.list.innerHTML.includes('completed'));});
const calls=f.calls.length;el.open=false;el.open=true;await f.c.loadWorkText(el);
check('unchanged terminal full text is cached across reopen',()=>assert.equal(f.calls.length,calls));
current={...current,updated_utc:'three',result_text:'较新结果'};saved='较新的完整结果';await f.c.loadWork(true);
check('a newer saved version invalidates the terminal full-text cache',()=>assert.equal(el.querySelector('[data-full-result]').textContent,saved));
check('periodic refresh binds one toggle handler per preserved node',()=>assert.equal(el.events.filter(x=>x.type==='toggle').length,1));
const attempts=f.all('[data-attempt]')[0];attempts.open=true;await f.c.loadWorkAttempts(attempts);current={...current,state:'running',updated_utc:'four',attempt_count:2};await f.c.loadWork(true);
check('request detail survives refresh and reloads changed attempt metadata',()=>{assert.equal(f.all('[data-attempt]')[0],attempts);assert(attempts.open);assert.match(attempts.querySelector('summary').textContent,/2/);assert.match(attempts.querySelector('div').textContent,/running/);});
const beforePoll=f.calls.length;await f.c.poll();
check('expanded full text no longer stops work polling',()=>assert(f.calls.slice(beforePoll).some(x=>x.route.startsWith('work?'))));
let resolve;delete el.dataset.loaded;f.setHandler(()=>new Promise(done=>resolve=done));const pending=f.c.loadWorkText(el);el.dataset.workVersion='newer';resolve({original:'stale',result_text:'stale'});await pending;
check('late full text cannot overwrite a newer task version',()=>{assert.notEqual(el.querySelector('[data-full-result]').textContent,'stale');assert(!el.dataset.loading);});
f.setHandler(async()=>{throw new Error('temporary');});await assert.rejects(f.c.loadWorkText(el));
check('failed refresh remains retryable without losing expanded content',()=>{assert(el.open);assert(!el.dataset.loading);assert.equal(el.querySelector('[data-full-result]').textContent,saved);});
f.setHandler(async()=>({original:'recovered',result_text:'restored'}));await f.c.loadWorkText(el);
check('a subsequent refresh replaces retained stale content',()=>assert.equal(el.querySelector('[data-full-result]').textContent,'restored'));

allChecks.push(...checks);
}
await liveDetails();

async function requestOrderingAndConsent(){
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
let anchorTop=150;reading.isConnected=true;reading.getBoundingClientRect=()=>({top:anchorTop,bottom:anchorTop+100});f.get('#luWorkDialog').getBoundingClientRect=()=>({top:0,bottom:500});f.get('#luWorkDialog').scrollTop=50;
page[0]={...page[0],updated_utc:'changed'};const changedRow=f.list.children[0],replace=changedRow.replaceWith;
changedRow.replaceWith=function(next){replace.call(this,next);anchorTop=1000;};await f.c.loadWork(true);
check('poll restores the visible reader scroll anchor after DOM movement',()=>assert.equal(f.get('#luWorkDialog').scrollTop,900));
reading.open=false;await f.c.loadWork(true);
check('closing the pinned reader releases it on the next refresh',()=>{assert(!f.c.workView().rows.has('history-29'));assert.equal(f.c.workView().rows.size,30);});
f=workFixture();let resolve;f.setHandler(()=>new Promise(done=>resolve=done));const inFlight=f.c.loadWork();f.c.cancelRead();resolve([row('hidden')]);await inFlight;
check('leaving a view cancels manual reads and suppresses their late DOM writes',()=>{assert(f.calls[0].signal.aborted);assert.equal(f.list.innerHTML,'initial');});

allChecks.push(...checks);
}
await requestOrderingAndConsent();

async function expandedPolling(){
const checks=[],row={id:'fixture',field:'Term',state:'completed',step:'verify',attempt_count:1,request_ms:5,save_ms:1,display_ms:0,updated_utc:'one'};
let f=workFixture();f.setHandler(async()=>[row]);await f.c.loadWork();const detail=f.all('[data-attempt]')[0];detail.querySelector('div').textContent='expanded request';detail.dataset.loaded='true';
let resolve;f.setHandler(route=>route.startsWith('work?')?new Promise(done=>resolve=done):[]);const pending=f.c.loadWork(true);detail.open=true;resolve([row]);await pending;
assert.equal(f.all('[data-attempt]')[0],detail);assert.equal(detail.querySelector('div').textContent,'expanded request');checks.push({name:'poll response preserves request panel opened while fetch was pending',passed:true});
f.setHandler(async()=>[row]);await f.c.loadWork(false);assert.notEqual(f.all('[data-attempt]')[0],detail);checks.push({name:'explicit pagination replaces previous detail nodes',passed:true});
await f.c.loadWork(true);assert.match(f.list.innerHTML,/data-attempt="fixture"/);checks.push({name:'collapsed periodic work updates normally',passed:true});

allChecks.push(...checks);
}
await expandedPolling();

const result={status:'passed',checks:allChecks,realModelRequests:0};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
