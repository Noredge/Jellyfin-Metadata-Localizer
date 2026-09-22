const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
new vm.Script(html.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1]);
const fragment=html.slice(html.indexOf('        const overviewRequests ='),html.indexOf('        async function saveAndApplyCandidate('));
class El{
 constructor(tag,text='',cls=''){this.tag=tag;this.textContent=text;this.className=cls;this.children=[];this.events={};this.value='';}
 append(...c){this.children.push(...c);}replaceChildren(...c){this.children=c;}addEventListener(k,f){this.events[k]=f;}scrollIntoView(){}
 get options(){return this.children;}all(tag){return this.children.flatMap(x=>[...(x.tag===tag?[x]:[]),...x.all(tag)]);}
 querySelector(selector){return this.children.find(x=>x.className===selector.slice(1))||this.children.map(x=>x.querySelector(selector)).find(Boolean);}
}
const detail=new El('div'),calls=[],status={},shell={};let library='libraries/a',networkFail=false,generationState='Completed';let serial=0;
const data={Display:'Original display',Source:{Hash:'hash',Path:'source.nfo',Text:'Original full synopsis'},State:{Revision:2,Sources:[{Version:1,Hash:'hash',NfoPath:'source.nfo'}],Candidates:[{Id:'c',SourceVersion:1,Language:'en',Text:'Saved overview',HumanEdited:false}],Failures:[]}};
const operations=[];const ctx=vm.createContext({markEditor:()=>{},requireModel:()=>true,serviceLabel:id=>id,failurePanel:()=>{},Map,Promise,Error,encodeURIComponent,crypto:{randomUUID:()=>`request-${++serial}`},lang:{value:'en'},detail,status,active:null,
 $:()=>shell,base:()=>library,serviceSettings:{DefaultServiceId:'local',Profiles:[{Id:'local',Model:'fake'}]},selectedService:()=>({Id:'local'}),
 detailDirty:()=>detail.all('textarea').some(x=>x.value!==x.defaultValue),node:(...a)=>new El(...a),button:(text,action)=>Object.assign(new El('button',text),{action}),
 field:(p,label,value)=>{const x=new El('textarea');x.value=x.defaultValue=value;p.append(x);return x;},run:f=>f(),
 api:async(path,body)=>{calls.push({path,body});if(!body)return path.endsWith('/operations')?operations:structuredClone(data);
 if(path.endsWith('/generate')){if(networkFail)throw new Error('lost response');return {State:generationState,Error:generationState==='Failed'?'rate_limit':null};}
 if(path.endsWith('/edit')){data.State.Revision++;data.State.Candidates[0].Text=body.Text;return {};}
 return {State:'Applied'};}});
vm.runInContext(fragment,ctx);
const open=()=>vm.runInContext('openOverview("item")',ctx),btn=t=>detail.all('button').find(x=>x.textContent===t),input=()=>detail.all('textarea')[0];
(async()=>{
 await open();assert.equal(input().value,'Saved overview');assert(btn('Apply saved overview'));assert(detail.all('p').some(x=>x.textContent==='Original full synopsis'));
 input().value='Draft';const before=calls.length;await btn('Retranslate').action();assert.equal(calls.length,before);await open();assert.equal(input().value,'Draft');
 await btn('Save edit').action();assert.equal(input().value,'Draft');assert.equal(input().defaultValue,'Draft');assert(calls.find(x=>x.path.endsWith('/edit')).body.Revision===2);
 await btn('Apply saved overview').action();const applied=calls.find(x=>x.path.endsWith('/apply')).body;assert.equal(applied.ExpectedDisplay,'Original display');assert.equal(applied.CandidateId,'c');
 networkFail=true;try{await btn('Retranslate').action();}catch{}const first=calls.filter(x=>x.path.endsWith('/generate')).at(-1).body;
 networkFail=false;await btn('Retranslate').action();const second=calls.filter(x=>x.path.endsWith('/generate')).at(-1).body;assert.equal(first.RequestId,second.RequestId);assert.equal(second.ServiceId,'local');
 generationState='Failed';await btn('Retranslate').action();const failed=calls.filter(x=>x.path.endsWith('/generate')).at(-1).body;await btn('Retranslate').action();assert.notEqual(calls.filter(x=>x.path.endsWith('/generate')).at(-1).body.RequestId,failed.RequestId);
 generationState='Uncertain';await btn('Retranslate').action();assert(btn('Allow a new request (may use API quota again)'));const uncertain=calls.filter(x=>x.path.endsWith('/generate')).at(-1).body.RequestId;await btn('Allow a new request (may use API quota again)').action();generationState='Completed';await btn('Retranslate').action();assert.notEqual(calls.filter(x=>x.path.endsWith('/generate')).at(-1).body.RequestId,uncertain);
 operations.push({Id:'pending',State:'Writing'});await open();await btn('Check interrupted write').action();assert(calls.some(x=>x.path.endsWith('/operations/pending/recover')));
 data.Source.Hash='changed';await open();assert(!btn('Apply saved overview'));assert(btn('Use this NFO source'));
 console.log(JSON.stringify({passed:9,failed:0,checks:['English candidate and full source rendered','dirty draft blocks generation/navigation','save revision and refresh','apply expected display','uncertain request reuses ID','explicit failed retry gets new ID','uncertain retry requires explicit new-request allowance','interrupted write recovery','changed source blocks translation/application']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
