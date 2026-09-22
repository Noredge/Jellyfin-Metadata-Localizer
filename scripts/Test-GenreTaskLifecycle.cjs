const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const code=html.slice(html.indexOf('        let genreTaskDrafts'),html.indexOf("        $('.jml-genre-prepare').addEventListener"));
const storage=new Map();let task,failSecond=false,editCalls=0,writes=0,passed=0;
function reset(){storage.clear();editCalls=0;writes=0;failSecond=false;task={Id:'task',Revision:1,Model:'synthetic',State:'Completed',Items:[0,1].map(i=>({Source:{Original:'Synthetic',Language:i===0?'SimplifiedChinese':'English',ExpectedMapping:null},Text:'Saved '+i,State:'Generated'}))};}
function runtime(mapping={Matches:false,CanSave:true}){
 const areas=new Map();
 function node(tag,text){return {tag,text,children:[],dataset:{},listeners:{},append(...xs){this.children.push(...xs);},replaceChildren(){this.children=[];},addEventListener(event,fn){this.listeners[event]=fn;}};}
 const $=key=>{if(!areas.has(key))areas.set(key,node('div'));return areas.get(key);};
 const ctx=vm.createContext({$,node,base:()=> 'libraries/Movies',sessionStorage:{getItem:k=>storage.get(k)||null,setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},
  button:(text,action)=>({...node('button',text),action}),
  field:(parent,text,value)=>{const input=node('input',text);input.value=input.defaultValue=value;parent.append(input);return input;},
  checkbox:(parent,text)=>{const input=node('checkbox',text);parent.append(input);return input;},
  async api(route,body){if(body){writes++;assert(route.endsWith('/edit'));editCalls++;if(failSecond&&editCalls===2)throw Error('synthetic connection failure');assert.equal(body.Revision,task.Revision);const i=body.Language==='zh-Hans'?0:1;task.Items[i].Text=body.Text;task.Revision++;}return body?structuredClone(task):{Task:structuredClone(task),Running:false,Mappings:task.Items.map((_,Index)=>({Index,...mapping}))};}});
 vm.runInContext(code,ctx);
 const all=()=>{const out=[];function walk(n){out.push(n);n.children.forEach(walk);}areas.forEach(walk);return out;};
 return {open:()=>vm.runInContext("openGenreTask('libraries/Movies','task')",ctx),all,inputs:()=>all().filter(n=>n.tag==='input'),click:label=>all().find(n=>n.tag==='button'&&n.text===label).action(),type(i,value){const n=this.inputs()[i];n.value=value;n.listeners.input();}};
}
async function check(name,action){reset();await action();passed++;console.log('PASS '+name);}
(async()=>{
 await check('fresh runtime restores input without requests that write',async()=>{let a=runtime();await a.open();a.type(1,'Draft English');a=runtime();await a.open();assert.equal(a.inputs()[1].value,'Draft English');assert.equal(writes,0);await assert.rejects(a.open(),/Save or discard/);});
 await check('discard restores saved candidate and removes session draft',async()=>{let a=runtime();await a.open();a.type(0,'Draft');a=runtime();await a.open();await a.click('Discard candidate edits');assert.equal(a.inputs()[0].value,'Saved 0');assert.equal(storage.size,0);});
 await check('revision mismatch preserves copyable text and prevents editing',async()=>{let a=runtime();await a.open();a.type(1,'Old draft');task.Revision++;a=runtime();await a.open();assert.equal(a.inputs().length,0);assert(a.all().some(n=>n.tag==='pre'&&n.text.includes('Old draft')));await a.click('Discard outdated browser draft');assert.equal(a.inputs().length,2);assert.equal(storage.size,0);assert.equal(writes,0);});
 await check('partial edit failure resumes remaining draft at new revision',async()=>{let a=runtime();await a.open();a.type(0,'Chinese edit');a.type(1,'English edit');failSecond=true;await assert.rejects(a.click('Save candidate edits'),/synthetic connection/);a=runtime();await a.open();assert.equal(a.inputs()[0].value,'Chinese edit');assert.equal(a.inputs()[1].value,'English edit');failSecond=false;await a.click('Save candidate edits');assert.equal(editCalls,3);assert.equal(task.Revision,3);assert.equal(storage.size,0);});
 await check('matching shared mappings cannot be confirmed again',async()=>{const a=runtime({Matches:true,CanSave:false});await a.open();const checks=a.all().filter(n=>n.tag==='checkbox');assert.equal(checks.length,2);assert(checks.every(n=>n.disabled&&n.dataset.genreDisabled==='true'));});
 await check('changed shared mappings show conflict and block confirmation',async()=>{const a=runtime({Matches:false,CanSave:false});await a.open();assert(a.all().some(n=>n.text?.startsWith('Dictionary changed')));assert(a.all().filter(n=>n.tag==='checkbox').every(n=>n.disabled));});
 console.log(JSON.stringify({passed,failed:0,scope:'UI logic with fresh JavaScript runtimes and shared session storage; not browser navigation verification'}));
})().catch(e=>{console.error(e);process.exitCode=1;});
