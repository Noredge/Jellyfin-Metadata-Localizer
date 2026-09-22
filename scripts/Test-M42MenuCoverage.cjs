const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync('src/Localizer.Plugin/Web/admin.html', 'utf8');
const dictionary = JSON.parse(fs.readFileSync('src/Localizer.Plugin/Web/menu.zh-Hans.json', 'utf8'));
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const translation = html.slice(html.indexOf('        function menuText('), html.indexOf('        function localizeMenu('));
const context = vm.createContext({menuLanguage: 'zh-Hans', menuDictionary: dictionary});
vm.runInContext(translation, context);
new vm.Script(script);

// Scan literal UI sinks from the real page, so a newly added unlocalized button,
// field label or status message fails without updating a mirrored fixture list.
const literal = String.raw`(?:'(?:\\.|[^'\\])*'|"(?:\\.|[^"\\])*")`;
const sinks = [
  new RegExp(String.raw`\b(?:button|labeledData)\s*\(\s*(${literal})(?=\s*[,\)])`, 'g'),
  new RegExp(String.raw`\bnode\s*\(\s*${literal}\s*,\s*(${literal})(?=\s*[,\)])`, 'g'),
  new RegExp(String.raw`\b(?:field|checkbox)\s*\([^,\n]+,\s*(${literal})(?=\s*[,\)])`, 'g'),
  new RegExp(String.raw`\.(?:textContent|placeholder|title)\s*=\s*(${literal})(?=\s*[;,\)])`, 'g'),
  new RegExp(String.raw`\.setAttribute\s*\(\s*['"](?:aria-label|placeholder|title|data-label)['"]\s*,\s*(${literal})(?=\s*\))`, 'g')
];
const visible = new Set();
for (const sink of sinks) {
  for (const match of script.matchAll(sink)) {
    const value = vm.runInNewContext(match[1]).trim();
    if (/[A-Za-z]{2}/.test(value)) visible.add(value);
  }
}
const markup = html.slice(0, html.indexOf('<script>'));
for (const match of markup.matchAll(/\b(?:aria-label|placeholder|title|data-label)\s*=\s*(["'])(.*?)\1/g)) {
  const value = match[2].replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
  if (/[A-Za-z]{2}/.test(value)) visible.add(value);
}
// Feedback strings inside conditional expressions are also actual UI text.
for (const match of script.matchAll(new RegExp(literal, 'g'))) {
  const value = vm.runInNewContext(match[0]);
  if (/^[A-Z][A-Za-z ]+\b/.test(value) && /[.!?…]$/.test(value) && value.trim() === value && !/[\n{}<>]/.test(value)) visible.add(value);
}
const brands = new Set(['Jellyfin', 'Metadata Localizer', 'LM Studio', 'OpenAI', 'Groq', 'Qwen']);
const missing = [...visible].filter(value => !brands.has(value) && context.menuText(value) === value);
assert.deepEqual(missing, [], 'Untranslated literal UI text: ' + JSON.stringify(missing));

const cases = new Map([
  ['Field / language', '字段 / 语言'],
  ['Original title:', '原始标题：'],
  ['Required current title:', '要求的当前标题：'],
  ['Edit English dictionary · 1 original terms', '编辑英文词典 · 1 个原始词条'],
  ['OpenAI · Generate Chinese + English · Display Chinese; keep existing translations · Titles + full overviews · Follow current display', 'OpenAI · 生成中文和英文译文 · 显示中文；保留已有译文 · 标题和完整简介 · 跟随当前显示'],
  ['Retranslate all; replace manual edits', '重新翻译全部；重新生成手动编辑的译文'],
  ['Library defaults loaded (revision 12). Adjust the next task on Home.', '已加载媒体库默认设置（版本 12）。可在主页调整下一次任务。'],
  ['Titles awaiting source confirmation: 2. Overviews awaiting source confirmation or repair: 3. Empty original overviews: 1. Accepted translations only; display may differ. Refresh after changes.', '标题原文待确认：2 部；简介原文待确认或修复：3 部；原始简介为空：1 部。仅统计已接受译文，实际显示可能不同；修改后请刷新。'],
  ['Dictionary update needed: 5 terms across 3 movies (Chinese missing: 2, English missing: 4). 1 movies still need genre sources read or confirmed.', '词典待补全：3 部影片涉及 5 个词条（中文缺少 2 个，英文缺少 4 个）。 另有 1 部影片的分类来源需要读取或确认。'],
  ['Dictionary covers all 118 detected terms in Chinese and English.', '已检测的 118 个分类词均有中英文映射。'],
  ['Selected: 4 items (up to 100 across pages) · Unsaved drafts: 2 items', '已选择 4 项（跨页最多 100 项） · 未保存草稿：2 项'],
  ['Current library: 84 items · matches: 20 items · showing: 1–20。', '当前媒体库 84 部 · 匹配 20 部 · 显示 1–20'],
  ['Latest task: 2 unfinished. View results and issues.', '最近任务有 2 项未完成，请查看结果和问题。'],
  ['View 3 items needing attention', '查看 3 个待处理项目'],
  ['Preference: Follow library · Original is displayed', '偏好：跟随媒体库 · 当前显示原文'],
  ['Overview application: Applied', '简介应用结果：已应用'],
  ['Overview restore: NoChange', '简介恢复结果：无需更改'],
  ['Generation: Uncertain', '生成结果：结果不确定'],
  ['Last attempt: Service rate limit. Retry later.', '上次尝试：服务限制了请求频率，请稍后重试。'],
  ['Diagnostic code: overview_nfo_ambiguous', '诊断代码：overview_nfo_ambiguous'],
  ['Model list received: 4. Translation and selected-model access have not been tested.', '已读取 4 个模型。尚未测试翻译或所选模型的访问权限。'],
  ['OpenAI: Key file found · Groq: Key file not found', 'OpenAI：已找到密钥文件 · Groq：未找到密钥文件'],
  ['OpenAI: Key file found；Groq: Key file found', 'OpenAI：已找到密钥文件；Groq：已找到密钥文件'],
  ['OpenAI settings', 'OpenAI 设置'],
  ['Groq · Configure a model', 'Groq · 请配置模型'],
  ['Groq · Configure a model settings', 'Groq · 请配置模型 设置'],
  ['Task service: OpenAI · gpt-5.6-sol. New settings do not change this task.', '任务服务：OpenAI · gpt-5.6-sol。新设置不会更改此任务。'],
  ['Send these original terms to qwen/qwen3-32b using the frozen task settings.', '按任务创建时的设置将这些原始词条发送至 qwen/qwen3-32b。'],
  ['12 matching people; 10 on this page. Select a person to edit.', '匹配 12 人，本页显示 10 人。选择人员即可编辑。'],
  ['Waiting for the service request interval. Continuing in about 6 seconds.', '正在等待服务请求间隔，约 6 秒后继续。']
]);
for (const [source, expected] of cases) assert.equal(context.menuText(source), expected, source);
for (const value of ['gpt-5.6-sol', 'qwen/qwen3-32b', '日本語の原文', 'ABC-123']) assert.equal(context.menuText(value), value);
context.menuLanguage = 'en';
for (const source of [...visible, ...cases.keys()]) assert.equal(context.menuText(source), source, 'English mode: ' + source);
console.log(JSON.stringify({passed: true, literalUiStrings: visible.size, dynamicTemplates: cases.size, preservedDataExamples: 4, englishModeVerified: true}));
