const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const fragment=html.slice(html.indexOf('        const originalRequests ='),html.indexOf('        const overviewRequests ='));
class El{constructor(tag,text){this.tag=tag;this.textContent=text;this.children=[];}append(...a){this.children.push(...a);}replaceChildren(...a){this.children=a;}scrollIntoView(){}all(tag){return this.children.flatMap(x=>[...(x.tag===tag?[x]:[]),...x.all(tag)]);}}
const detail=new El('div'),status={},calls=[];let dirty=false,fail=false,serial=0;
const field=()=>({Preference:{Mode:'FollowLibrary',Revision:0},Matches:false,CurrentDisplay:'Translation',OriginalDisplay:'Original',SourceId:1,SourceObserved:true,SavedRevision:1});
const data={Title:field(),Overview:field(),Pending:[]};
const ctx=vm.createContext({markEditor:()=>{},labeledData:(label,value,cls)=>new El('p',label+value,cls),detail,status,active:null,Map,encodeURIComponent,Error,crypto:{randomUUID:()=>String(++serial)},base:()=> 'library',detailDirty:()=>dirty,$:()=>({}),node:(...a)=>new El(...a),button:(t,f)=>Object.assign(new El('button',t),{action:f}),api:async(path,body)=>{if(!body)return data;calls.push(body);if(fail)throw Error('lost response');return {State:'Completed',Outcome:'NotApplied'};}});
vm.runInContext(fragment,ctx);const open=()=>vm.runInContext("openOriginalDisplay('movie')",ctx),button=t=>detail.all('button').find(x=>x.textContent===t);
(async()=>{
 await open();assert(button('Display original title')&&button('Display original overview'));
 fail=true;try{await button('Display original title').action();}catch{}const first=calls.at(-1).RequestId;
 fail=false;await button('Display original title').action();assert.equal(calls.at(-1).RequestId,first);assert.equal(calls.at(-1).Field,'Name');assert.equal(calls.at(-1).ExpectedDisplay,'Translation');
 await button('Display original title').action();assert.notEqual(calls.at(-1).RequestId,first);
 data.Title.Preference.Mode='Original';await open();assert(button('Follow library for title'));assert(!button('Follow library for overview'));
 dirty=true;const count=calls.length;await open();assert.equal(calls.length,count);assert(status.textContent.includes('Save or discard'));
 console.log(JSON.stringify({passed:5,failed:0,checks:['independent original buttons','lost response reuses request ID','terminal failure permits a new request','follow choice is field specific','dirty edits prevent opening display changes']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
