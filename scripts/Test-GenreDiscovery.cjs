const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const source=html.slice(html.indexOf('        async function refreshGenreDiscovery('),html.indexOf('        async function refreshCatalog('));
const checks=[];
function harness(){
  const timers=new Map(),areas=new Map(),calls=[];let seq=0;
  const element=()=>({textContent:'',children:[],classList:{toggle(){}},append(...x){this.children.push(...x);},replaceChildren(...x){this.children=x;}});
  const context=vm.createContext({document:{hidden:false},console,
    setTimeout(fn,ms){let id=++seq;timers.set(id,{fn,ms});return id;},clearTimeout(id){timers.delete(id);},
    $(selector){if(!areas.has(selector))areas.set(selector,element());return areas.get(selector);},
    node(tag,text){return Object.assign(element(),{tag,text});},button(text,action){return {text,action};},openDetail(){},
    api:async route=>{calls.push(route);return {UnknownTermCount:1,AffectedItemCount:2,MissingChineseCount:0,MissingEnglishCount:1,UnconfirmedItemCount:0,Items:[{Original:'<new & genre>',ItemCount:2,MissingLanguages:['en'],ExampleItemId:'item'}],Start:0};},
  });
  vm.runInContext(`let lib={value:'a'},campaignVisible=true,genreDiscoveryEpoch=0,genreDiscoveryTimer=null,busy=false,queue=null;function base(){return lib.value;} ${source}`,context);
  const run=code=>vm.runInContext(code,context);
  const tick=async()=>{const [id,t]=timers.entries().next().value;timers.delete(id);await t.fn();await new Promise(resolve=>setImmediate(resolve));};
  return {run,tick,timers,areas,calls,context};
}
(async()=>{
  const h=harness();await h.run('refreshGenreDiscovery()');
  assert(h.areas.get('.jml-genre-discovery-status').textContent.includes('1 terms across 2 movies'));
  assert(h.areas.get('.jml-genre-discovery-items').children[0].children[0].text.startsWith('<new & genre>'));
  checks.push('missing languages and impact render as text');
  assert.equal(h.timers.size,1);assert.equal([...h.timers.values()][0].ms,60000);await h.tick();assert.equal(h.calls.length,2);
  checks.push('visible page automatically rechecks after one minute');
  h.context.document.hidden=true;await h.tick();assert.equal(h.calls.length,2);assert.equal(h.timers.size,1);
  checks.push('hidden document defers requests');
  h.context.document.hidden=false;h.run('busy=true');await h.tick();assert.equal(h.calls.length,2);
  h.run('busy=false;campaignVisible=false');await h.tick();assert.equal(h.calls.length,2);assert.equal(h.timers.size,0);
  checks.push('busy work defers polling and leaving stops it');
  const old=harness();let release;old.context.api=()=>new Promise(r=>release=r);const pending=old.run('refreshGenreDiscovery()');old.run("lib.value='other';genreDiscoveryEpoch++");
  release({UnknownTermCount:99,Items:[]});await pending;assert.equal(old.areas.size,0);assert.equal(old.timers.size,0);
  checks.push('stale response cannot populate a different library or restart polling');
  const report={passed:checks.length,failed:0,checks};console.log(JSON.stringify(report));
})().catch(error=>{console.error(error);process.exitCode=1;});
