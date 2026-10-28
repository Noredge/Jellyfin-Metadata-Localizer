import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const line=prefix=>{const found=html.split(/\r?\n/).find(x=>x.startsWith(prefix));assert(found,prefix);return found;};
const functions=[line('async function run('),line('async function loadState('),line('async function loadOverview('),line('function validateConnectionDraft('),html.slice(html.indexOf('function invalidatePoll('),html.indexOf('async function start(')),line('async function start(')].join('\n');
const checks=[];
const check=(name,fn)=>{fn();checks.push({name,passed:true});};
function fixture(){
 const elements=new Map();const get=selector=>{if(!elements.has(selector))elements.set(selector,{open:false,value:'',textContent:'',innerHTML:'initial',focus(){this.focused=true;}});return elements.get(selector);};
 const calls=[];let handler;
 const intervals=new Set();let created=0;
 const c=vm.createContext({AbortController,Map,document:{hidden:false},state:{ready:true,health:{}},active:'overview',polling:false,pollAbort:null,pollEpoch:0,pageVisible:true,statusRevision:0,statusOwners:new Map(),startEpoch:0,timer:null,clearInterval:id=>intervals.delete(id),setInterval:()=>{const id=++created;intervals.add(id);return id;},show:()=>{},
  $:get,name:String,labels:{Name:'标题',Overview:'简介',Genres:'分类'},escape:String,recentBatches:()=>'',
  api:async(url,body,signal)=>{calls.push({url,signal});if(handler)return handler(url,signal);return url==='state'?{ready:true,health:{},settings:{language:'zh-Hans'},libraries:[]}:{fields:[],issues:[],batches:[]};}});
 c.status=(text,error=false,target='#luStatus',owner='operation')=>{c.statusRevision++;c.statusOwners.set(target,owner);get(target).textContent=text;get(target).error=error;};
 vm.runInContext(functions,c);
 return {c,get,calls,intervals,setHandler:fn=>handler=fn};
}
let f=fixture(),resolve;
f.setHandler(url=>url==='state'?new Promise(r=>resolve=r):{fields:[],issues:[],batches:[]});
let p=f.c.poll();await f.c.poll();
check('only one periodic refresh can be in flight',()=>assert.equal(f.calls.length,1));
resolve({ready:true,health:{},settings:{language:'zh-Hans'},libraries:[]});await p;
check('normal refresh updates coverage and releases polling gate',()=>{assert.equal(f.calls.length,2);assert.equal(f.c.polling,false);assert.match(f.get('#luCoverage').innerHTML,/标题/);});

f=fixture();f.setHandler(()=>new Promise(r=>resolve=r));p=f.c.poll();
await f.c.run(async()=>{f.c.state={ready:true,marker:'newer'};f.c.status('设置已保存');});
resolve({ready:true,health:{},settings:{language:'en'},libraries:[]});await p;
check('old poll response cannot overwrite newer manual state or DOM',()=>{assert.equal(f.c.state.marker,'newer');assert.equal(f.get('#luCoverage').innerHTML,'initial');assert.equal(f.get('#luStatus').textContent,'设置已保存');assert(f.calls[0].signal.aborted);});

f=fixture();let reject;f.setHandler(()=>new Promise((_,r)=>reject=r));p=f.c.poll();
await f.c.run(async()=>f.c.status('操作完成'));reject(new Error('Failed to fetch'));await p;
check('late periodic failure cannot replace a newer operation result',()=>assert.equal(f.get('#luStatus').textContent,'操作完成'));

f=fixture();f.setHandler(()=>{throw new Error('Failed to fetch');});await f.c.poll();
check('current periodic errors are still visible',()=>assert.equal(f.get('#luStatus').textContent,'Failed to fetch'));
f.setHandler(null);await f.c.poll();
check('successful refresh clears its own previous error',()=>assert.equal(f.get('#luStatus').textContent,''));
f.c.status('设置已保存');await f.c.poll();
check('later healthy polls preserve operation confirmation',()=>assert.equal(f.get('#luStatus').textContent,'设置已保存'));

f=fixture();f.c.document.hidden=true;await f.c.poll();f.c.document.hidden=false;f.c.pageVisible=false;await f.c.poll();
check('hidden document and inactive plugin page send no periodic requests',()=>assert.equal(f.calls.length,0));
f=fixture();f.setHandler(()=>new Promise((_,r)=>reject=r));p=f.c.poll();f.c.pageVisible=false;f.c.invalidatePoll();reject(new Error('Failed to fetch'));await p;
check('leaving page aborts poll and suppresses late errors',()=>{assert(f.calls[0].signal.aborted);assert.equal(f.get('#luStatus').textContent,'');});
f=fixture();const starts=[];f.setHandler(()=>new Promise(r=>starts.push(r)));const first=f.c.start(),second=f.c.start();
starts[1]({ready:true,health:{},settings:{language:'zh-Hans'},libraries:[]});await second;starts[0]({ready:true,health:{},settings:{language:'en'},libraries:[]});await first;
check('overlapping viewshow initialization leaves one timer and newest state',()=>{assert.equal(f.intervals.size,1);assert.equal(f.c.state.settings.language,'zh-Hans');});

f=fixture();for(const [selector,value] of [['#luBudget','512'],['#luOverviewBudget','4096'],['#luTimeout','60'],['#luRpm','30']])f.get(selector).value=value;
f.get('#luBudget').value='0';
check('invalid output budget is caught before any save request',()=>{assert.throws(()=>f.c.validateConnectionDraft(),/标题\/分类输出上限/);assert(f.get('#luBudget').focused);assert.equal(f.calls.length,0);});
f.get('#luBudget').value='64';f.get('#luOverviewBudget').value='32768';
check('valid boundary budgets need no translation test',()=>assert.doesNotThrow(()=>f.c.validateConnectionDraft()));

const apiContext=vm.createContext({errors:{},normalize:x=>x,ApiClient:{setRequestHeaders(){},getUrl:x=>x},fetch:async()=>({ok:false,json:async()=>({error:'invalid_connection_parameter',parameter:'OverviewMaxOutputTokens'})})});
vm.runInContext(line('async function api('),apiContext);
let parameterError;try{await apiContext.api('connection',{});}catch(error){parameterError=error;}
check('server parameter validation identifies the exact field in UI',()=>assert.match(parameterError.message,/简介输出上限/));
const result={status:'passed',checks,realModelRequests:0};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
