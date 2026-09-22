const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const source=html.slice(html.indexOf('        async function refreshDetectedChanges('),html.indexOf('        async function refreshNfoGenres('));
function harness(){
 const timers=new Map(),areas=new Map(),calls=[];let seq=0;
 const element=()=>({textContent:'',children:[],replaces:0,append(...x){this.children.push(...x);},replaceChildren(...x){this.children=x;this.replaces++;}});
 const sample={Enabled:true,Version:'same',CheckedUtc:'2026-09-07T00:00:00Z',Summary:{Movies:4,NewItems:1,ChangedSources:1,DisplayConflicts:0,MissingChinese:2,MissingEnglish:2,NfoReady:0,NfoChanged:1,NfoBlocked:0,UnknownTerms:1},Items:[{ItemId:'a',Name:'<img synthetic>',Reasons:['new','missing_en']}],Start:0};
 const ctx=vm.createContext({document:{hidden:false},setTimeout(fn,ms){const id=++seq;timers.set(id,{fn,ms});return id;},clearTimeout(id){timers.delete(id);},
 $(s){if(!areas.has(s))areas.set(s,element());return areas.get(s);},node(tag,text){return {...element(),tag,text};},button(text,action){return {text,action};},openDetail(){},api:async(path,body)=>{calls.push({path,body});return sample;}});
 vm.runInContext(`let lib={value:'a'},campaignVisible=true,detectionEpoch=0,detectionTimer=null,detectionVersion=null,busy=false,queue=null;function base(){return lib.value;} ${source}`,ctx);
 return {ctx,areas,timers,calls,sample,run:s=>vm.runInContext(s,ctx),async tick(){const [id,t]=timers.entries().next().value;timers.delete(id);await t.fn();await new Promise(r=>setImmediate(r));}};
}
(async()=>{
 const checks=[],h=harness();await h.run('refreshDetectedChanges(0,true)');
 assert.deepEqual(JSON.parse(JSON.stringify(h.calls[0].body)),{Enabled:true,OnlyIfNew:true});assert(h.areas.get('.jml-detection-status').textContent.includes('Missing English titles: 2'));
 assert(h.areas.get('.jml-detection-items').children[0].children[0].text.startsWith('<img synthetic>'));checks.push('subscription respects existing choice and titles render as text');
 const replaces=h.areas.get('.jml-detection-items').replaces;await h.tick();assert.equal(h.areas.get('.jml-detection-items').replaces,replaces);checks.push('unchanged version does not rebuild result controls');
 h.ctx.document.hidden=true;const count=h.calls.length;await h.tick();assert.equal(h.calls.length,count);h.ctx.document.hidden=false;h.run('busy=true');await h.tick();assert.equal(h.calls.length,count);checks.push('hidden and busy page defer polling');
 h.run('busy=false;campaignVisible=false');await h.tick();assert.equal(h.timers.size,0);checks.push('leaving stops UI polling');
 const stale=harness();let release;stale.ctx.api=()=>new Promise(r=>release=r);const pending=stale.run('refreshDetectedChanges()');stale.run("lib.value='other';detectionEpoch++");release(stale.sample);await pending;assert.equal(stale.areas.size,0);assert.equal(stale.timers.size,0);checks.push('old library response cannot replace new scope');
 const off=harness();off.sample.Enabled=false;await off.run('refreshDetectedChanges()');assert.equal(off.timers.size,0);assert.equal(off.areas.get('.jml-detection-enabled').checked,false);checks.push('disabled watch stays disabled without polling');
 console.log(JSON.stringify({passed:checks.length,checks}));
})().catch(e=>{console.error(e);process.exitCode=1;});
