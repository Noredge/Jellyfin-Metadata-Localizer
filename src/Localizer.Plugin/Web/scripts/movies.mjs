export async function loadMovies() {
  listAbort?.abort();
  listAbort = new AbortController();
  const query = new URLSearchParams({
    search: $('#luSearch').value,
    filter: $('#luFilter').value,
    offset,
    limit: 30,
  });
  if ($('#luBrowseLibrary').value) query.set('libraryId', $('#luBrowseLibrary').value);
  const data = await api('movies?' + query, undefined, listAbort.signal);
  total = data.total;
  $('#luMovieCount').textContent = total + ' 部影片 · 已选 ' + selected.size + ' 部';
  $('#luPaging').textContent =
    (total ? offset + 1 : 0) + '–' + Math.min(offset + 30, total) + ' / ' + total;
  $('#luPrev').disabled = offset === 0;
  $('#luNext').disabled = offset + 30 >= total;
  $('#luMovieList').innerHTML =
    data.items
      .map(
        (m) =>
          '<li><div class="lu-row"><input type="checkbox" aria-label="选择影片" data-select="' +
          escape(m.id) +
          '" ' +
          (selected.has(m.id) ? 'checked' : '') +
          '><div class="lu-grow"><button class="lu-link" data-movie="' +
          escape(m.id) +
          '">' +
          escape(m.name) +
          '</button><div class="lu-muted">' +
          (m.pending ? '处理中 ' + m.pending + ' 项' : '') +
          (m.source_problems ? ' · 来源问题 ' + m.source_problems + ' 项' : '') +
          '</div></div></div></li>',
      )
      .join('') || '<li class="lu-muted">没有匹配的已观察影片</li>';
}

export async function openMovie(id, initialField = 'Name') {
  if ($('#luEditor').open && !canCloseEditor()) return;
  detailAbort?.abort();
  detailAbort = new AbortController();
  editor = await api('movies/' + encodeURIComponent(id), undefined, detailAbort.signal);
  drafts = {};
  fieldName = editor.fields.some((x) => x.state.field === initialField) ? initialField : 'Name';
  historySource = null;
  historyOffset = 0;
  $('#luEditorTitle').textContent =
    editor.fields.find((x) => x.state.field === 'Name')?.observation.display || '影片';
  $('#luDraftNotice').hidden = true;
  status('', false, '#luEditorStatus');
  renderEditor();
  if (!$('#luEditor').open) $('#luEditor').showModal();
}

export function currentField() {
  return editor.fields.find((x) => x.state.field === fieldName);
}

export function saveDraft() {
  const area = $('#luDraft');
  if (area && editor)
    drafts[fieldName] = { value: area.value, changed: area.dataset.changed === 'true' };
}

export function canCloseEditor() {
  saveDraft();
  if (Object.values(drafts).some((x) => x.changed)) {
    $('#luDraftNotice').hidden = false;
    return false;
  }
  return true;
}

export function renderEditor() {
  const f = currentField();
  if (!f) return;
  $('#luDraftNotice').hidden = !Object.values(drafts).some((x) => x.changed);
  $('#luFieldTabs').innerHTML = editor.fields
    .map(
      (x) =>
        '<button data-field="' +
        x.state.field +
        '" aria-pressed="' +
        (x.state.field === fieldName) +
        '">' +
        labels[x.state.field] +
        '</button>',
    )
    .join('');
  const empty = f.state.availability === 'empty';
  const draft = drafts[fieldName]?.value ?? f.selection?.text ?? '';
  $('#luEditorBody').innerHTML =
    '<div class="lu-two"><div><h3>原文</h3><div class="lu-read">' +
    escape(
      fieldName === 'Genres'
        ? JSON.parse(f.state.renderJson || '[]').join('、')
        : f.state.prefix + f.state.content,
    ) +
    '</div></div><div><h3>当前显示</h3><div class="lu-read">' +
    escape(
      fieldName === 'Genres'
        ? JSON.parse(f.observation.display || '[]').join('、')
        : f.observation.display,
    ) +
    '</div></div></div>' +
    (f.observation.sourceError
      ? '<p class="lu-error">' + escape(name(f.observation.sourceError)) + '</p>'
      : '') +
    (f.observation.displayError
      ? '<p class="lu-muted">' +
        escape(name(f.observation.displayError)) +
        '。仍可保存人工版本。</p>'
      : '') +
    ('<label>人工译文' +
      (fieldName === 'Genres' ? '（JSON 字符串数组）' : '') +
      '<textarea id="luDraft" data-changed="' +
      (drafts[fieldName]?.changed ? 'true' : 'false') +
      '">' +
      escape(draft) +
      '</textarea></label><div class="lu-actions"><button data-action="save">只保存</button><button class="lu-primary" data-action="show">保存并显示</button><button data-action="keep">保留原文</button><button data-action="restore">恢复原文</button><button data-action="follow">跟随全局目标</button><button id="luRetranslateField" ' +
      (empty ? 'hidden' : '') +
      '>重译此字段</button></div>') +
    '<details id="luHistory"><summary>译文历史</summary><label>历史语言<select id="luHistoryLanguage"><option value="zh-Hans">简体中文</option><option value="en">English</option></select></label><div id="luHistoryBody"></div><div class="lu-row"><button id="luHistoryPrev">上一页</button><button id="luHistoryNext">下一页</button></div></details>' +
    (fieldName === 'Name'
      ? '<details id="luBinding"><summary>核对标题原文与前缀</summary><div id="luBindingBody"></div></details>'
      : '');
  $('#luHistoryLanguage').value = editor.settings.language;
  $('#luHistory').addEventListener('toggle', () => {
    if ($('#luHistory').open) run(loadHistory, '#luEditorStatus');
  });
  $('#luHistoryLanguage').onchange = () => {
    historyOffset = 0;
    historySource = null;
    run(loadHistory, '#luEditorStatus');
  };
  $('#luHistoryPrev').onclick = () => {
    historyOffset = Math.max(0, historyOffset - 20);
    run(loadHistory, '#luEditorStatus');
  };
  $('#luHistoryNext').onclick = () => {
    historyOffset += 20;
    run(loadHistory, '#luEditorStatus');
  };
  if ($('#luBinding'))
    $('#luBinding').addEventListener('toggle', () => {
      if ($('#luBinding').open) run(loadBinding, '#luEditorStatus');
    });
}

