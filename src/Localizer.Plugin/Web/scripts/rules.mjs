export async function loadTerms() {
  const ticket = beginRead('terms'),
    identity = JSON.stringify([$('#luTermSearch').value, termOffset]);
  try {
    const rows = await api(
      'terms?' +
        new URLSearchParams({ search: $('#luTermSearch').value, offset: termOffset, limit: 30 }),
      undefined,
      ticket.signal,
    );
    if (!ticket.current() || identity !== JSON.stringify([$('#luTermSearch').value, termOffset]))
      return;
    $('#luTermList').innerHTML = rows
      .map(
        (x, i) =>
          '<li><div class="lu-row"><div class="lu-grow">' +
          escape(x.original) +
          '<small> · ' +
          escape(x.chinese) +
          ' · ' +
          escape(x.english) +
          '</small></div><button data-term="' +
          i +
          '">编辑</button></div></li>',
      )
      .join('');
    $('#luTermPrev').disabled = !termOffset;
    $('#luTermNext').disabled = rows.length < 30;
    $$('[data-term]').forEach((el) => (el.onclick = () => editTerm(rows[Number(el.dataset.term)])));
  } finally {
    ticket.finish();
  }
}

export function editTerm(data) {
  rule = { type: 'term', data };
  $('#luRuleTitle').textContent = '分类词条';
  $('#luRuleBody').innerHTML =
    '<label>原词<input id="luRuleOriginal" ' +
    (data ? 'readonly' : '') +
    ' value="' +
    escape(data?.original) +
    '"></label><label>语言<select id="luRuleLanguage"><option value="zh-Hans">简体中文</option><option value="en">English</option></select></label><label>译词<input id="luRuleText" value="' +
    escape(data?.chinese) +
    '"></label><div class="lu-actions"><button id="luBuiltin">恢复此语言内置词</button><button id="luGenerateTerm">补齐此词译文</button></div>';
  $('#luRuleLanguage').onchange = () =>
    ($('#luRuleText').value =
      ($('#luRuleLanguage').value === 'en' ? data?.english : data?.chinese) || '');
  $('#luBuiltin').onclick = () =>
    run(async () => {
      await api('terms', {
        original: $('#luRuleOriginal').value,
        language: $('#luRuleLanguage').value,
        controlRevision: data?.controlRevision || 0,
        action: 'builtin',
        commandId: uid(),
      });
      $('#luRuleDialog').close();
      await loadTerms();
    }, '#luRuleStatus');
  $('#luGenerateTerm').onclick = () =>
    run(async () => {
      await loadState();
      if ($('#luRuleLanguage').value !== state.settings.language)
        throw new Error('要生成其他语言，请先切换全局目标。');
      await api('terms/generate', {
        settingsRevision: state.settings.revision,
        commandId: uid(),
        originals: [$('#luRuleOriginal').value],
      });
      $('#luRuleDialog').close();
      await openWork(null);
    }, '#luRuleStatus');
  $('#luDeleteRule').hidden = true;
  status('', false, '#luRuleStatus');
  $('#luRuleDialog').showModal();
}

export async function loadPeople() {
  const ticket = beginRead('people'),
    identity = JSON.stringify([$('#luPersonSearch').value, personOffset]);
  try {
    const data = await api(
      'people?' +
        new URLSearchParams({
          search: $('#luPersonSearch').value,
          offset: personOffset,
          limit: 30,
        }),
      undefined,
      ticket.signal,
    );
    if (
      !ticket.current() ||
      identity !== JSON.stringify([$('#luPersonSearch').value, personOffset])
    )
      return;
    if (personOffset && personOffset >= data.total) {
      personOffset = Math.max(0, Math.floor((data.total - 1) / 30) * 30);
      return loadPeople();
    }
    const rows = data.items;
    $('#luPeopleList').innerHTML =
      rows
        .map(
          (x, i) =>
            '<li><div class="lu-row"><div class="lu-grow">' +
            escape(x.original) +
            '<small> · ' +
            escape(x.chinese) +
            ' · ' +
            escape(x.english) +
            '</small></div><button data-person="' +
            i +
            '">编辑</button></div></li>',
        )
        .join('') || '<li class="lu-muted">没有匹配的人名规则</li>';
    $('#luPersonPrev').disabled = !personOffset;
    $('#luPersonNext').disabled = personOffset + rows.length >= data.total;
    $('#luPersonPaging').textContent =
      (data.total ? personOffset + 1 : 0) + '–' + (personOffset + rows.length) + ' / ' + data.total;
    $$('[data-person]').forEach(
      (el) => (el.onclick = () => editPerson(rows[Number(el.dataset.person)])),
    );
  } finally {
    ticket.finish();
  }
}

export function editPerson(data) {
  rule = { type: 'person', data };
  $('#luRuleTitle').textContent = '人名规则';
  $('#luRuleBody').innerHTML =
    '<label>原名<input id="luRuleOriginal" value="' +
    escape(data?.original) +
    '"></label><label>中文名<input id="luRuleChinese" value="' +
    escape(data?.chinese) +
    '"></label><label>英文名<input id="luRuleEnglish" value="' +
    escape(data?.english) +
    '"></label><label>别名（每行一个）<textarea id="luRuleAliases">' +
    escape((data?.aliases || []).join('\n')) +
    '</textarea></label>';
  $('#luDeleteRule').hidden = !data;
  status('', false, '#luRuleStatus');
  $('#luRuleDialog').showModal();
}

export async function saveRule(remove = false) {
  const d = rule.data;
  if (rule.type === 'term')
    await api('terms', {
      original: $('#luRuleOriginal').value,
      language: $('#luRuleLanguage').value,
      controlRevision: d?.controlRevision || 0,
      action: 'save',
      text: $('#luRuleText').value,
      commandId: uid(),
    });
  else
    await api('people', {
      id: d?.id || uid(),
      revision: d?.revision || 0,
      original: $('#luRuleOriginal').value,
      chinese: $('#luRuleChinese').value || null,
      english: $('#luRuleEnglish').value || null,
      aliases: $('#luRuleAliases')
        .value.split('\n')
        .map((x) => x.trim())
        .filter(Boolean),
      delete: remove,
    });
  $('#luRuleDialog').close();
  await (rule.type === 'term' ? loadTerms() : loadPeople());
}
