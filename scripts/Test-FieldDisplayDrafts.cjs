const fs = require('fs'), vm = require('vm'), assert = require('assert');
const html = fs.readFileSync('src/Localizer.Plugin/Web/admin.html', 'utf8'), elements = new Map(), calls = [];
class El {
  constructor(tag, text = '', cls = '') {
    this.tag = tag; this.textContent = text; this.children = []; this.dataset = {}; this.attributes = {};
    this.classList = { add: name => elements.set('.' + name, this) };
    cls.split(' ').filter(Boolean).forEach(name => this.classList.add(name));
  }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(name, value) { this.attributes[name] = value; }
}
const controls = ['Name', 'Overview', 'Genres'].flatMap(field => ['en', 'zh-Hans', 'original'].map(language => {
  const control = new El('button'); control.dataset = { displayField: field, displayLanguage: language }; return control;
}));
elements.set('.jml-display-choices', { querySelectorAll: () => controls });
for (const field of ['Name', 'Overview', 'Genres']) elements.set('[data-current-field="' + field + '"]', new El('p'));
const fields = {
  '.jml-campaign-mode': { value: 'current' }, '.jml-generation-range': { value: 'missing' },
  '.jml-preserve-human': { checked: false }, '.jml-sync-genres': { checked: false },
  '.jml-include-titles': { checked: true }, '.jml-include-overviews': { checked: true }
};
const snapshot = { Fields: {
  Name: { total: 2, en: 2, 'zh-Hans': 0, original: 0 },
  Overview: { total: 2, en: 0, 'zh-Hans': 2, original: 0 },
  Genres: { total: 2, en: 0, 'zh-Hans': 0, original: 2 }
} };
let failRead = false;
const ctx = vm.createContext({
  busy: false, lib: { value: 'A' }, lang: { value: 'en' }, status: {}, base: () => 'libraries/A',
  $: selector => elements.get(selector) || fields[selector] || new El('div'),
  node: (...args) => new El(...args), button: (text, action) => Object.assign(new El('button', text), { action }), newDisplay: new El('section'),
  campaignList: [], campaignUnfinished: () => false, campaignEpoch: 1, coverageScope: '', refreshCoveragePanel: null,
  fieldDisplayScope: 'libraries/A', fieldDisplaySnapshot: snapshot, fieldDisplayDirty: true,
  fieldDisplayChoices: { Name: 'zh-Hans', Overview: 'zh-Hans', Genres: 'original' }, fieldDisplayEdited: new Set(['Name']), fieldDisplayRequests: new Map(),
  requireModel:()=>true,hasDrafts: () => false, selectedService: () => ({ Id: 'synthetic' }), generationLanguages: () => ['zh-Hans', 'en'],
  generationDisplay: () => ctx.fieldDisplayChoices.Name, refreshSavedLanguages: async () => {},
  refreshCampaigns: async () => {}, refreshWorkbench: async () => {}, workbenchStart: 0,
  serviceSettings: { Revision: 1 }, campaignRequest: null, crypto: { randomUUID: () => 'synthetic-request' },
  api: async (path, body) => { calls.push({ path, body }); if (body) return {}; if (failRead) throw Error('Read failed'); return snapshot; }
});
function load(start, end) { const at = html.indexOf(start); vm.runInContext(html.slice(at, html.indexOf(end, at)), ctx); }
load('        function refreshFieldDisplayControls()', '        function mountModernWorkspace()');
load("          const foot=node('div',undefined,'jml-display-footer')", '          const campaign=home.querySelector');
load('        async function startCampaign()', '        function detailDirty()');
const discard = elements.get('.jml-field-display-discard'), apply = elements.get('.jml-field-display-apply');
let passed = 0;
const check = (condition, message) => { assert(condition, message); passed++; };
(async () => {
  vm.runInContext('refreshFieldDisplayControls()', ctx);
  check(!discard.hidden && !discard.disabled, 'Dirty display choices offer discard');
  await vm.runInContext('startCampaign()', ctx);
  check(calls.length === 0 && ctx.status.textContent.includes('Apply display changes'), 'Unapplied display choices block translation');
  await discard.action();
  check(calls.length === 1 && calls[0].path.endsWith('/display-state') && calls[0].body === undefined, 'Discard reads the actual display without posting any action');
  check(!ctx.fieldDisplayDirty && ctx.fieldDisplayEdited.size === 0 && discard.hidden && apply.disabled, 'Discard clears the draft and hides its action');
  check(ctx.fieldDisplayChoices.Name === 'en' && ctx.fieldDisplayChoices.Overview === 'zh-Hans' && ctx.fieldDisplayChoices.Genres === 'original', 'Discard restores title choice while retaining the actual other field languages');
  const count = calls.length; await discard.action();
  check(calls.length === count, 'A clean discard is a no-op');
  await vm.runInContext('startCampaign()', ctx);
  const campaign = calls.at(-1);
  check(campaign.path.endsWith('/campaigns/library') && campaign.body.DisplayLanguage === 'en' && campaign.body.OverviewDisplayLanguage === 'zh-Hans', 'Translation starts after discard with current independent field languages');
  ctx.fieldDisplayDirty = true; ctx.fieldDisplayEdited.add('Name'); ctx.fieldDisplayChoices.Name = 'zh-Hans';
  await vm.runInContext('applyFieldDisplay()', ctx);
  const application = calls.find(call => call.path.endsWith('/campaigns/field-display'));
  check(application.body.TitleLanguage === 'zh-Hans' && application.body.OverviewLanguage === null && application.body.GenreLanguage === null, 'A later title-only edit does not apply other fields');
  ctx.fieldDisplayDirty = true; ctx.fieldDisplayEdited.add('Name'); ctx.fieldDisplayChoices.Name = 'original'; failRead = true;
  const beforeFailure = calls.length; await assert.rejects(discard.action());
  check(calls.length === beforeFailure + 1 && calls.at(-1).body === undefined, 'Failed discard still performs only a read');
  check(ctx.fieldDisplayDirty && ctx.fieldDisplayEdited.has('Name') && ctx.fieldDisplayChoices.Name === 'original' && !discard.hidden, 'Failed read preserves the prior draft and its start guard');
  ctx.busy = true; vm.runInContext('refreshFieldDisplayControls()', ctx);
  check(discard.disabled, 'Discard is disabled while another action is running');
  console.log(JSON.stringify({ passed, failed: 0, cloudRequests: 0, productionWrites: 0 }));
})().catch(error => { console.error(error); process.exitCode = 1; });
