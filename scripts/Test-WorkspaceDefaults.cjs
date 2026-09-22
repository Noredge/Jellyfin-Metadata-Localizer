const fs=require('fs'),vm=require('vm'),assert=require('assert');
const s=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const fragment=s.slice(s.indexOf('        function workspaceDefaultsDirty()'),s.indexOf('        async function refreshWorkbench('));
const fields=new Map();let current='A',resolveRead,reads=0;const ctx=vm.createContext({lib:{value:'A'},base:()=>current,$:k=>{if(!fields.has(k))fields.set(k,{});return fields.get(k);},syncCampaignControls:()=>{},api:()=>{reads++;return new Promise(r=>resolveRead=r);}});
vm.runInContext('let workspaceDefaults=null,workspaceDefaultsScope="";'+fragment,ctx);
(async()=>{
 let pending=vm.runInContext('refreshWorkspaceDefaults()',ctx);current='B';resolveRead({Revision:1,Languages:'en',Display:'en'});await pending;assert.equal(vm.runInContext('workspaceDefaults',ctx),null);
 pending=vm.runInContext('refreshWorkspaceDefaults()',ctx);resolveRead({Revision:2,Languages:'both',Display:'store'});await pending;assert.equal(fields.get('.jml-campaign-mode').value,'store');
 fields.get('.jml-campaign-mode').value='en';const before=reads;await vm.runInContext('refreshWorkspaceDefaults()',ctx);assert.equal(reads,before);assert.equal(fields.get('.jml-campaign-mode').value,'en');assert(!vm.runInContext('workspaceDefaultsDirty()',ctx));
 fields.get('.jml-default-display').value='zh-Hans';assert(vm.runInContext('workspaceDefaultsDirty()',ctx));current='C';assert(vm.runInContext('workspaceDefaultsDirty()',ctx));current='B';
 pending=vm.runInContext('refreshWorkspaceDefaults(true)',ctx);resolveRead({Revision:3,Languages:'both',Display:'en'});await pending;assert(!vm.runInContext('workspaceDefaultsDirty()',ctx));assert.equal(fields.get('.jml-default-display').value,'en');
 console.log(JSON.stringify({passed:5,failed:0,checks:['stale library response ignored','server defaults initialize task options','routine refresh preserves temporary task override without saving','default form changes are dirty','explicit reload restores persisted values']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
