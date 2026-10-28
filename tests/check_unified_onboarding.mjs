import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const html=fs.readFileSync(path.join(repo,'src/Localizer.Plugin/Web/unified.html'),'utf8');
const checks=[];
const check=(name,fn)=>{fn();checks.push({name,passed:true});};
function fixture(){
  const elements=Object.fromEntries(['luEndpoint','luKey','luModel','luListModels','luSave','luLanguage','luTitle','luSynopsis','luGenres','luAutomatic','luTimeout','luRpm','luBudget','luOverviewBudget','luTokenParameter','luJsonMode'].map(id=>['#'+id,{value:'',checked:true,disabled:false,innerHTML:'original options'}]));
  Object.assign(elements['#luEndpoint'],{value:'https://draft.example.org/v1'});elements['#luKey'].value='DRAFT_KEY';elements['#luLanguage'].value='zh-Hans';elements['#luTimeout'].value='60';elements['#luRpm'].value='30';elements['#luBudget'].value='3200';elements['#luOverviewBudget'].value='4096';elements['#luTokenParameter'].value='max_tokens';
  const calls=[],notices=[];let resolveCatalog,failCatalog=false,reloads=0;
  const context=vm.createContext({$:id=>elements[id],$$:()=>[{dataset:{managed:'library'}}],dirty:true,connectionDirty:true,connection:{revision:0,parametersJson:'{}'},state:{settings:{revision:7,language:'zh-Hans'}},settingsDirty:()=>{context.dirty=true;},status:m=>notices.push(m),escape:v=>String(v).replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;'),loadSettings:async()=>{reloads++;},
    api:async (route,body)=>{calls.push({route,body});if(route==='connection/models'){if(failCatalog)throw new Error('operation_unavailable');return await new Promise(resolve=>{resolveCatalog=resolve;});}if(route==='state')return {settings:{revision:8,language:'zh-Hans'}};if(route==='connection'&&!body)return {revision:1,parametersJson:'{}'};return {};}});
  const catalogStart=html.indexOf('function showModels('),catalogEnd=html.indexOf("$('#luListModels').onclick",catalogStart);
  const saveStart=html.indexOf('function settingsChange('),saveEnd=html.indexOf('async function makePreview(',saveStart);
  vm.runInContext(html.slice(catalogStart,catalogEnd)+'\n'+html.slice(saveStart,saveEnd),context);
  return {context,elements,calls,notices,resolve:models=>resolveCatalog({models}),fail:()=>{failCatalog=true;},reloads:()=>reloads};
}
let f=fixture(),pending=f.context.listModels();await Promise.resolve();f.resolve(['text-model','embedding-model','image-model','text-model']);await pending;
check('unsaved endpoint/key fetch has no model or save prerequisite',()=>{assert.equal(f.calls.length,1);assert.equal(f.calls[0].route,'connection/models');assert.equal(f.calls[0].body.endpoint,'https://draft.example.org/v1');assert.equal(f.calls[0].body.apiKey,'DRAFT_KEY');assert.equal('model' in f.calls[0].body,false);assert.equal(f.context.dirty,true);assert.equal(f.context.connectionDirty,true);assert.equal(f.elements['#luKey'].value,'DRAFT_KEY');});
check('provider models remain in returned order including non-chat types and duplicates',()=>{const text=f.elements['#luModel'].innerHTML;assert(text.indexOf('text-model')<text.indexOf('embedding-model'));assert(text.indexOf('embedding-model')<text.indexOf('image-model'));assert.equal((text.match(/value="text-model"/g)||[]).length,2);assert.equal(f.elements['#luModel'].value,'');});
f.elements['#luModel'].value='embedding-model';await f.context.saveSettings();
check('selected provider model saves without invoking optional translation test',()=>{assert.equal(f.calls.filter(c=>c.route==='connection'&&c.body)[0].body.model,'embedding-model');assert.equal(f.calls.some(c=>c.route==='connection/test'),false);assert.equal(f.calls.filter(c=>c.route==='settings').length,1);assert.equal(f.reloads(),1);});
f=fixture();pending=f.context.listModels();await Promise.resolve();f.elements['#luEndpoint'].value='https://other.example.org/v1';f.resolve(['old-provider-model']);await pending;
check('late list from an edited endpoint does not replace current model choices',()=>{assert.equal(f.elements['#luModel'].innerHTML,'original options');assert(f.notices.at(-1).includes('草稿已变化'));assert.equal(f.calls.length,1);});
f=fixture();f.fail();await assert.rejects(f.context.listModels());
check('catalog failure retains draft and re-enables lookup',()=>{assert.equal(f.elements['#luListModels'].disabled,false);assert.equal(f.elements['#luKey'].value,'DRAFT_KEY');assert.equal(f.context.dirty,true);assert.equal(f.calls.length,1);});
f=fixture();f.context.dirty=false;f.context.connectionDirty=false;f.elements['#luModel'].value='retired-model';pending=f.context.listModels();await Promise.resolve();f.resolve(['new-provider-model']);await pending;
check('missing saved model clears the selection and marks a draft without saving',()=>{assert.equal(f.elements['#luModel'].value,'');assert.equal(f.context.dirty,true);assert.equal(f.context.connectionDirty,true);assert.equal(f.calls.length,1);});
check('first-use controls follow address key list model order without a prefilled model',()=>{const settings=html.slice(html.indexOf('<section id="luSettings"'),html.indexOf('<dialog id="luEditor"'));assert(settings.indexOf('id="luEndpoint"')<settings.indexOf('id="luKey"'));assert(settings.indexOf('id="luKey"')<settings.indexOf('id="luListModels"'));assert(settings.indexOf('id="luListModels"')<settings.indexOf('id="luModel"'));assert(settings.includes('<select id="luModel"><option value="">请选择模型</option></select>'));assert(html.includes('#luModel option:not([value=""])'));});
const result={status:'passed',checks};if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
