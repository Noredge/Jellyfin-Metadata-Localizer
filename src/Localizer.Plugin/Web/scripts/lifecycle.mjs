export function invalidatePoll() {
  pollEpoch++;
  pollAbort?.abort();
  poll.next = 0;
  poll.delay = 2000;
  poll.last = null;
}

export async function poll(scheduled = false) {
  if (polling || !pageVisible || document.hidden || (scheduled && Date.now() < (poll.next || 0)))
    return;
  const overview =
    active === 'overview' && !$('#luEditor').open && !$('#luWorkDialog').open && state?.ready;
  const work = $('#luWorkDialog').open;
  if (!overview && !work) return;
  const controller = new AbortController(),
    epoch = pollEpoch,
    revision = statusRevision,
    target = work ? '#luWorkStatus' : '#luStatus';
  const current = () =>
    pageVisible &&
    !document.hidden &&
    !controller.signal.aborted &&
    epoch === pollEpoch &&
    revision === statusRevision;
  polling = true;
  pollAbort = controller;
  try {
    let data, busy;
    if (overview) {
      await loadState(controller.signal, current);
      if (!current()) return;
      data = await loadOverview(controller.signal, current);
      busy = (data?.active || 0) > 0;
    } else {
      await loadWork(true, controller.signal, current);
      data = [...workView().rows.values()];
      busy = data.some((x) => ['queued', 'running', 'waiting'].includes(x.state));
    }
    if (!current()) return;
    const version = JSON.stringify([
      target,
      overview ? state.settings : null,
      overview ? state.libraries : null,
      overview ? state.health?.state : null,
      overview ? state.health?.error : null,
      data,
    ]);
    poll.delay = !busy && poll.last === version ? Math.min((poll.delay || 2000) * 2, 10000) : 2000;
    poll.last = version;
    poll.next = Date.now() + poll.delay;
    if (
      current() &&
      ((overview && state.health?.error) ||
        statusOwners.get(target) === 'poll' ||
        !$(target).textContent)
    )
      status(
        overview && state.health?.error ? name(state.health.error) : '',
        !!(overview && state.health?.error),
        target,
        'poll',
      );
  } catch (error) {
    if (error.name !== 'AbortError' && current()) {
      poll.delay = 2000;
      poll.next = Date.now() + 2000;
      status(error.message, true, target, 'poll');
    }
  } finally {
    if (pollAbort === controller) pollAbort = null;
    polling = false;
  }
}

export async function start() {
  pageVisible = true;
  invalidatePoll();
  const epoch = ++startEpoch,
    revision = statusRevision;
  clearInterval(timer);
  await loadState(
    undefined,
    () => pageVisible && epoch === startEpoch && revision === statusRevision,
  );
  if (!pageVisible || epoch !== startEpoch) return;
  if (state?.ready) show(active);
  clearInterval(timer);
  timer = setInterval(() => poll(true), 2000);
}
