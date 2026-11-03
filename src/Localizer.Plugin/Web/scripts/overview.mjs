export function show(which) {
  active = which;
  ['overview', 'movies', 'settings'].forEach(
    (x) => ($('#lu' + x[0].toUpperCase() + x.slice(1)).hidden = x !== which),
  );
  $$('[data-page]').forEach((x) =>
    x.setAttribute('aria-selected', String(x.dataset.page === which)),
  );
  if (!state?.ready) return;
  run(() =>
    which === 'overview' ? loadOverview() : which === 'movies' ? loadMovies() : loadSettings(),
  );
}

export async function loadState(signal, accept = () => true) {
  const data = await api('state', undefined, signal);
  if (!accept()) return;
  state = data;
  setUiText(
    $('#luTarget'),
    state.settings
      ? state.settings.language === 'en'
        ? '全局目标 · English'
        : '全局目标 · 简体中文'
      : state.health?.state || '未就绪',
  );
  $('#luMissing').hidden = state.ready;
  const local = $('#luLocalRules');
  if (local) setUiText(local, state.localRules?.loaded ? '本机规则已加载' : '使用内置通用规则');
  const legacy = $('#luLegacyNotice');
  if (legacy) legacy.hidden = !state.legacyDataPresent;
  ['#luOverview', '#luMovies', '#luSettings'].forEach(
    (x) => ($(x).hidden = !state.ready || x !== '#lu' + active[0].toUpperCase() + active.slice(1)),
  );
  if (state.health?.error && !signal) status(name(state.health.error), true);
  const current = $('#luBrowseLibrary').value;
  const markup =
    '<option value="">全部已观察影片</option>' +
    (state.libraries || [])
      .map(
        (x) =>
          '<option value="' +
          escape(x.id.replaceAll('-', '').toLowerCase()) +
          '">' +
          escape(x.name) +
          '</option>',
      )
      .join('');
  setUiHtml($('#luBrowseLibrary'), markup);
  $('#luBrowseLibrary').value = current;
}

export function batchTime(value) {
  const date = new Date(value);
  if (!value || !Number.isFinite(date.getTime())) return '—';
  const pad = (n) => String(n).padStart(2, '0');
  return (
    date.getFullYear() +
    '-' +
    pad(date.getMonth() + 1) +
    '-' +
    pad(date.getDate()) +
    ' ' +
    pad(date.getHours()) +
    ':' +
    pad(date.getMinutes())
  );
}

export function recentBatches(rows) {
  return (
    rows
      .slice(0, 3)
      .map(
        (b) =>
          '<li><div class="lu-row"><button class="lu-link" data-batch="' +
          escape(b.id) +
          '">' +
          escape(name(b.kind)) +
          ' · ' +
          escape(languageName(b.language)) +
          '</button><small class="lu-grow">完成 ' +
          (b.completed || 0) +
          '/' +
          b.total +
          (b.pending ? ' · 待处理 ' + b.pending : '') +
          (b.problem ? ' · 历史失败/暂停 ' + b.problem : '') +
          (b.cancelled ? ' · 已取消' : '') +
          '</small><time class="lu-muted" datetime="' +
          escape(b.created_utc || '') +
          '" aria-label="提交时间 ' +
          escape(batchTime(b.created_utc)) +
          '">' +
          escape(batchTime(b.created_utc)) +
          '</time>' +
          (!b.cancelled && b.pending
            ? '<button data-cancel="' + escape(b.id) + '">取消此批</button>'
            : '') +
          '</div></li>',
      )
      .join('') || '<li class="lu-muted">暂无处理记录</li>'
  );
}

export async function loadOverview(signal, accept = () => true) {
  const data = await api('overview', undefined, signal);
  if (!accept()) return;
  const set = (selector, html) => {
    const node = $(selector);
    setUiHtml(node, html);
  };
  set(
    '#luCoverage',
    ['Name', 'Overview', 'Genres']
      .map((f) => {
        const row = data.fields.find((x) => x.field === f) || {};
        const done = row.translated || 0,
          den = row.denominator || 0;
        return (
          '<article class="lu-card"><h3>' +
          labels[f] +
          '</h3><span class="lu-stat">' +
          done +
          ' <small>/ ' +
          den +
          '</small></span><div class="lu-bar"><span style="width:' +
          (den ? Math.min(100, (done / den) * 100) : 0) +
          '%"></span></div><small>缺译 ' +
          (row.missing || 0) +
          ' · 待显示 ' +
          (row.pending_display || 0) +
          ' · 保留原文 ' +
          (row.kept_original || 0) +
          (row.empty ? ' · 空原文 ' + row.empty : '') +
          '</small></article>'
        );
      })
      .join(''),
  );
  set('#luBatches', recentBatches(data.batches));
  set(
    '#luIssues',
    data.issues
      .map(
        (x) =>
          '<li><button class="lu-link" data-issue-kind="' +
          escape(x.issue_kind) +
          '" data-issue-code="' +
          escape(x.error_code || '') +
          '">' +
          escape(name(x.issue_kind)) +
          ' · ' +
          escape(name(x.error_code)) +
          ' <span class="lu-pill">' +
          x.count +
          '</span> · 查看问题</button></li>',
      )
      .join('') || '<li class="lu-muted">暂无需要处理的问题</li>',
  );
  return data;
}

export async function makePreview(ids, force = false) {
  if (fillLoading) return;
  if (ids && ids.length === 0) throw new Error('请先选择影片。');
  fillLoading = true;
  preview = null;
  const buttons = ['#luFillAll', '#luFillSelected', '#luForceSelected'].map($);
  buttons.forEach((x) => (x.disabled = true));
  $('#luCommit').textContent = '开始处理';
  $('#luCommit').disabled = true;
  status('', false, '#luPreviewStatus');
  $('#luPreviewText').textContent = force ? '正在检查重译范围…' : '正在检查缺译，仅检查缺失字段…';
  if (!$('#luPreview').open) $('#luPreview').showModal();
  try {
    await loadState();
    const data = await api('preview', {
      settingsRevision: state.settings.revision,
      mediaIds: ids,
      force,
    });
    if (!$('#luPreview').open) return;
    preview = { ...data, commandId: uid() };
    $('#luPreviewText').textContent = force
      ? '明确重译 ' + data.count + ' 个字段。本次将重新请求模型并取代对应字段的当前控制。'
      : '仅补缺译 ' +
        data.count +
        ' 个字段；跳过 ' +
        (data.skipped || 0) +
        ' 个已有译文、空原文、保留或保护及处理中字段。' +
        (data.sourceChanged
          ? ' ' + data.sourceChanged + ' 个原文已变化，等待后台观察后再补齐。'
          : '') +
        (data.count ? '此前失败且仍缺译的字段会再次尝试。' : '当前没有可补齐的缺译字段。');
    $('#luCommit').disabled = !data.count;
  } catch (error) {
    status(error.message, true, '#luPreviewStatus');
  } finally {
    fillLoading = false;
    buttons.forEach((x) => (x.disabled = false));
  }
}
