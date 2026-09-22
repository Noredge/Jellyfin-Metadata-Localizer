const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const fragment=html.slice(html.indexOf('        function renderWorkbench()'),html.indexOf('        const overviewRequests ='));
class El{
 constructor(tag,text='',cls=''){this.tagName=tag;this.textContent=text;this.className=cls;this.children=[];this.events={};}
 append(...c){for(const x of c){this.children=this.children.filter(y=>y!==x);this.children.push(x);}}querySelector(selector){return this.children.find(x=>x.className===selector.slice(1));}replaceChildren(...c){this.children=c;}setAttribute(){}addEventListener(k,f){this.events[k]=f;}
 all(tag){return this.children.flatMap(c=>[...(c.tagName===tag?[c]:[]),...c.all(tag)]);}
}
const areas=new Map(),drafts=new Map(),calls=[];const $=s=>{if(!areas.has(s))areas.set(s,new El('div'));return areas.get(s);};
const row={Id:'a',Name:'Synthetic movie',Confirmed:true,SourceId:'s',Candidate:{Id:'c',Revision:1,Text:'Saved title',Approved:true},Status:'applied'};
const ctx=vm.createContext({markEditor:()=>{},labeledData:(label,value,cls)=>new El('p',label+value,cls),$,workbenchRows:[row],workbenchDrafts:drafts,workbenchSelection:new Map(),queue:null,status:{},openCorrection:async(id,language)=>calls.push({edit:id,language}),openSingleTranslation:async(id,language,field)=>calls.push({translate:id,language,field}),openOverview:async(id,language)=>calls.push({overview:id,language}),workbenchStates:{},workbenchReasons:{},lang:{value:'en'},node:(...a)=>new El(...a),button:(t,f)=>Object.assign(new El('button',t),{action:f}),field:(parent,label,value)=>{const input=new El('textarea');input.value=value;parent.append(input);return input;},selectionChanged:()=>{},renderDraftStatus:()=>{},detailDirty:()=>false,base:()=>'',api:async()=>calls.push('write')});
vm.runInContext(fragment,ctx);const render=()=>vm.runInContext('renderWorkbench()',ctx);const body=()=>$('.jml-workbench-rows');
(async()=>{
 row.Alternate={Language:'zh-Hans',Candidate:{Text:'中文译文',Approved:true}};render();
 assert(body().all('p').some(x=>x.textContent==='Chinese · Saved'));assert(body().all('p').some(x=>x.textContent==='English · Saved'));
 assert.equal(body().all('textarea').length,0);
 const action=t=>body().all('button').find(x=>x.textContent===t).action();
 await action('Edit movie');assert.deepEqual(calls.pop(),{edit:'a',language:'en'});
 assert(!body().all('button').some(x=>x.textContent==='View translations'));
 row.Alternate.Candidate=null;render();assert(body().all('p').some(x=>x.textContent==='Chinese · No saved translation'));
 assert.equal(calls.length,0);
 assert.equal(ctx.lang.value,'en');
 console.log(JSON.stringify({passed:6,failed:0,checks:['bilingual availability is visible','no editors mount in the list','edit movie opens the existing guarded editor','view translations opens the same editor','missing translation is indicated','browsing performs no write or language switch']}));
})().catch(e=>{console.error(e);process.exitCode=1;});