export async function fieldAction(action, translationId) {
  saveDraft();
  const f = currentField();
  await api('field', {
    expected: f.state,
    targetEpoch: editor.settings.targetEpoch,
    language: editor.settings.language,
    action,
    expectedDisplay: f.observation.display,
    text: $('#luDraft')?.value,
    translationId,
    commandId: uid(),
  });
  if (action === 'save' || action === 'show')
    drafts[fieldName] = { value: $('#luDraft').value, changed: false };
  const next = await api('movies/' + editor.id);
  editor = next;
  renderEditor();
  status(
    action === 'save' ? '版本已保存。显示保持当前状态。' : '操作已保存，显示会由后台更新。',
    false,
    '#luEditorStatus',
  );
}

export async function loadHistory() {
  const f = currentField(),
    lang = $('#luHistoryLanguage').value;
  const q = new URLSearchParams({
    mediaId: editor.id,
    field: fieldName,
    language: lang,
    offset: historyOffset,
    limit: 20,
  });
  if (historySource) q.set('sourceId', historySource);
  const data = await api('history?' + q, undefined, detailAbort?.signal);
  historySource = historySource || data.currentSourceId;
  $('#luHistoryBody').innerHTML =
    '<label>原文版本<select id="luHistorySource">' +
    data.sources
      .map(
        (x) =>
          '<option value="' +
          escape(x.id) +
          '" ' +
          (x.id === historySource ? 'selected' : '') +
          '>' +
          escape(x.content.slice(0, 90)) +
          '</option>',
      )
      .join('') +
    '</select></label><ul class="lu-list lu-history">' +
    data.versions
      .map(
        (x) =>
          '<li><small>' +
          escape(x.origin) +
          ' · ' +
          escape(x.createdUtc) +
          '</small><div class="lu-read">' +
          escape(x.text) +
          '</div>' +
          (x.sourceId === f.state.sourceId && lang === editor.settings.language
            ? '<button data-choose="' + escape(x.id) + '">选用并显示</button>'
            : '') +
          '</li>',
      )
      .join('') +
    '</ul>';
  $('#luHistorySource').onchange = () => {
    historySource = $('#luHistorySource').value;
    historyOffset = 0;
    run(loadHistory, '#luEditorStatus');
  };
  $('#luHistoryPrev').disabled = !historyOffset;
  $('#luHistoryNext').disabled = data.versions.length < 20;
}

export async function loadBinding() {
  const data = await api('movies/' + editor.id + '/binding');
  $('#luBindingBody').innerHTML =
    '<label>可信标题原文<textarea id="luOriginal">' +
    escape(data.original || data.observedOriginalTitle) +
    '</textarea></label><label>显示前缀<input id="luPrefix" value="' +
    escape(data.prefix) +
    '"></label><button id="luConfirmBinding">保存来源绑定</button>';
  $('#luConfirmBinding').onclick = () =>
    run(async () => {
      await api('movies/' + editor.id + '/binding', {
        observation: data.observation,
        original: $('#luOriginal').value,
        prefix: $('#luPrefix').value,
      });
      editor = await api('movies/' + editor.id);
      renderEditor();
      status('来源绑定已保存。', false, '#luEditorStatus');
    }, '#luEditorStatus');
}
