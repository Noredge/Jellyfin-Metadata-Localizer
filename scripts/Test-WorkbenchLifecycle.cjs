const fs = require('fs'), vm = require('vm'), assert = require('assert');
const html = fs.readFileSync('src/Localizer.Plugin/Web/admin.html', 'utf8');
const inline = html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(inline);
const show = inline.slice(inline.indexOf('        function showPage()'), inline.indexOf("        page.addEventListener('viewshow', showPage);"));
const leave = inline.slice(inline.indexOf('        function stopQueuedGroupsOnLeave()'), inline.indexOf("        window.addEventListener('beforeunload'"));
const schedule = inline.slice(inline.indexOf('        function scheduleCampaignRefresh('), inline.indexOf('        async function refreshCampaigns()'));
const cases = [];
function make() {
  const timers = new Map(), pageEvents = {}, windowEvents = {}, calls = [], areas = new Map(); let seq = 0;
  const ctx = vm.createContext({ URLSearchParams, console, timers, calls, document:{body:{style:{}}},
    setTimeout(fn, delay) { const id = ++seq; timers.set(id,{fn,delay}); return id; }, clearTimeout(id) { timers.delete(id); },
    page: {addEventListener(name, fn) { pageEvents[name] = fn; }},
    window: {location:{hash:'#/configurationpage?name=metadata-localizer'},addEventListener(name,fn){windowEvents[name]=fn;}},
    $(selector){ if (!areas.has(selector)) areas.set(selector,{}); return areas.get(selector); },
    node(tag,text){return {tag,text};}, status:{}, lib:{value:'', append(){calls.push('option');}}, lang:{value:'zh-Hans'},
  });
  vm.runInContext(`let campaignVisible=true,campaignEpoch=0,campaignTimer=null,campaignUpdating=false,genreDiscoveryTimer=null,genreDiscoveryEpoch=0;
    let detectionTimer=null,detectionEpoch=0,detectionVersion=null;
    async function refreshDetectedChanges(){calls.push('detection');}
    async function refreshGenreDiscovery(){calls.push('discovery');}
    let pageShowTimer=null,pageShownEpoch=-1,busy=false,queue=null,initialized=false,previousLibrary='',workbenchStart=50,drafts=false;
    let campaignList=[{State:'Running'}];
    const workbenchDrafts=new Map([['item',{Text:'kept',Revision:2}]]), detail={text:'unsaved detail'};
    function base(){return 'libraries/'+lib.value;}
    function campaignUnfinished(job){return job.State==='Running';}
    function hasDrafts(){return drafts;}
    function serviceDirty(){return false;} function peopleDirty(){return false;}
    async function refreshServices(){calls.push('services');} async function refreshPeople(){calls.push('people');}
    let peopleOffset=0,peopleLoaded=false;
    async function api(path){calls.push(path);return path==='libraries'?[{Id:'lib',Name:'Synthetic'}]:{CredentialFilePresent:true};}
    async function run(action){if(busy||queue)return;busy=true;try{await action();}finally{busy=false;}}
    function clearWorkbench(){campaignEpoch++;calls.push('clear');}
    async function refreshWorkbench(start){calls.push('workbench:'+start);await refreshSavedLanguages();}
    async function refreshSavedLanguages(){calls.push('saved');}
    async function refreshTasks(){calls.push('tasks');}
    async function refreshBatches(){calls.push('batches');}
    async function refreshCampaigns(){calls.push('campaign');scheduleCampaignRefresh(campaignEpoch,base(),lang.value);}
    ${schedule}\n${leave}\n${show}
    page.addEventListener('viewshow',showPage);page.addEventListener('pageshow',showPage);
  `,ctx);
  async function tick(delay){const entry=[...timers.entries()].find(([,t])=>t.delay===delay);assert(entry,'missing timer '+delay);timers.delete(entry[0]);await entry[1].fn();}
  return {ctx,timers,calls,pageEvents,windowEvents,tick,set:s=>vm.runInContext(s,ctx),get:s=>vm.runInContext(s,ctx)};
}
(async()=>{
 let h=make();h.pageEvents.viewshow();h.pageEvents.pageshow();await h.tick(0);assert.equal(h.calls.filter(x=>x==='libraries').length,1);assert.equal(h.calls.filter(x=>x==='credential-status').length,1);cases.push('initial viewshow + pageshow initializes once');
 h.calls.length=0;h.pageEvents.viewhide();assert.equal(h.timers.size,0);h.pageEvents.viewshow();await h.tick(0);assert(h.calls.includes('campaign'));assert(h.calls.includes('workbench:50'));assert(h.calls.includes('saved'));assert(!h.calls.includes('libraries'));assert([...h.timers.values()].some(t=>t.delay===3000));await h.tick(3000);assert.equal(h.calls.filter(x=>x==='campaign').length,2);cases.push('restored viewshow refreshes campaign/workbench/saved and resumes polling');
 h.calls.length=0;h.pageEvents.viewhide();h.set('drafts=true');h.pageEvents.viewshow();await h.tick(0);assert(h.calls.includes('saved'));assert(!h.calls.some(x=>x.startsWith('workbench:')));assert.equal(h.get("workbenchDrafts.get('item').Text"),'kept');assert.equal(h.get('detail.text'),'unsaved detail');cases.push('return preserves table and detail drafts while refreshing counts');
 h.pageEvents.viewhide();h.set('busy=true');h.pageEvents.viewshow();await h.tick(0);assert([...h.timers.values()].some(t=>t.delay===100));h.set('busy=false');await h.tick(100);assert(h.get('campaignVisible'));cases.push('return during busy action defers refresh rather than dropping it');
 const before=h.get('campaignTimer');h.set("scheduleCampaignRefresh(campaignEpoch-1,base(),lang.value)");assert(h.timers.has(before));cases.push('stale epoch cannot clear resumed campaign timer');
 h.windowEvents.hashchange();assert(h.get('campaignVisible'));h.ctx.window.location.hash='#/dashboard';h.windowEvents.hashchange();assert(!h.get('campaignVisible'));assert.equal(h.timers.size,0);cases.push('hash navigation hides only after leaving plugin');
 h=make();h.set('busy=true');h.pageEvents.viewshow();await h.tick(0);h.pageEvents.viewhide();assert.equal(h.timers.size,0);h.set('busy=false');h.pageEvents.viewshow();await h.tick(0);assert.equal(h.calls.filter(x=>x==='libraries').length,1);cases.push('leave cancels deferred entry and next visit initializes once');
 const report={passed:cases.length,failed:0,cases};console.log(JSON.stringify(report,null,2));
})().catch(error=>{console.error(error);process.exitCode=1;});
