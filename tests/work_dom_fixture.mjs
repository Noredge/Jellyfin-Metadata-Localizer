import {functionSource,bindingSource,pollingSource} from './web_sources.mjs';
import fs from 'node:fs';
import vm from 'node:vm';
export const html=fs.readFileSync(new URL('../src/Localizer.Plugin/Web/unified.html',import.meta.url),'utf8');
export function workFixture(){
 const calls=[],elements=new Map();let handler=async()=>[];
 const dataset=attributes=>Object.fromEntries([...attributes.matchAll(/data-([a-z-]+)="([^"]*)"/g)].map(x=>[x[1].replace(/-([a-z])/g,(_,c)=>c.toUpperCase()),x[2]]));
 const list={children:[],value:'initial',writes:0,creates:0,
  get details(){return this.children.flatMap(row=>row.details);},
  get innerHTML(){return this.children.length?this.children.map(row=>row.html).join(''):this.value;},
  set innerHTML(value){this.writes++;this.value=value;this.children=value.startsWith('<li')?[parse(value)]:[];for(const row of this.children)row.parent=this;},
  querySelector(selector){return selector==='[data-work-empty]'?this.children.find(row=>row.html.includes('data-work-empty')):null;},
  insertBefore(node,before){if(node.parent){const i=node.parent.children.indexOf(node);if(i>=0)node.parent.children.splice(i,1);}const i=this.children.indexOf(before);this.children.splice(i<0?this.children.length:i,0,node);node.parent=this;}
 };
 function parse(value){
  const row={html:value,dataset:dataset(value.match(/^<li\s+([^>]+)/)?.[1]||''),details:[],parent:null,
   remove(){if(this.parent){const i=this.parent.children.indexOf(this);if(i>=0)this.parent.children.splice(i,1);this.parent=null;}},
   replaceWith(other){const p=this.parent,i=p.children.indexOf(this);p.children[i]=other;other.parent=p;this.parent=null;}};
  row.details=[...value.matchAll(/<details\s+([^>]+)><summary>([^<]*)<\/summary>/g)].map(match=>{
   const nodes={summary:{textContent:match[2]},div:{textContent:''},'[data-full-original]':{textContent:''},'[data-full-result]':{textContent:''}};
   return {dataset:dataset(match[1]),open:false,events:[],querySelector:s=>nodes[s],addEventListener(type,fn){this.events.push({type,fn});},replaceWith(other){row.details[row.details.indexOf(this)]=other;}};
  });
  const start=value.match(/data-stage-start="([^"]*)"/);
  if(start){const elapsed={textContent:''};row.clock={dataset:{stageStart:start[1]},querySelector:()=>elapsed};}
  return row;
 }
 const document={hidden:false,createElement(tag){if(tag!=='template')throw new Error('Unexpected fixture element');return {content:{},set innerHTML(value){list.creates++;this.content.firstElementChild=parse(value);}};}};
 elements.set('#luWorkList',list);elements.set('#luWorkDialog',{open:true,showModal(){this.open=true;},close(){this.open=false;}});
 const get=s=>s==='#luWorkDialog details[open]'?list.details.find(x=>x.open):elements.get(s)||(()=>{const value={open:false,textContent:'',disabled:false,showModal(){this.open=true;},close(){this.open=false;}};elements.set(s,value);return value;})();
 const all=s=>s==='#luWorkList details'?[...list.details]:s==='[data-work-text]'?list.details.filter(x=>x.dataset.workText):s==='[data-attempt]'?list.details.filter(x=>x.dataset.attempt):s==='#luWorkList [data-stage-start]'?list.children.flatMap(row=>row.clock?[row.clock]:[]):[];
 const c=vm.createContext({URLSearchParams,Map,Date,Math,AbortController,termOffset:0,personOffset:0,issueOffset:0,issueKind:"translation",issueCode:"failure",workOffset:0,workBatch:null,workTarget:null,$:get,$$:all,name:String,escape:x=>String(x??''),attemptText:JSON.stringify,run:fn=>fn(),api:async(route,body,signal)=>{calls.push({route,signal});return handler(route,body,signal);},polling:false,pollAbort:null,pollEpoch:0,pageVisible:true,statusRevision:0,statusOwners:new Map(),document,active:'overview',state:{ready:true,health:{}},status:()=>{}});
 for(const name of ['setUiText','setUiHtml','renderWorkRows','updateWorkClocks','beginRead','cancelRead','workView','workVersion','loadWork','loadWorkText','loadWorkAttempts','retryWork','retryWithConsent','cancelWorkConsent','openWork','loadTerms','loadPeople','loadIssues']){vm.runInContext(functionSource(name),c);}
 vm.runInContext(pollingSource(),c);
 return {c,list,calls,get,all,setHandler:fn=>handler=fn};
}
