// Exercise actual task rendering and action handlers without calling a model.
const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const render=html.slice(html.indexOf('        function renderCampaign('),html.indexOf('        async function startCampaign('));
class Element {
  constructor(tag,text=''){this.tagName=tag.toUpperCase();this.textContent=text;this.children=[];this.style={};}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;}
  querySelector(selector){return this.querySelectorAll(selector)[0]||null;}
  querySelectorAll(selector){return this.children.flatMap(x=>[...(x.tagName===selector.toUpperCase()?[x]:[]),...x.querySelectorAll(selector)]);}
  contains(){return false;}
  setAttribute(){} addEventListener(){} focus(){}
}
const area=new Element('div'),status={},calls=[];
const ctx=vm.createContext({labeledData:(label,value,cls)=>new Element('p',label+value),Date,Math,Number,encodeURIComponent,Map,JSON,area,status,
  document:{activeElement:null},$:()=>area,
  node:(tag,text)=>new Element(tag,text),button:(text,action)=>Object.assign(new Element('button',text),{action}),
  checkbox:(parent,text)=>{const input=new Element('input',text);parent.append(input);return input;},
  campaignList:[],campaignStates:{},campaignItemStates:{Translating:'正在翻译'},workbenchReasons:{},
  campaignUncertainAcknowledgements:new Map(),serviceLabel:x=>x,hasDrafts:()=>false,
  campaignUnfinished:j=>['Running','Paused','Interrupted'].includes(j.State),failurePanel:()=>{},
  api:async(path,body)=>calls.push({path,body}),refreshCampaigns:async()=>{},
});
vm.runInContext(render,ctx);
const base={Id:'test',Mode:'library_translate',Language:'multi',ServiceId:'local',Total:1,Pending:0,Translated:0,Applied:0,Saved:0,Review:0,Skipped:0,Failed:0,Items:[]};
function show(changes){ctx.job={...base,...changes};vm.runInContext("renderCampaign(job,'libraries/test','en')",ctx);return area.querySelectorAll('button');}
function action(name){return area.querySelectorAll('button').find(x=>x.textContent===name);}
function text(){return [area,...area.children.flatMap(function walk(x){return [x,...x.children.flatMap(walk)];})].map(x=>x.textContent).join('\n');}
const cases=[];
(async()=>{
 show({State:'Running',WorkerRunning:true,Items:[{State:'Translating',Name:'sample'}]});assert(action('Pause task'));assert(!action('Resume task'));assert(text().includes('正在翻译: sample'));cases.push('running exposes pause and active item');
 show({State:'Paused',WorkerRunning:false,Pending:1,Items:[{State:'Translating',Name:'sample'}]});assert(!text().includes('正在翻译: sample'));await action('Resume task').action();assert.equal(calls.at(-1).body.RetryFailed,false);assert(status.textContent.includes('Task resumed'));cases.push('paused resume updates status and hides stale active item');
 show({State:'CompletedWithErrors',WorkerRunning:false,Failed:1,Items:[{State:'Failed',Name:'sample'}]});assert(!action('Resume task'));assert(action('Retry failed items'));await action('Retry failed items').action();assert.equal(calls.at(-1).body.RetryFailed,true);cases.push('finished failures offer retry without ineffective continue');
 show({State:'Interrupted',WorkerRunning:false,Pending:1});assert(action('Resume task'));assert(text().includes('Requests do not resume automatically'));cases.push('interrupted task requires explicit continuation');
 show({State:'Paused',WorkerRunning:false,Failed:1,Items:[{State:'Uncertain',ItemId:'a',Name:'sample'}]});const count=calls.length;await action('Retry uncertain requests').action();assert.equal(calls.length,count);area.querySelector('input').checked=true;await action('Retry uncertain requests').action();assert.equal(calls.at(-1).body.AcceptUncertainRequest,true);assert(status.textContent.includes('Uncertain retry authorized'));cases.push('uncertain retry requires acknowledgement and clears stale prompt');
 show({State:'Completed',WorkerRunning:false});assert(!action('Resume task'));assert(!action('Pause task'));cases.push('completed task has no running controls');
 show({State:'CompletedWithErrors',Movies:2,Total:5,Fields:[
  {Field:'Name',Language:'en',Total:4,Available:3,Generated:1,Reused:1,OriginUnknown:1,SavedOnly:1,Applied:1,Skipped:0,NeedsAttention:2,Pending:0},
  {Field:'Overview',Language:'zh-Hans',Total:1,Available:1,Generated:0,Reused:1,OriginUnknown:0,SavedOnly:1,Applied:0,Skipped:0,NeedsAttention:0,Pending:0}
 ]});
 let rows=area.querySelectorAll('tr');
 assert.deepEqual(rows[1].children.map(x=>x.textContent),['Titles / English','4','1','1','1','1','1','0','2','0']);
 assert.deepEqual(rows[2].children.map(x=>x.textContent),['Overviews / Chinese','1','0','1','0','1','0','0','0','0']);
 assert(text().includes('including any whose display application failed'));cases.push('mixed title and overview counts render in correct columns');
 show({State:'Completed',Fields:[{Field:'Name',Language:'en',Total:2,Available:2,SavedOnly:0,Applied:2,Skipped:0,NeedsAttention:0,Pending:0}]});
 rows=area.querySelectorAll('tr');assert.deepEqual(rows[1].children.slice(2,5).map(x=>x.textContent),['0','0','2']);cases.push('legacy available count remains origin unknown');
 show({State:'Completed',Retranslate:true,Movies:1,DisplayLanguage:'en'});assert(text().includes('Completed · Translate and display'));assert(text().includes('Retranslate selected scope'));assert(!text().includes('全库重译'));cases.push('single movie retranslation does not claim whole library and shows display intent');
 show({State:'Completed',DisplayLanguage:null});assert(text().includes('Completed · Translate and save'));assert(text().includes('Save only; keep current display'));cases.push('save only task does not claim display changes');
 show({State:'Completed',Skipped:2,Items:[{Field:'Overview',State:'Skipped',ErrorCategory:'overview_empty'},{Field:'Overview',State:'Skipped',ErrorCategory:'overview_nfo_missing'}]});assert(!text().includes('items needing attention'));cases.push('absent overview skips stay out of attention list');
 show({State:'Completed',Skipped:1,Items:[{Field:'Overview',State:'Skipped',ErrorCategory:'overview_nfo_invalid',Name:'Broken source'}]});assert(text().includes('1 items needing attention'));cases.push('malformed overview source remains visible');
 show({State:'Completed',Skipped:1,Items:[{Field:'Name',State:'Skipped',ErrorCategory:'original_display_preserved'}]});assert(!text().includes('items needing attention'));cases.push('explicit original preference is a normal skip');
 console.log(JSON.stringify({passed:cases.length,failed:0,cases},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
