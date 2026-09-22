const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
new vm.Script(html.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1]);
const profiles=[{Id:'openai',Kind:'openai',Model:'model-a',Endpoint:'https://api.openai.com/v1'},{Id:'groq',Kind:'groq',Model:'configure-model',IsConfigured:false,Endpoint:'https://api.groq.com/openai/v1'},{Id:'local',Kind:'lmstudio',Model:'test-local',Endpoint:'http://127.0.0.1:1234/api/v1'}].map(profile=>({...profile,MaxOutputTokens:1024}));
const inputs=new Map(profiles.map(p=>[p.Id,{model:{value:p.Model},tokens:{value:'1024'},endpoint:{value:p.Endpoint}}]));
const status={},opened=[],requests=[];
const ctx=vm.createContext({serviceSettings:{DefaultServiceId:'groq',Profiles:profiles},serviceInputs:inputs,serviceDraftProfiles:null,URL,status,openServiceSettings:id=>opened.push(id),workbenchReasons:{model_not_configured:'Configure the model first.'}});
function load(start,end){vm.runInContext(html.slice(html.indexOf(start),html.indexOf(end,html.indexOf(start))),ctx);}
load('        function serviceConfigured(', '        function serviceDirty(');
load('        function serviceCredentialPresent(', '        async function refreshServices(');
const checks=[];function check(code,expected){assert.equal(vm.runInContext(code,ctx),expected);checks.push(code);}
check("serviceLabel('openai')",'OpenAI · model-a');
check("serviceLabel('openai','frozen-model')",'OpenAI · frozen-model');
check("serviceLabel('local')",'LM Studio · test-local');
check("serviceLabel('unknown')",'unknown');
check("serviceLabel('groq')",'Groq · Configure a model');
check("serviceLabel('groq','previous-model')",'Groq · previous-model');
check("serviceConfigured(serviceSettings.Profiles[0])",true);
check("serviceConfigured(serviceSettings.Profiles[1])",false);
check("serviceConfigured({Model:' '})",false);
check("serviceConfigured({Model:'arbitrary-model',IsConfigured:false})",false);
check("requireModel(serviceSettings.Profiles[1])",false);
assert.deepEqual(opened,['groq']);assert.equal(status.textContent,'Configure the model first.');
check("requireModel(serviceSettings.Profiles[0])",true);assert.equal(opened.length,1);
inputs.get('groq').model.value='';
check("profileDraft('groq').Model",'configure-model'); // Empty model still allows connection/list-model testing.
inputs.get('groq').model.value='user-selected-model';
check("profileDraft('groq').Model",'user-selected-model');
inputs.get('local').endpoint.value='https://outside.example/api/v1';
assert.throws(()=>ctx.profileDraft('local'),/invalid_local_endpoint/);checks.push('local endpoint remains loopback-only');
Object.assign(ctx,{lib:{value:'Movies'},lang:{value:'en'},hasDrafts:()=>false,base:()=> 'libraries/Movies',$:()=>({value:'store'}),selectedService:()=>profiles[1],api:async(...args)=>requests.push(args),workbenchSelection:new Map([['movie',{Status:'new'}]])});
load('        async function startCampaign()', '        function detailDirty()');
load('        async function startSelectedTranslation()', '        async function reviewSelectedAndPreview()');
(async()=>{
 await ctx.startCampaign();assert.equal(requests.length,0);assert.equal(opened.at(-1),'groq');checks.push('Home task routes unconfigured service to Settings without submitting');
 await ctx.startSelectedTranslation();assert.equal(requests.length,0);assert.equal(opened.at(-1),'groq');checks.push('Selected-title task checks its default service before preparing batches');
 // File presence, saved model and model-list connectivity are independent states.
 class El {
  constructor(tag,text='',className=''){this.tag=tag;this.textContent=text;this.className=className;this.children=[];this.events={};this.attributes={};}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;}
  setAttribute(key,value){this.attributes[key]=value;}
  addEventListener(key,handler){this.events[key]=handler;}
  all(tag){return this.children.flatMap(child=>[...(child.tag===tag?[child]:[]),...child.all(tag)]);}
 }
 const elements=new Map(),serviceRequests=[],node=(...args)=>new El(...args),$=selector=>{
  if(!elements.has(selector))elements.set(selector,node('div'));return elements.get(selector);
 };
 const settings={DefaultServiceId:'groq',Profiles:profiles,OpenAiCredentialFilePresent:true,CredentialFilePresent:false};
 Object.assign(ctx,{$,node,serviceOverride:false,syncCampaignControls:()=>{},
  field:(parent,label,value)=>{const input=node('input');input.value=value;input.defaultValue=value;parent.append(input);return input;},
  button:(text,action)=>Object.assign(node('button',text),{action}),
  checkbox:(parent,text)=>{const input=node('input');input.type='checkbox';parent.append(input);return input;},
  api:async(path,body)=>{serviceRequests.push({path,body});return path==='services'?settings:{Models:['synthetic-model-1',false,'synthetic-model-2']};}});
 load('        async function refreshServices()', '        async function saveServices()');
 await ctx.refreshServices();
 assert.equal($('.jml-credential').textContent,'OpenAI: Key file found；Groq: Key file not found');
 const cards=$('.jml-service-grid').children.filter(element=>element.tag==='section');
 assert(cards[1].all('p').some(element=>element.textContent.startsWith('Choose a model in Settings')));
 assert.equal(cards.flatMap(card=>card.all('p')).filter(element=>element.textContent==='Connection not tested in this view.').length,3);
 assert.deepEqual(serviceRequests.map(request=>request.path),['services']);
 checks.push('File presence does not imply a configured model or tested connection; page load only reads local settings');
 await cards[0].all('button').find(element=>element.textContent==='Test connection / list models').action();
 assert(cards[0].all('p').some(element=>element.textContent==='Model list received: 2. Translation and selected-model access have not been tested.'));
 assert.deepEqual(serviceRequests.map(request=>request.path),['services','services/test']);
 assert.deepEqual(Object.keys(serviceRequests[1].body),['Profile']);
 const modelSelect=cards[0].all('select')[0];modelSelect.value='synthetic-model-2';modelSelect.events.change();
 assert.equal(ctx.serviceInputs.get('openai').model.value,'synthetic-model-2');
 assert.equal(settings.Profiles[0].Model,'model-a');
 assert.equal($('.jml-service-status').textContent,'Unsaved service changes. Save to use them for new tasks.');
 settings.OpenAiCredentialFilePresent=false;settings.CredentialFilePresent=true;await ctx.refreshServices();
 assert.equal($('.jml-credential').textContent,'OpenAI: Key file not found；Groq: Key file found');
 checks.push('Explicit connection test lists models without translation, distinguishes selected-model access and leaves choices unsaved');
 console.log(JSON.stringify({passed:checks.length,failed:0,checks}));
})().catch(error=>{console.error(error);process.exitCode=1;});
