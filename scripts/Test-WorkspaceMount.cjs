// Execute the real workspace entry point, including its first refresh and navigation.
// This minimal DOM supplies element operations only; JavaScript scope is not mocked.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const start=html.indexOf('        function mountWorkspace() {');
const mount=html.slice(start,html.indexOf('        // M39:',start));

function fixture(){
  const created=[],areas=new Map(),observers=[],calls=[];
  class Element{
    constructor(tag='div',text='',className=''){
      Object.assign(this,{tagName:tag.toUpperCase(),textContent:text||'',className:className||'',children:[],dataset:{},attributes:{},events:{},value:'',checked:false,hidden:false,queries:new Map()});
      this.classes=new Set(this.className.split(/\s+/).filter(Boolean));
      this.classList={add:name=>this.classes.add(name),contains:name=>this.classes.has(name),toggle:(name,on)=>on?this.classes.add(name):this.classes.delete(name)};
      created.push(this);
    }
    get firstChild(){return this.children[0];}
    get childElementCount(){return this.children.length;}
    append(...nodes){for(const node of nodes){if(node.parentElement)node.parentElement.children=node.parentElement.children.filter(x=>x!==node);node.parentElement=this;this.children.push(node);}}
    prepend(...nodes){this.append(...nodes);this.children=[...nodes,...this.children.filter(x=>!nodes.includes(x))];}
    insertBefore(node){this.append(node);}
    insertAdjacentElement(position,node){(this.parentElement||this).append(node);}
    before(...nodes){(this.parentElement||this).append(...nodes);}
    replaceChildren(...nodes){this.children=[];this.append(...nodes);}
    setAttribute(name,value){this.attributes[name]=value;}
    removeAttribute(name){delete this.attributes[name];}
    addEventListener(name,callback){(this.events[name]||=[]).push(callback);}
    querySelector(selector){if(!this.queries.has(selector)){const child=new Element(selector==='summary'?'summary':'div');this.queries.set(selector,child);this.append(child);}return this.queries.get(selector);}
    querySelectorAll(){return [];}
    closest(selector){
      if(!this.queries.has('closest:'+selector)){
        const parent=new Element(selector==='label'?'label':'div');
        if(selector==='label')parent.append(new Element('span','Synthetic label'));
        parent.append(this);this.queries.set('closest:'+selector,parent);
      }
      return this.queries.get('closest:'+selector);
    }
    focus(){this.focused=true;}
    scrollIntoView(){}
    click(){for(const handler of this.events.click||[])handler({target:this});}
  }
  const node=(...args)=>new Element(...args),$=selector=>{if(!areas.has(selector))areas.set(selector,node());return areas.get(selector);};
  const lib=node('select'),lang=node('select');lang.options=[node('option','Chinese')];
  const context=vm.createContext({Map,Set,WeakMap,Date,Array,Object,String,$,node,lib,lang,page:node(),detail:node(),status:node(),
    peopleLoaded:true,busy:false,catalogSnapshot:null,refreshCoveragePanel:null,openServiceSettings:null,showServiceSettings:null,
    savedLanguageStates:new Map(),campaignList:[],campaignCurrent:null,campaignStates:{},workspaceDefaultsScope:'libraries/',
    uiTextSources:new WeakMap(),workbenchReasons:{model_not_configured:'Configure a model first.'},
    base:()=> 'libraries/',menuText:text=>text,campaignUnfinished:()=>false,selectedService:()=>null,serviceConfigured:()=>false,
    serviceCredentialPresent:()=>false,serviceLabel:()=> 'Synthetic service',generationLanguages:()=>['zh-Hans'],generationDisplay:()=>null,
    button:(text,action)=>{const result=node('button',text);result.addEventListener('click',action);return result;},
    api:()=>{calls.push('api');throw Error('Mount must not send API requests.');},
    run:()=>{calls.push('run');throw Error('Mount must not start a task.');},
    MutationObserver:class{constructor(callback){this.callback=callback;observers.push(this);}observe(){}disconnect(){}}
  });
  return {context,created,areas,observers,calls};
}

const h=fixture();vm.runInContext(mount+'\nmountWorkspace();',h.context);
const panels=h.created.filter(x=>x.dataset.panel),navigation=h.created.filter(x=>x.dataset.workspace);
assert.deepEqual(panels.map(x=>x.dataset.panel),['home','titles','dictionary','settings','advanced']);
assert.equal(navigation.length,5);assert.equal(panels.find(x=>x.dataset.panel==='home').hidden,false);
assert.equal(h.created.filter(x=>x.dataset.dictionaryPanel).length,3);
assert.equal(h.observers.length,2);assert.equal(h.calls.length,0);
for(const control of navigation){control.click();assert.equal(panels.find(x=>x.dataset.panel===control.dataset.workspace).hidden,false);}
h.context.openServiceSettings('synthetic');
assert.equal(panels.find(x=>x.dataset.panel==='settings').hidden,false);
h.observers[0].callback();assert.equal(h.calls.length,0);

// Prove this executable test catches the exact scoped-reference regression.
const broken=mount.replace("const tab=node('button',title);", "const tab=node('button',title,custom?'jml-data':undefined);");
assert.notEqual(broken,mount);
assert.throws(()=>vm.runInContext(broken+'\nmountWorkspace();',fixture().context),/custom is not defined/);
console.log(JSON.stringify({passed:4,failed:0,checks:[
  'Real mountWorkspace entry point executes through initial Home refresh without undefined references',
  'All five pages and three dictionary panes are mounted before navigation runs',
  'Every workspace navigation handler and service-settings route executes after initialization without a model/API request',
  'The exact undefined custom regression fails this executable startup check'
]}));
