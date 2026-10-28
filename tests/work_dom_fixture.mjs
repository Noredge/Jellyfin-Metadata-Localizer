import fs from 'node:fs';
import vm from 'node:vm';
export const html=fs.readFileSync(new URL('../src/Localizer.Plugin/Web/unified.html',import.meta.url),'utf8');
export function workFixture(){
 const calls=[],elements=new Map();let handler=async()=>[];
 const list={details:[],value:'initial',get innerHTML(){return this.value;},set innerHTML(value){this.value=value;this.details=[...value.matchAll(/<details\s+([^>]+)><summary>([^<]*)<\/summary>/g)].map(match=>{
  const dataset=Object.fromEntries([...match[1].matchAll(/data-([a-z-]+)="([^"]*)"/g)].map(x=>[x[1].replace(/-([a-z])/g,(_,c)=>c.toUpperCase()),x[2]]));
  const nodes={summary:{textContent:match[2]},div:{textContent:''},'[data-full-original]':{textContent:''},'[data-full-result]':{textContent:''}};
  return {dataset,open:false,events:[],querySelector:s=>nodes[s],addEventListener(type,fn){this.events.push({type,fn});},replaceWith(other){list.details[list.details.indexOf(this)]=other;}};
 });}};
 elements.set('#luWorkList',list);elements.set('#luWorkDialog',{open:true});
 const get=s=>s==='#luWorkDialog details[open]'?list.details.find(x=>x.open):elements.get(s)||(()=>{const value={open:false,textContent:'',disabled:false};elements.set(s,value);return value;})();
 const all=s=>s==='#luWorkList details'?[...list.details]:s==='[data-work-text]'?list.details.filter(x=>x.dataset.workText):s==='[data-attempt]'?list.details.filter(x=>x.dataset.attempt):[];
 const c=vm.createContext({URLSearchParams,Map,Date,Math,AbortController,termOffset:0,personOffset:0,issueOffset:0,issueKind:"translation",issueCode:"failure",workOffset:0,workBatch:null,workTarget:null,$:get,$$:all,name:String,escape:x=>String(x??''),attemptText:JSON.stringify,run:fn=>fn(),api:async(route,body,signal)=>{calls.push({route,signal});return handler(route,body,signal);},polling:false,pollAbort:null,pollEpoch:0,pageVisible:true,statusRevision:0,statusOwners:new Map(),document:{hidden:false},active:'overview',state:{ready:true,health:{}},status:()=>{}});
 for(const name of ['beginRead','cancelRead','workView','workVersion','loadWork','loadWorkText','loadWorkAttempts','retryWork','retryWithConsent','cancelWorkConsent','openWork','loadTerms','loadPeople','loadIssues']){const start=html.indexOf('function '+name+'(');vm.runInContext((html.slice(start-6,start)==='async '?'async ':'')+html.slice(start,html.indexOf('\n',start)),c);}
 vm.runInContext(html.slice(html.indexOf('function invalidatePoll('),html.indexOf('async function start(')),c);
 return {c,list,calls,get,all,setHandler:fn=>handler=fn};
}
