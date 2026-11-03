export function workView() {
  return workView.value || (workView.value = { scope: null, rows: new Map(), consents: new Map() });
}

export function workVersion(row) {
  return (row.updated_utc || '') + '|' + row.state + '|' + row.attempt_count;
}

export async function loadWork(preserveOpen = false, signal, accept = () => true) {
  const ticket = beginRead('work', signal),
    scope = JSON.stringify([workBatch, workTarget, workOffset]),
    view = workView();
  const current = () =>
    ticket.current() && accept() && scope === JSON.stringify([workBatch, workTarget, workOffset]);
  try {
    const q = new URLSearchParams({ offset: workOffset, limit: 30 });
    if (workBatch) q.set('batchId', workBatch);
    if (workTarget) q.set('workId', workTarget);
    const rows = await api('work?' + q, undefined, ticket.signal);
    if (!current() || (preserveOpen && !$('#luWorkDialog').open)) return false;
    if (view.scope !== scope || !preserveOpen) {
      view.rows.clear();
      view.consents.clear();
    }
    view.scope = scope;
    const dialog = $('#luWorkDialog'),
      anchor = preserveOpen
        ? $$('#luWorkList details').find((el) => {
            if (!el.open || !el.getBoundingClientRect || !dialog.getBoundingClientRect)
              return false;
            const r = el.getBoundingClientRect(),
              v = dialog.getBoundingClientRect();
            return r.bottom > v.top && r.top < v.bottom;
          })
        : null,
      anchorTop = anchor?.getBoundingClientRect().top;
    const retained = preserveOpen
      ? new Map(
          $$('#luWorkList details').map((el) => [
            (el.dataset.workText ? 'text/' : 'attempt/') +
              (el.dataset.workText || el.dataset.attempt),
            el,
          ]),
        )
      : new Map();
    const pins = preserveOpen
      ? new Set(
          [...retained.values()]
            .filter((el) => el.open)
            .map((el) => el.dataset.workText || el.dataset.attempt),
        )
      : new Set();
    for (const id of view.consents.keys()) pins.add(id);
    const display = [...rows];
    for (const id of pins) {
      if (display.some((w) => w.id === id) || !view.rows.has(id)) continue;
      const exact = new URLSearchParams({ workId: id, offset: 0, limit: 1 });
      if (workBatch) exact.set('batchId', workBatch);
      const extra = await api('work?' + exact, undefined, ticket.signal);
      if (!current()) return false;
      if (extra.length) display.push({ ...extra[0], pinned: true });
      else view.consents.delete(id);
    }
    for (const row of display) {
      if (
        view.consents.get(row.id) !== workVersion(row) ||
        !['failed', 'paused'].includes(row.state)
      )
        view.consents.delete(row.id);
    }
    if (!current()) return false;
    view.rows = new Map(display.map((w) => [w.id, w]));
    $('#luWorkList').innerHTML =
      display
        .map(
          (w) =>
            '<li data-work-id="' +
            escape(w.id) +
            '">' +
            (w.pinned ? '<small class="lu-muted">正在查看 · 已移出当前分页</small>' : '') +
            '<div class="lu-row"><div class="lu-grow"><strong><span class="lu-data">' +
            escape(w.movie_name || '共享词条') +
            '</span> · ' +
            escape(name(w.field)) +
            ' · ' +
            escape(name(w.state)) +
            '</strong><div class="lu-muted">' +
            escape(name(w.wait_reason || w.step)) +
            (w.error_code ? ' · ' + escape(name(w.error_code)) : '') +
            '</div><small>原文：<span class="lu-data">' +
            escape(w.original) +
            (w.original_truncated ? '…' : '') +
            '</span></small>' +
            (w.result_text
              ? '<small>已保存：<span class="lu-data">' +
                escape(w.result_text) +
                (w.result_truncated ? '…' : '') +
                '</span></small>'
              : '') +
            '' +
            (w.state === 'running'
              ? '<small>当前阶段 ' +
                Math.max(0, Date.now() - Date.parse(w.current_stage_started_utc)) +
                ' ms</small>'
              : '') +
            '<small>请求 ' +
            w.request_ms +
            ' ms · 保存 ' +
            (w.save_timing_measured ? w.save_ms + ' ms' : '未计量') +
            ' · 显示 ' +
            w.display_ms +
            ' ms</small></div>' +
            (w.media_id ? '<button data-movie="' + escape(w.media_id) + '">影片</button>' : '') +
            (['failed', 'paused'].includes(w.state)
              ? '<button data-retry="' + escape(w.id) + '">重试失败项</button>'
              : '') +
            (view.consents.get(w.id) === workVersion(w)
              ? '<button data-consent="' +
                escape(w.id) +
                '" data-consent-version="' +
                escape(workVersion(w)) +
                '">接受可能重复计费并重试</button><button data-cancel-consent="' +
                escape(w.id) +
                '">取消确认</button>'
              : '') +
            '</div>' +
            (w.original_truncated || w.result_truncated
              ? '<details data-work-text="' +
                escape(w.id) +
                '" data-work-state="' +
                escape(w.state) +
                '" data-work-version="' +
                escape(w.updated_utc || '') +
                '|' +
                escape(w.state) +
                '"><summary>完整原文与结果</summary><div class="lu-read"><strong>原文</strong><pre class="lu-data" data-full-original style="white-space:pre-wrap;overflow-wrap:anywhere"></pre><strong>已保存</strong><pre class="lu-data" data-full-result style="white-space:pre-wrap;overflow-wrap:anywhere"></pre></div></details>'
              : '') +
            '<details data-attempt="' +
            escape(w.id) +
            '" data-work-state="' +
            escape(w.state) +
            '" data-work-version="' +
            escape(w.updated_utc || '') +
            '|' +
            escape(w.state) +
            '|' +
            w.attempt_count +
            '"><summary>请求记录（' +
            w.attempt_count +
            '）</summary><div class="lu-read"></div></details></li>',
        )
        .join('') || '<li class="lu-muted">暂无记录</li>';
    $('#luWorkPrev').disabled = !workOffset;
    $('#luWorkNext').disabled = rows.length < 30;
    for (const el of $$('#luWorkList details')) {
      const prior = retained.get(
        (el.dataset.workText ? 'text/' : 'attempt/') + (el.dataset.workText || el.dataset.attempt),
      );
      if (prior) {
        if (prior.dataset.workVersion !== el.dataset.workVersion) delete prior.dataset.loaded;
        prior.dataset.workVersion = el.dataset.workVersion;
        prior.dataset.workState = el.dataset.workState;
        prior.querySelector('summary').textContent = el.querySelector('summary').textContent;
        el.replaceWith(prior);
      }
    }
    if (anchor?.isConnected) dialog.scrollTop += anchor.getBoundingClientRect().top - anchorTop;
    for (const el of $$('[data-work-text]')) {
      if (!el.dataset.bound) {
        el.dataset.bound = 'true';
        el.addEventListener('toggle', () => {
          if (el.open) run(() => loadWorkText(el), '#luWorkStatus');
        });
      }
      if (el.open) await loadWorkText(el, ticket.signal);
    }
    for (const el of $$('[data-attempt]')) {
      if (!el.dataset.bound) {
        el.dataset.bound = 'true';
        el.addEventListener('toggle', () => {
          if (el.open) run(() => loadWorkAttempts(el), '#luWorkStatus');
        });
      }
      if (el.open) await loadWorkAttempts(el, ticket.signal);
    }
    return current();
  } finally {
    ticket.finish();
  }
}

