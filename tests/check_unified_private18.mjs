import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const checks=[];const check=(name,fn)=>{fn();checks.push({name,passed:true});};
function extract(name){const start=html.indexOf('function '+name+'(');assert(start>=0);return (html.slice(start-6,start)==='async '?'async ':'')+html.slice(start,html.indexOf('\n',start));}
const elements=new Map();const get=id=>{if(!elements.has(id))elements.set(id,{innerHTML:'',textContent:'',disabled:false,open:false,showModal(){this.open=true;},close(){this.open=false;}});return elements.get(id);};
let response;const calls=[];
const c=vm.createContext({$:get,$$:()=>[],api:async route=>{calls.push(route);return response;},escape:x=>String(x??''),name:x=>x,URLSearchParams,Date,Math,AbortController,
  issueKind:'translation',issueCode:'synthetic_failure',issueOffset:0,workBatch:null,workTarget:null,workOffset:0,labels:{Name:'标题',Overview:'简介',Genres:'分类'},status:()=>{},recentBatches:()=>''});
for(const name of ['beginRead','workView','workVersion','loadIssues','loadWork','openWork','loadOverview','retryWork'])vm.runInContext(extract(name),c);
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
check('delegated term action opens exact work without generating translation',()=>{assert(html.includes('await openWork(null,el.dataset.issueWork)'));assert(html.includes("$('#luIssueDialog').close()"));});
// Parse the entire embedded application in addition to the isolated function tests.
const scripts=[...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)];for(const script of scripts)new vm.Script(script[1]);
check('whole embedded application parses',()=>assert(scripts.length>0));
const result={status:'passed',checks,realModelRequests:0};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
