import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const checks=[];const check=(name,fn)=>{fn();checks.push({name,passed:true});};
function extract(name){const start=html.indexOf('function '+name+'(');assert(start>=0);return html.slice(start,html.indexOf('\n',start));}
function fixture(){
 const elements=new Map(),get=id=>{if(!elements.has(id))elements.set(id,{value:'',disabled:false,open:false,textContent:'',innerHTML:'',dataset:{},showModal(){this.open=true;},close(){this.open=false;}});return elements.get(id);};
 const calls=[],notices=[];let resolve,reject;
 const c=vm.createContext({$:get,$$:()=>[],fillLoading:false,preview:{id:'old'},state:{settings:{revision:3}},uid:()=> 'command',loadState:async()=>{},status:(...x)=>notices.push(x),escape:x=>String(x??''),URLSearchParams,personOffset:0,issueOffset:0,issueKind:'translation',issueCode:'output_budget',name:x=>x,normalize:x=>x,
  api:async(route,body)=>{calls.push({route,body});return await new Promise((a,b)=>{resolve=a;reject=b;});},detailAbort:null,AbortController,editor:null,drafts:{},fieldName:'Name',historySource:null,historyOffset:0,canCloseEditor:()=>true,renderEditor:()=>{},editPerson:()=>{}});
 for(const name of ['beginRead','makePreview','loadPeople','openMovie','loadIssues','attemptText'])vm.runInContext((['beginRead','attemptText'].includes(name)?'':'async ')+extract(name),c);
 return {c,get,calls,notices,resolve:x=>resolve(x),reject:x=>reject(x)};
}
let f=fixture(),p=f.c.makePreview(null);await new Promise(setImmediate);
check('fill shows disabled loading dialog before the server finishes',()=>{assert(f.get('#luPreview').open);assert(f.get('#luCommit').disabled);assert(f.get('#luFillAll').disabled);assert.equal(f.c.preview,null);assert(f.get('#luPreviewText').textContent.includes('正在检查'));});
await f.c.makePreview(null);check('double clicking fill does not issue another preview',()=>assert.equal(f.calls.length,1));
f.resolve({id:'new',count:1,skipped:632});await p;
check('missing-only preview shows skipped count and enables only eligible work',()=>{assert(!f.get('#luCommit').disabled);assert(!f.get('#luFillAll').disabled);assert(f.get('#luPreviewText').textContent.includes('632'));assert(f.get('#luPreviewText').textContent.includes('仅补缺译 1'));assert.equal(f.c.preview.id,'new');});
f=fixture();p=f.c.makePreview(null);await new Promise(setImmediate);f.resolve({id:'zero',count:0,skipped:633});await p;
check('zero missing never enables submit',()=>{assert(f.get('#luCommit').disabled);assert(f.get('#luPreviewText').textContent.includes('当前没有'));});
f=fixture();p=f.c.makePreview(null);await new Promise(setImmediate);f.reject(new Error('failed'));await p;
check('preview error retains disabled submit and restores buttons',()=>{assert(f.get('#luCommit').disabled);assert(!f.get('#luFillAll').disabled);assert.equal(f.c.preview,null);assert.equal(f.notices.at(-1)[0],'failed');});
f=fixture();p=f.c.makePreview(null);await new Promise(setImmediate);f.get('#luPreview').close();f.resolve({id:'late',count:1});await p;
check('closed preview cannot revive a stale submit',()=>assert.equal(f.c.preview,null));
f=fixture();p=f.c.loadPeople();await new Promise(setImmediate);f.resolve({total:65,items:Array.from({length:30},(_,i)=>({original:'Name '+i}))});await p;
check('person page uses bounded API with search and next navigation',()=>{assert(f.calls[0].route.includes('offset=0&limit=30'));assert(f.get('#luPersonPrev').disabled);assert(!f.get('#luPersonNext').disabled);assert.equal(f.get('#luPersonPaging').textContent,'1–30 / 65');});
f.c.personOffset=60;p=f.c.loadPeople();await new Promise(setImmediate);f.resolve({total:65,items:Array.from({length:5},(_,i)=>({original:'Tail '+i}))});await p;
check('last person page disables next without expanding all rules',()=>{assert(!f.get('#luPersonPrev').disabled);assert(f.get('#luPersonNext').disabled);assert.equal((f.get('#luPeopleList').innerHTML.match(/<li>/g)||[]).length,5);});
f=fixture();p=f.c.loadIssues();await new Promise(setImmediate);f.resolve({total:1,items:[{media_id:'movie',field:'Overview',name:'WANZ-790',error_code:'output_budget'}]});await p;
check('issue list carries exact movie and field links',()=>{assert(f.get('#luIssueList').innerHTML.includes('data-issue-movie="movie" data-issue-field="Overview"'));assert(f.get('#luIssueNext').disabled);});
p=f.c.openMovie('movie','Overview');await new Promise(setImmediate);f.resolve({id:'movie',fields:[{state:{field:'Name'},observation:{display:'WANZ-790'}},{state:{field:'Overview'}}]});await p;
check('issue movie opens directly at failing field',()=>assert.equal(f.c.fieldName,'Overview'));
const result={status:'passed',checks,realModelRequests:0};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