export async function loadWorkText(el, signal) {
  if (!el.open || el.dataset.loaded || el.dataset.loading) return;
  el.dataset.loading = 'true';
  const version = el.dataset.workVersion;
  try {
    const data = await api('work/' + encodeURIComponent(el.dataset.workText), undefined, signal);
    if (el.dataset.workVersion !== version) return;
    el.querySelector('[data-full-original]').textContent = data.original || '';
    el.querySelector('[data-full-result]').textContent = data.result_text || '';
    if (!['queued', 'running', 'waiting'].includes(el.dataset.workState))
      el.dataset.loaded = 'true';
  } finally {
    delete el.dataset.loading;
  }
}

export async function loadWorkAttempts(el, signal) {
  if (!el.open || el.dataset.loaded || el.dataset.loading) return;
  el.dataset.loading = 'true';
  const version = el.dataset.workVersion;
  try {
    const data = await api(
      'attempts?workId=' + encodeURIComponent(el.dataset.attempt),
      undefined,
      signal,
    );
    if (el.dataset.workVersion !== version) return;
    el.querySelector('div').textContent = attemptText(data);
    if (!['queued', 'running', 'waiting'].includes(el.dataset.workState))
      el.dataset.loaded = 'true';
  } finally {
    delete el.dataset.loading;
  }
}

