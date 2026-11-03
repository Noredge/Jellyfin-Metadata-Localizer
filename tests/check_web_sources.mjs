import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { assemblePage } from '../scripts/Build-Web.mjs';
import { messages, errors, uiWords } from '../src/Localizer.Plugin/Web/scripts/i18n.mjs';
import { html, functionSource } from './web_sources.mjs';

const checks = [];
const check = (name, test) => { test(); checks.push({ name, passed: true }); };
check('embedded delivery exactly matches readable source assembly', () => assert.equal(html, assemblePage()));
check('every error code has paired Chinese and English guidance', () => {
  for (const [code, text] of Object.entries(messages)) {
    assert(text.zh.trim()); assert(text.en.trim()); assert(!/[\u3400-\u9fff]/u.test(text.en));
    assert.equal(errors[code], text.zh); assert.equal(uiWords[text.zh], text.en);
  }
});
check('formatted exported functions retain their complete executable bodies', () => {
  const source = functionSource('loadWork'); assert(source.split('\n').length > 20); new vm.Script(source);
});
const nodes = new Map(), requests = [];
function element(id) {
  if (!nodes.has(id)) nodes.set(id, { dataset: {}, value: '', open: false, textContent: '', innerHTML: '', hidden: false,
    classList: { toggle() {} }, addEventListener() {}, querySelectorAll() { return []; } });
  return nodes.get(id);
}
const page = { querySelector: element, querySelectorAll: () => [], addEventListener() {} };
const context = vm.createContext({
  document: { querySelector: () => page, hidden: false, addEventListener() {}, createTreeWalker: () => ({ nextNode: () => null }) },
  window: { addEventListener() {} }, NodeFilter: { SHOW_TEXT: 4 },
  MutationObserver: class { observe() {} }, localStorage: { getItem: () => 'en', setItem() {} },
  ApiClient: { getUrl: path => path, setRequestHeaders() {} },
  fetch: async (path, options) => { requests.push({ path, method: options.method }); return { ok: true, json: async () => ({ Ready: false, Libraries: [], Health: { State: 'Unavailable' } }) }; },
  AbortController, URLSearchParams, setInterval: () => 1, clearInterval() {},
});
for (const script of html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)) vm.runInContext(script[1], context);
await new Promise(setImmediate);
check('complete generated page initializes through its real closure with GET-only setup', () => {
  assert.equal(requests.length, 1); assert.equal(requests[0].path, 'MetadataLocalizer/unified/state'); assert.equal(requests[0].method, 'GET');
  assert.equal(element('#luUiLanguage').value, 'en'); assert.equal(element('#luSetup').hidden, false);
});
check('Jellyfin receives a self-contained page without external script assets', () => {
  assert(!/<script[^>]+src=/i.test(html)); assert(!/^export (?:const|function|async)/m.test(html));
});
const result = { status: 'passed', checks, realModelRequests: 0 };
if (process.argv[2]) fs.writeFileSync(process.argv[2], JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
