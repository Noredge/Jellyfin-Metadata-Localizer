const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
class Element {
  constructor(tag,text='',className=''){Object.assign(this,{tag,textContent:text,className,children:[],events:{},attributes:{},value:'',defaultValue:'',checked:false,defaultChecked:false});}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;}
  setAttribute(key,value){this.attributes[key]=value;}
  addEventListener(key,handler){this.events[key]=handler;}
  all(tag){return this.children.flatMap(child=>[...(child.tag===tag?[child]:[]),...child.all(tag)]);}
}
const elements=new Map(),node=(...args)=>new Element(...args),$=selector=>{
  if(!elements.has(selector))elements.set(selector,node('div'));return elements.get(selector);
};
const preset=(Id,Kind,Endpoint)=>({Id,Kind,Endpoint,Model:'synthetic-model',MaxOutputTokens:1024,IsConfigured:true});
let settings={Revision:4,DefaultServiceId:'openai',OpenAiCredentialFilePresent:true,CredentialFilePresent:false,CredentialFiles:{CUSTOM:true},Profiles:[
  preset('openai','openai','https://api.openai.com/v1'),preset('groq','groq','https://api.groq.com/openai/v1'),preset('local','lmstudio','http://127.0.0.1:1234/api/v1'),
  {...preset('custom','openai-compatible','https://service.example/v1'),DisplayName:'Studio cloud',UseJsonResponseFormat:false}
]};
const requests=[],checks=[];
const ctx=vm.createContext({URL,Map,Set,JSON,Number,Error,serviceSettings:null,serviceDraftProfiles:null,serviceInputs:new Map(),serviceOverride:false,
  $,node,status:{},peopleEditor:null,syncCampaignControls(){},
  field:(parent,label,value)=>{const input=node('input');input.label=label;input.value=input.defaultValue=value;parent.append(input);return input;},
  checkbox:(parent,label)=>{const input=node('input');input.label=label;input.type='checkbox';parent.append(input);return input;},
  button:(text,action)=>Object.assign(node('button',text),{action}),
  api:async(path,body)=>{requests.push({path,body:body&&JSON.parse(JSON.stringify(body))});
    if(path==='services'&&body){settings={...settings,...structuredClone(body),Revision:settings.Revision+1};return {};}
    if(path==='services')return structuredClone(settings);
    if(path==='services/test')return {Models:['synthetic-listed-model']};throw Error('Unexpected API route');
  }
});
vm.runInContext(html.slice(html.indexOf('        function serviceConfigured('),html.indexOf('        function openPerson(')),ctx);
const get=code=>vm.runInContext(code,ctx),record=name=>checks.push(name);
function cardFor(id){return $('.jml-service-grid').children.filter(x=>x.tag==='section')[ctx.serviceDraftProfiles.findIndex(x=>x.Id===id)];}
function testConnection(id){return cardFor(id).all('button').find(x=>x.textContent==='Test connection / list models').action();}
(async()=>{
  await ctx.refreshServices();
  assert.equal(get("serviceLabel('custom')"),'Studio cloud · synthetic-model');
  assert.equal(get("serviceLabel('custom','frozen-model')"),'Studio cloud · frozen-model');
  assert.equal(get("serviceCredentialPresent(serviceSettings.Profiles[3])"),true);
  assert.equal(ctx.serviceInputs.get('custom').endpoint.readOnly,true);
  assert.equal(ctx.serviceInputs.get('custom').id.readOnly,true);
  assert.equal(get('serviceDirty()'),false);
  assert.equal(requests.length,1);record('Loads only settings; custom labels include name/model; own key file locks endpoint and saved ID');

  ctx.serviceInputs.get('openai').model.value='unsaved-model';
  ctx.serviceInputs.get('groq').tokens.value='invalid-draft';
  ctx.serviceInputs.get('custom').json.checked=true;
  $('.jml-service-default').value='custom';
  ctx.addCustomService();
  assert.equal(ctx.serviceInputs.get('openai').model.value,'unsaved-model');
  assert.equal(ctx.serviceInputs.get('groq').tokens.value,'invalid-draft');
  assert.equal(ctx.serviceInputs.get('custom').json.checked,true);
  assert.equal($('.jml-service-default').value,'custom');
  assert.equal(ctx.serviceSettings.Profiles.length,4);
  const fresh=ctx.serviceInputs.get('cloud-1');
  assert.equal(fresh.endpoint.value,'');assert.equal(fresh.model.value,'');assert.equal(fresh.json.checked,false);
  assert.equal(fresh.id.readOnly,false);assert.equal(get('serviceDirty()'),true);assert.equal(requests.length,1);
  record('Add preserves all existing drafts including invalid values and checkbox edits, and chooses no endpoint/model');

  fresh.endpoint.value='https://new.example/v1';fresh.name.value='Second cloud';fresh.id.value='new_cloud';
  ctx.addCustomService();assert(ctx.serviceInputs.has('cloud-2'));assert.equal(ctx.serviceInputs.get('cloud-1').id.value,'new_cloud');assert.equal(ctx.serviceInputs.get('cloud-1').endpoint.value,'https://new.example/v1');
  ctx.removeCustomService('cloud-2');record('Adding after editing an unsaved service ID preserves both unique draft identities and inputs');
  await testConnection('cloud-1');
  assert.equal(requests.length,1);assert(cardFor('cloud-1').all('p').some(x=>x.textContent.startsWith('Save this service')));
  record('New custom connection cannot send until its service and address have been saved');

  ctx.removeCustomService('cloud-1');
  assert.equal(ctx.serviceInputs.get('groq').tokens.value,'invalid-draft');
  assert.equal(ctx.serviceInputs.get('custom').json.checked,true);
  assert.equal(requests.length,1);assert.equal(ctx.serviceSettings.Profiles.length,4);
  ctx.removeCustomService('openai');assert.equal(ctx.serviceDraftProfiles.length,4);
  record('Removing a draft preserves other edits and cannot remove built-in profiles or call an API');

  await ctx.refreshServices();ctx.addCustomService();
  const draft=ctx.serviceInputs.get('cloud-1');draft.endpoint.value='https://new.example/v1/';draft.id.value='my_cloud';draft.name.value='My cloud';
  const request=ctx.profileDraft('cloud-1');
  assert.deepEqual(Object.keys(request).sort(),['DisplayName','Endpoint','Id','Kind','MaxOutputTokens','Model','UseJsonResponseFormat'].sort());
  assert.equal(request.Kind,'openai-compatible');assert.equal(request.Endpoint,'https://new.example/v1');
  assert.equal(request.Model,'configure-model');assert.equal(request.UseJsonResponseFormat,false);
  assert.equal(request.DisplayName,'My cloud');assert.equal(request.Id,'my_cloud');
  record('Custom request uses the exact profile contract and has no key, credential or file-path field');

  for(const invalid of ['http://new.example/v1','https://user:password@new.example/v1','https://new.example/v1?key=example','https://new.example/v1#part']){
    draft.endpoint.value=invalid;assert.throws(()=>ctx.profileDraft('cloud-1'));
  }
  draft.endpoint.value='https://new.example/v1';draft.id.value='not a valid id';assert.throws(()=>ctx.profileDraft('cloud-1'));
  draft.id.value='my_cloud';draft.model.value='a model';assert.throws(()=>ctx.profileDraft('cloud-1'));
  draft.model.value='synthetic-user-choice';draft.tokens.value='63';assert.throws(()=>ctx.profileDraft('cloud-1'));
  draft.tokens.value='4097';assert.throws(()=>ctx.profileDraft('cloud-1'));draft.tokens.value='2048';
  ctx.serviceInputs.get('custom').endpoint.value='https://elsewhere.example/v1';assert.throws(()=>ctx.profileDraft('custom'),/service_endpoint_bound_to_credential/);
  ctx.serviceInputs.get('custom').endpoint.value='https://service.example/v1';
  record('Rejects insecure/credential-bearing URLs, invalid IDs/models/token limits and tampering with locked addresses');

  draft.id.value='OPENAI';let before=requests.length;await ctx.saveServices();assert.equal(requests.length,before);
  draft.id.value='my_cloud';draft.json.checked=true;$('.jml-service-default').value='cloud-1';
  await ctx.saveServices();
  const saved=requests.findLast(x=>x.path==='services'&&x.body).body;
  assert.equal(saved.DefaultServiceId,'my_cloud');
  const added=saved.Profiles.find(x=>x.Id==='my_cloud');
  assert.equal(added.Model,'synthetic-user-choice');assert.equal(added.UseJsonResponseFormat,true);
  assert.equal(get('serviceDirty()'),false);assert.equal(ctx.serviceInputs.get('my_cloud').id.readOnly,true);
  record('Case-insensitive duplicate IDs block save; explicit save includes requested JSON mode and resolves a renamed draft default ID');

  before=requests.length;await testConnection('my_cloud');assert.equal(requests.length,before+1);
  assert.equal(requests.at(-1).path,'services/test');assert.deepEqual(Object.keys(requests.at(-1).body),['Profile']);
  assert.equal(ctx.serviceInputs.get('my_cloud').model.value,'synthetic-user-choice');
  assert.equal(settings.Profiles.find(x=>x.Id==='my_cloud').Model,'synthetic-user-choice');
  record('Saved service has an explicit model-list action without selecting a returned model or sending media/credentials');

  ctx.serviceInputs.get('openai').model.value='preserve-on-remove';before=requests.length;
  ctx.removeCustomService('my_cloud');
  assert.equal($('.jml-service-default').value,'');assert.equal(ctx.serviceInputs.get('openai').model.value,'preserve-on-remove');
  await ctx.saveServices();assert.equal(requests.length,before);
  assert(settings.Profiles.some(x=>x.Id==='my_cloud'));assert.equal(settings.CredentialFiles.CUSTOM,true);
  $('.jml-service-default').value='openai';await ctx.saveServices();
  assert(!settings.Profiles.some(x=>x.Id==='my_cloud'));assert.equal(settings.CredentialFiles.CUSTOM,true);
  assert(requests.every(x=>x.path==='services'||x.path==='services/test'));
  record('Removing the default requires an explicit replacement and save; no key deletion or existing-task API is called');

  const dictionary=JSON.parse(fs.readFileSync('src/Localizer.Plugin/Web/menu.zh-Hans.json','utf8'));
  for(const text of ['Add OpenAI-compatible service','Remove service','HTTPS base address','Display name (optional)','Request JSON object response format','Title output token limit'])assert(dictionary[text]&&dictionary[text]!==text);
  assert(html.includes('Full overviews use a separate output limit of 3200 tokens; genre dictionary translation uses 4000 tokens.'));
  assert(!/type=["']password["']/.test(html));record('Custom service controls have Chinese labels and no plaintext-key field');
  console.log(JSON.stringify({passed:checks.length,failed:0,checks}));
})().catch(error=>{console.error(error);process.exitCode=1;});
