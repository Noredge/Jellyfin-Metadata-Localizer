export function settingsDirty() {
  dirty = true;
  $('#luDirty').textContent = '有未保存修改';
  $('#luDiscard').hidden = false;
}

export async function loadSettings() {
  if (dirty || settingsLoading) return;
  settingsLoading = true;
  let loaded = false;
  const fields = $$('#luSettings input,#luSettings select,#luSave');
  fields.forEach((el) => (el.disabled = true));
  try {
    await loadState();
    connection = await api('connection');
    const s = state.settings;
    $('#luLanguage').value = s.language;
    $('#luTitle').checked = s.fields.title;
    $('#luSynopsis').checked = s.fields.overview;
    $('#luGenres').checked = s.fields.genres;
    $('#luAutomatic').checked = s.autoEnabled;
    $('#luManagedLibraries').innerHTML =
      '<p class="lu-muted">管理媒体库</p>' +
      state.libraries
        .map((x) => {
          const id = x.id.replaceAll('-', '').toLowerCase();
          return (
            '<label><input data-managed="' +
            escape(id) +
            '" type="checkbox" ' +
            (s.managedLibraries.includes(id) ? 'checked' : '') +
            '><span class="lu-data">' +
            escape(x.name) +
            '</span>' +
            (x.saversDisabled ? '' : ' · 保存器已开启，显示更新将被阻止') +
            '</label>'
          );
        })
        .join('');
    $('#luEndpoint').value = connection.endpoint || '';
    showModels(connection.model ? [connection.model] : [], connection.model || '');
    $('#luKey').value = '';
    $('#luTimeout').value = (connection.timeoutMs || 60000) / 1000;
    $('#luRpm').value = connection.requestsPerMinute || 30;
    const params = normalize(JSON.parse(connection.parametersJson || '{}'));
    $('#luBudget').value = params.maxOutputTokens || 3200;
    $('#luOverviewBudget').value =
      params.overviewMaxOutputTokens || Math.max(4096, params.maxOutputTokens || 3200);
    $('#luTokenParameter').value = params.tokenParameter || 'max_tokens';
    $('#luJsonMode').checked = params.useJsonResponseFormat !== false;
    dirty = false;
    connectionDirty = false;
    $('#luDirty').textContent = '已保存';
    $('#luDiscard').hidden = true;
    loaded = true;
  } finally {
    settingsLoading = false;
    if (loaded) fields.forEach((el) => (el.disabled = false));
  }
}

export function settingsChange() {
  return {
    revision: state.settings.revision,
    language: $('#luLanguage').value,
    fields: {
      title: $('#luTitle').checked,
      overview: $('#luSynopsis').checked,
      genres: $('#luGenres').checked,
    },
    managedLibraries: $$('[data-managed]:checked').map((x) => x.dataset.managed),
    autoEnabled: $('#luAutomatic').checked,
  };
}

export function validateConnectionDraft() {
  for (const [selector, label, min, max] of [
    ['#luBudget', '标题/分类输出上限', 64, 32768],
    ['#luOverviewBudget', '简介输出上限', 64, 32768],
    ['#luTimeout', '超时秒数', 1, 600],
    ['#luRpm', '每分钟请求数', 1, 10000],
  ]) {
    const el = $(selector),
      value = Number(el.value);
    if (!el.value.trim() || !Number.isInteger(value) || value < min || value > max) {
      el.focus();
      throw new Error(label + '需为 ' + min + '–' + max + ' 的整数。');
    }
  }
}

export async function saveSettings(confirmed = false) {
  if (!confirmed && $('#luLanguage').value !== state.settings.language) {
    const next = settingsChange();
    const data = await api('settings/preview', next);
    preview = { ...data, kind: 'language' };
    $('#luPreviewText').textContent =
      '切换全局目标至 ' +
      (next.language === 'en' ? 'English' : '简体中文') +
      '：' +
      data.count +
      ' 个字段，可复用 ' +
      data.cached +
      '，保留原文 ' +
      data.keptOriginal +
      '，缺译 ' +
      data.missing +
      '。缺译项会请求模型，两种语言历史都会保留。';
    $('#luCommit').textContent = '确认保存并切换';
    $('#luCommit').disabled = false;
    $('#luPreview').showModal();
    return;
  }
  const button = $('#luSave');
  button.disabled = true;
  let connectionSaved = false;
  try {
    let revision = state.settings.revision;
    if (connectionDirty) {
      validateConnectionDraft();
      await api('connection', {
        revision: connection.revision || 0,
        settingsRevision: revision,
        endpoint: $('#luEndpoint').value.trim(),
        model: $('#luModel').value.trim(),
        apiKey: $('#luKey').value || null,
        timeoutMs: Number($('#luTimeout').value) * 1000,
        requestsPerMinute: Number($('#luRpm').value),
        parametersJson: JSON.stringify({
          ...JSON.parse(connection.parametersJson || '{}'),
          MaxOutputTokens: Number($('#luBudget').value),
          OverviewMaxOutputTokens: Number($('#luOverviewBudget').value),
          TokenParameter: $('#luTokenParameter').value,
          UseJsonResponseFormat: $('#luJsonMode').checked,
        }),
      });
      connectionDirty = false;
      connectionSaved = true;
      revision += 1;
      $('#luKey').value = '';
      state = await api('state');
      connection = await api('connection');
    }
    await api('settings', {
      revision,
      language: $('#luLanguage').value,
      fields: {
        title: $('#luTitle').checked,
        overview: $('#luSynopsis').checked,
        genres: $('#luGenres').checked,
      },
      managedLibraries: $$('[data-managed]:checked').map((x) => x.dataset.managed),
      autoEnabled: $('#luAutomatic').checked,
    });
    dirty = false;
    await loadSettings();
    status('设置已保存；语言切换会进入同一处理流程。');
  } catch (error) {
    if (connectionSaved) error.message = '连接已保存；全局设置尚未保存。草稿保留：' + error.message;
    throw error;
  } finally {
    button.disabled = false;
  }
}

export function showModels(models, selected = '') {
  const model = $('#luModel');
  model.innerHTML =
    '<option value="">请选择模型</option>' +
    models.map((x) => '<option value="' + escape(x) + '">' + escape(x) + '</option>').join('');
  model.value = models.includes(selected) ? selected : '';
}

export async function listModels() {
  const button = $('#luListModels');
  const endpoint = $('#luEndpoint').value.trim(),
    apiKey = $('#luKey').value;
  button.disabled = true;
  try {
    const data = await api('connection/models', { endpoint, apiKey: apiKey || null });
    if ($('#luEndpoint').value.trim() !== endpoint || $('#luKey').value !== apiKey) {
      status('连接草稿已变化，请重新获取模型列表。');
      return;
    }
    const selected = $('#luModel').value;
    showModels(data.models, selected);
    if ($('#luModel').value !== selected) {
      settingsDirty();
      connectionDirty = true;
    }
    status('已读取 ' + data.models.length + ' 个模型。未发送翻译请求。');
  } finally {
    button.disabled = false;
  }
}
