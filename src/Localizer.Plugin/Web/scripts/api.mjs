export const status = (text, error = false, target = '#luStatus', owner = 'operation') => {
  statusRevision++;
  statusOwners.set(target, owner);
  const el = $(target);
  el.textContent = text || '';
  el.classList.toggle('lu-error', error);
};

export const uid = () => crypto.randomUUID().replaceAll('-', '');

export const name = (v) => labels[v] || errors[v] || v || '—';

export const normalize = (v) =>
  Array.isArray(v)
    ? v.map(normalize)
    : v && typeof v === 'object'
      ? Object.fromEntries(
          Object.entries(v).map(([k, x]) => [k[0].toLowerCase() + k.slice(1), normalize(x)]),
        )
      : v;

export const escape = (v) =>
  String(v ?? '').replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c],
  );

export async function api(path, body, signal) {
  const headers = { accept: 'application/json' };
  ApiClient.setRequestHeaders(headers);
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(ApiClient.getUrl('MetadataLocalizer/unified/' + path), {
    method: body === undefined ? 'GET' : 'POST',
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  let data;
  try {
    data = normalize(await response.json());
  } catch (_) {
    data = {};
  }
  if (!response.ok) {
    const parameterNames = {
      MaxOutputTokens: '标题/分类输出上限',
      OverviewMaxOutputTokens: '简介输出上限',
      UseJsonResponseFormat: 'JSON 响应格式',
      TokenParameter: 'Token 参数',
      ReasoningEffort: '推理强度',
      Store: '保存请求',
      parameters: '连接参数',
    };
    const error = new Error(
      data.error === 'invalid_connection_parameter'
        ? '设置值无效：' + (parameterNames[data.parameter] || data.parameter)
        : errors[data.error] || data.error || '请求失败',
    );
    error.code = data.error;
    throw error;
  }
  return data;
}

export async function run(fn, target = '#luStatus') {
  invalidatePoll();
  try {
    status('', false, target);
    await fn();
  } catch (e) {
    if (e.name !== 'AbortError') status(e.message, true, target);
  }
}

export function beginRead(channel, signal) {
  const pending = beginRead.pending || (beginRead.pending = new Map());
  pending.get(channel)?.abort();
  const controller = new AbortController();
  pending.set(channel, controller);
  const abort = () => controller.abort();
  if (signal?.aborted) abort();
  else signal?.addEventListener('abort', abort, { once: true });
  return {
    signal: controller.signal,
    current: () => pending.get(channel) === controller && !controller.signal.aborted,
    finish: () => signal?.removeEventListener('abort', abort),
  };
}

export function cancelRead(channel) {
  const pending = beginRead.pending;
  if (channel) pending?.get(channel)?.abort();
  else for (const controller of pending?.values() || []) controller.abort();
}
