import fs from 'node:fs';
import * as api from '../src/Localizer.Plugin/Web/scripts/api.mjs';
import * as overview from '../src/Localizer.Plugin/Web/scripts/overview.mjs';
import * as movies from '../src/Localizer.Plugin/Web/scripts/movies.mjs';
import * as settings from '../src/Localizer.Plugin/Web/scripts/settings.mjs';
import * as work from '../src/Localizer.Plugin/Web/scripts/work.mjs';
import * as rules from '../src/Localizer.Plugin/Web/scripts/rules.mjs';
import * as lifecycle from '../src/Localizer.Plugin/Web/scripts/lifecycle.mjs';
import * as i18n from '../src/Localizer.Plugin/Web/scripts/i18n.mjs';

const exported = { ...api, ...overview, ...movies, ...settings, ...work, ...rules, ...lifecycle, ...i18n };
export const html = fs.readFileSync(new URL('../src/Localizer.Plugin/Web/unified.html', import.meta.url), 'utf8');
export function functionSource(name) {
  const value = exported[name];
  if (typeof value !== 'function') throw new Error('Missing exported UI function: ' + name);
  const source = value.toString();
  return /^(?:async )?function\b/.test(source) ? source : 'const ' + name + ' = ' + source + ';';
}
export function bindingSource(name) {
  if (!(name in exported)) throw new Error('Missing exported UI binding: ' + name);
  return 'const ' + name + ' = ' + JSON.stringify(exported[name]) + ';';
}
export const pollingSource = () => ['invalidatePoll', 'poll'].map(functionSource).join('\n');
