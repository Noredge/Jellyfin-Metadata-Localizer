const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const code=html.slice(html.indexOf('        function renderDraftStatus()'),html.indexOf('        function selectionChanged()'));
const areas=new Map(),drafts=new Map(),selection=new Map(),calls=[];
const $=s=>{if(!areas.has(s))areas.set(s,{children:[],replaceChildren(){this.children=[];},append(...xs){this.children.push(...xs);},querySelector(){return {focus(){calls.push('focus');}};}});return areas.get(s);};
const ctx=vm.createContext({$,workbenchRows:[],workbenchDrafts:drafts,workbenchSelection:selection,queue:null,node:(tag,text)=>({tag,text}),button:(text,action)=>({text,action}),selectionChanged(){calls.push('selection');},async refreshWorkbench(){calls.push('refresh');}});
vm.runInContext(code,ctx);const render=()=>vm.runInContext('renderDraftStatus()',ctx);
(async()=>{
 render();assert($('.jml-workbench-count').hidden);assert($('.jml-workbench-drafts').hidden);
 drafts.set('item-id',{Name:'Synthetic title',Text:'unsaved'});render();assert(!$('.jml-workbench-count').hidden);const link=$('.jml-workbench-drafts').children[1];assert(link.text.startsWith('Recover draft outside the filter'));
 selection.set('other',{});await link.action();assert.equal($('.jml-workbench-query').value,'item-id');assert.equal($('.jml-workbench-filter').value,'all');assert.equal(selection.size,0);assert.equal(drafts.get('item-id').Text,'unsaved');assert.deepEqual(calls,['selection','refresh','focus']);
 ctx.workbenchRows=[{Id:'item-id'}];render();assert($('.jml-workbench-drafts').children[1].text.startsWith('Go to draft'));
 drafts.clear();render();assert($('.jml-workbench-drafts').hidden);
 console.log(JSON.stringify({passed:5,failed:0,checks:['empty counts hidden','hidden draft recovery visible','locate preserves draft and resets scope','visible draft label','discard clears recovery entry']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
