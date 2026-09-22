const fs = require('fs'), vm = require('vm'), assert = require('assert');
const html = fs.readFileSync('src/Localizer.Plugin/Web/admin.html', 'utf8');
class El {
  constructor(tag, text = '', cls = '') { this.tag = tag; this.textContent = text; this.className = cls; this.children = []; this.style = {}; this.value = this.defaultValue = ''; }
  append(...children) { children.forEach(child => { child.parent = this; this.children.push(child); }); }
  all() { return this.children.flatMap(child => [child, ...child.all()]); }
  querySelectorAll(selector) {
    const matches = (el, rule) => rule === 'input:not([type=checkbox])' ? el.tag === 'input' && el.type !== 'checkbox' : rule.startsWith('.') ? el.className.split(' ').includes(rule.slice(1)) : el.tag === rule;
    return this.all().filter(el => selector.split(',').some(rule => {
      const parts = rule.trim().split(/\s+/), leaf = parts.pop();
      if (!matches(el, leaf)) return false;
      if (!parts.length) return true;
      for (let ancestor = el.parent; ancestor; ancestor = ancestor.parent) if (matches(ancestor, parts[0])) return true;
      return false;
    }));
  }
}
const page = new El('div'), status = {}, calls = [];
let failSave = false;
const ctx = vm.createContext({
  page, status, lib: { value: 'library' }, base: () => 'libraries/library',
  node: (...args) => new El(...args), button: (text, action) => Object.assign(new El('button', text), { action }),
  api: async (route, body) => {
    calls.push({ route, body });
    if (failSave) throw Error('Lost response');
    assert(body, 'A dirty catalog must not make a refresh request');
    return { Original: body.Original, Effective: body.Replacement, Override: body.Replacement, Revision: body.Revision + 1 };
  }
});
function load(start, end) { vm.runInContext(html.slice(html.indexOf(start), html.indexOf(end, html.indexOf(start))), ctx); }
// Load the production field/checkbox helpers so this regression uses their actual input element types.
const helperLine = prefix => html.split('\n').find(line => line.startsWith(prefix));
vm.runInContext(helperLine('        function field('), ctx);
vm.runInContext(helperLine('        function checkbox('), ctx);
vm.runInContext(helperLine('        function catalogDirty('), ctx);
load('        function renderBilingualTerm(', '        async function refreshBatches(');
const row = {
  ExampleItemId: 'movie', SourceId: 'source',
  Entry: { Original: 'Synthetic term', Effective: 'Original English', Revision: 4 },
  Alternate: { Original: 'Synthetic term', Effective: '原有中文', Revision: 7 }
};
ctx.row = row;
vm.runInContext("renderBilingualTerm(page,row,base(),'en')", ctx);
const card = page.children[0], inputs = card.querySelectorAll('input:not([type=checkbox])'), checks = card.querySelectorAll('input').filter(x => x.type === 'checkbox');
const buttons = text => card.querySelectorAll('button').filter(x => x.textContent === text);
const dirty = () => vm.runInContext('catalogDirty()', ctx);
let passed = 0;
const check = (condition, message) => { assert(condition, message); passed++; };
(async () => {
  check(inputs.length === 2 && card.querySelectorAll('textarea').length === 0, 'Both actual dictionary editors are single-line inputs');
  check(!dirty(), 'Saved inputs are initially clean');
  checks[0].checked = true;
  check(!dirty(), 'Shared-dictionary acknowledgement is not a text draft');
  inputs[0].value = '未保存中文'; inputs[1].value = 'Unsaved English';
  check(dirty(), 'Single-line input drafts are detected');
  await vm.runInContext('refreshCatalog(50)', ctx);
  check(calls.length === 0 && inputs[0].value === '未保存中文' && status.textContent.includes('Save or discard'), 'Refresh/pagination is blocked before replacing drafts');
  await buttons('Discard changes')[0].action();
  check(inputs[0].value === '原有中文' && inputs[1].value === 'Original English' && !dirty(), 'Discard restores both input values');
  inputs[0].value = '保存中文'; inputs[1].value = 'Keep other draft';
  await buttons('Save translation')[0].action();
  const request = calls.at(-1).body;
  check(request.Revision === 7 && request.SourceId === 'source' && request.Language === 'zh-Hans' && request.ConfirmSharedDictionary, 'Saving keeps revision/source and shared-write confirmation checks');
  check(inputs[0].defaultValue === '保存中文' && dirty(), 'Saving one language keeps the other draft dirty');
  await buttons('Discard changes')[0].action();
  check(inputs[0].value === '保存中文' && inputs[1].value === 'Original English' && !dirty(), 'Discard uses the latest saved baseline');
  inputs[1].value = 'Failed save draft'; checks[1].checked = true; failSave = true;
  await assert.rejects(buttons('Save translation')[1].action());
  check(dirty() && inputs[1].value === 'Failed save draft' && inputs[1].defaultValue === 'Original English', 'Failed save preserves the input and its baseline');
  await buttons('Discard changes')[0].action();
  const multiline = new El('textarea'); multiline.defaultValue = 'Saved'; multiline.value = 'Draft'; card.append(multiline);
  check(dirty(), 'Multiline editors remain covered');
  await buttons('Discard changes')[0].action();
  check(multiline.value === 'Saved' && !dirty(), 'Discard handles multiline editors too');
  console.log(JSON.stringify({ passed, failed: 0, cloudRequests: 0, productionWrites: 0 }));
})().catch(error => { console.error(error); process.exitCode = 1; });