export async function retryWork(id, acceptUnknown = false, expectedVersion = null) {
  const result = await api('retry', { workId: id, acceptUnknown, expectedVersion });
  if (result.reused) {
    if (workTarget === id) workTarget = result.workId;
    status('已有相同任务，已复用。', false, '#luWorkStatus');
  }
  return result;
}

export async function retryWithConsent(id, accepted = false, version = null) {
  const view = workView();
  if (
    accepted &&
    (view.consents.get(id) !== version || workVersion(view.rows.get(id) || {}) !== version)
  )
    throw new Error('任务已变化，请重新核对后重试。');
  try {
    await retryWork(id, accepted, accepted ? version : null);
    view.consents.delete(id);
    await loadWork(true);
  } catch (error) {
    if (error.code === 'UnknownOutcomeConsentRequired') {
      const row = view.rows.get(id);
      if (row) {
        view.consents.set(id, workVersion(row));
        await loadWork(true);
      }
    } else if (error.code === 'RevisionConflict') {
      view.consents.delete(id);
      await loadWork(true);
    }
    throw error;
  }
}

export async function cancelWorkConsent(id) {
  workView().consents.delete(id);
  await loadWork(true);
}

export async function openWork(batch, target = null) {
  workBatch = batch;
  workTarget = target;
  workOffset = 0;
  if (!(await loadWork())) return;
  if (!$('#luWorkDialog').open) $('#luWorkDialog').showModal();
}

export async function openIssues(kind, code) {
  issueKind = kind;
  issueCode = code;
  issueOffset = 0;
  $('#luIssueTitle').textContent = name(kind) + ' · ' + name(code);
  $('#luIssueList').innerHTML = '';
  $('#luIssueDialog').showModal();
  await loadIssues();
}

export async function loadIssues() {
  const ticket = beginRead('issues'),
    identity = JSON.stringify([issueKind, issueCode, issueOffset]);
  try {
    const data = await api(
      'issues?' +
        new URLSearchParams({
          kind: issueKind,
          errorCode: issueCode,
          offset: issueOffset,
          limit: 30,
        }),
      undefined,
      ticket.signal,
    );
    if (!ticket.current() || identity !== JSON.stringify([issueKind, issueCode, issueOffset]))
      return;
    $('#luIssueList').innerHTML =
      data.items
        .map(
          (x) =>
            '<li><div class="lu-row"><div class="lu-grow"><span class="lu-data">' +
            escape(x.name) +
            '</span><small>' +
            escape(name(x.field)) +
            ' · ' +
            escape(name(x.error_code)) +
            '</small></div>' +
            (x.target_kind === 'term'
              ? '<button data-issue-work="' + escape(x.work_id) + '">查看词条任务</button>'
              : '<button data-issue-movie="' +
                escape(x.media_id) +
                '" data-issue-field="' +
                escape(x.field) +
                '">查看影片</button>') +
            '</div></li>',
        )
        .join('') || '<li class="lu-muted">问题已解决或目前没有对应项目</li>';
    $('#luIssuePrev').disabled = !issueOffset;
    $('#luIssueNext').disabled = issueOffset + data.items.length >= data.total;
    $('#luIssuePaging').textContent =
      (data.total ? issueOffset + 1 : 0) +
      '–' +
      (issueOffset + data.items.length) +
      ' / ' +
      data.total;
  } finally {
    ticket.finish();
  }
}

export function attemptText(data) {
  return (
    data
      .map((x) => {
        let d;
        try {
          d = normalize(JSON.parse(x.diagnostics_json || '{}'));
        } catch (_) {
          d = {};
        }
        const v = d.validation;
        return [
          name(x.state),
          d.code ? name(d.code) : '',
          d.finishReason ? 'finish_reason: ' + d.finishReason : '',
          v ? '校验详情：' + name(v.code) + ' · ' + JSON.stringify(v) : '',
        ]
          .filter(Boolean)
          .join(' · ');
      })
      .join('\n') +
    '\n\n' +
    JSON.stringify(data, null, 2)
  );
}
