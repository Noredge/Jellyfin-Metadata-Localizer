const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const source=html.slice(html.indexOf('        async function refreshOperationView('),html.indexOf('        function selectionCount()'));
const calls=[],status={};let failList=false,failDetail=false;
const ctx=vm.createContext({lang:{value:"zh-Hans"},status,workbenchStart:50,operationText:r=>r.State+' '+r.Id,
 refreshWorkbench:async start=>{calls.push(['list',start]);if(failList)throw Error('offline');},
 open:async id=>{calls.push(['detail',id]);if(failDetail)throw Error('offline');}});
vm.runInContext(source,ctx);
(async()=>{
 const run=()=>vm.runInContext("refreshOperationView('movie',{State:'Applied',Id:'receipt'})",ctx);
 await run();assert.deepEqual(calls,[['list',50],['detail','movie']]);assert.equal(status.textContent,'Applied receipt');
 calls.length=0;failList=true;await run();assert.deepEqual(calls,[['list',50]]);assert(status.textContent.startsWith('Applied receipt'));assert(status.textContent.includes('Could not refresh'));
 calls.length=0;failList=false;failDetail=true;await run();assert.deepEqual(calls,[['list',50],['detail','movie']]);assert(status.textContent.startsWith('Applied receipt'));assert(status.textContent.includes('Refresh before'));
 console.log(JSON.stringify({passed:3,failed:0,checks:['refresh list and detail after operation','list reload failure preserves durable result','detail reload failure preserves durable result']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
