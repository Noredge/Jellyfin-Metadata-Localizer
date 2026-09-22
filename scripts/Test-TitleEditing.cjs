const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const fragment=html.slice(html.indexOf('        async function openCorrection('),html.indexOf('        async function open(id,'));
class El{constructor(tag,text){this.tag=tag;this.textContent=text;this.children=[];}append(...a){this.children.push(...a);}replaceChildren(...a){this.children=a;}setAttribute(){}scrollIntoView(){}all(tag){return this.children.flatMap(x=>[...(x.tag===tag?[x]:[]),...x.all(tag)]);}}
const detail=new El('div'),status={},posts=[];let rejectSave=false,failRefresh=false,openedOriginal=0;
const ctx=vm.createContext({markEditor:()=>{},labeledData:(label,value,cls)=>new El('p',label+value,cls),status,detail,active:null,Map,encodeURIComponent,crypto:{randomUUID:()=> 'request'},candidateApplyRequests:new Map(),hasDrafts:()=>false,base:()=> 'library',
 $:()=>({}),node:(...a)=>new El(...a),button:(t,f)=>Object.assign(new El('button',t),{action:f}),
 field:(parent,text,value)=>{const el=new El('textarea');el.value=el.defaultValue=value;parent.append(el);return el;},
 actionError:async()=> 'Input rejected; your input is kept.',failureText:()=> 'Apply blocked.',workbenchStart:0,
 api:async(path,body)=>{if(body){posts.push(body);if(rejectSave)throw {status:400};return {Operation:{State:'Applied'}};}
 return {Item:{Name:'Current',Confirmed:true,Source:{Id:1,OriginalTitle:'Original',DisplayPrefix:''},Observation:{}},Candidates:[{Id:'saved',Text:'Saved',Revision:1,Approved:true}],SelectedCandidateId:'saved',SelectionRevision:1};},
 refreshCampaigns:async()=>{if(failRefresh)throw Error('refresh failed');},refreshWorkbench:async()=>{},refreshSavedLanguages:async()=>{},
 openOriginalDisplay:async()=>{openedOriginal++;},openDetail:async()=>{},failurePanel:()=>{}});
vm.runInContext(fragment,ctx);
const open=()=>vm.runInContext("openCorrection('movie','en')",ctx),input=()=>detail.all('textarea')[0],button=t=>detail.all('button').find(x=>x.textContent===t);
(async()=>{
 await open();input().value=' ';await button('Save translation').action();assert.equal(openedOriginal,1);assert.equal(posts.length,0);
 await open();input().value='Edited';rejectSave=true;await button('Save translation').action();assert.equal(input().value,'Edited');assert(detail.all('p').some(x=>x.textContent==='Input rejected; your input is kept.'));
 rejectSave=false;await button('Save translation').action();assert.equal(posts.at(-1).Language,'en');assert.equal(posts.at(-1).Apply,false);assert.equal(posts.at(-1).Text,'Edited');
 await open();failRefresh=true;await button('Save & display on this movie').action();assert(status.textContent.includes('Title saved and applied. Could not refresh'));assert.equal(posts.at(-1).Apply,true);
 console.log(JSON.stringify({passed:4,failed:0,checks:['blank title opens original confirmation without candidate write','failed save keeps input and shows local error','explicit language save only','refresh failure does not misreport successful apply']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
