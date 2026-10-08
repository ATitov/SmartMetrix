'use strict';
const $ = id => document.getElementById(id);
const state = { scopes: [], runs: [], current: null, generation: 0, controller: null };
const phases = { Unknown: 'Нет достоверных данных', Waiting: 'Ожидание', Digging: 'Копание', LoadedSwing: 'Поворот с грузом', Unloading: 'Разгрузка', Returning: 'Возврат' };
const statuses = { Completed: 'Завершён', Interrupted: 'Прерван', Incomplete: 'Не завершён' };
const reasons = { 'ordered-phases': 'Все рабочие фазы подтверждены', 'telemetry-gap': 'Обрыв телеметрии', 'end-of-recording': 'Конец записи', 'unexpected-phase': 'Нарушен порядок фаз', 'signal-quality': 'Недостоверные сигналы', 'missing-engine-state': 'Нет состояния двигателя', 'missing-travel-signal': 'Нет скорости движения', 'missing-work-signals': 'Недостаточно рабочих сигналов', 'conflicting-work-signals': 'Противоречивые сигналы', 'machine-travelling': 'Перемещение экскаватора', 'stationary-loaded': 'Неподвижный ковш с грузом', 'idle-reason-unknown': 'Причина ожидания неизвестна', 'engine-off': 'Двигатель выключен', 'tool-engaged': 'Работа инструмента', 'swing-with-load': 'Поворот с грузом', 'discharge-active': 'Разгрузка', 'swing-without-load': 'Поворот без груза', 'outside-recording': 'За пределами записи' };
const esc = value => { const node = document.createElement('span'); node.textContent = String(value ?? '—'); return node.innerHTML; };
const duration = seconds => { const rounded = Math.round(seconds); return `${Math.floor(rounded / 60)} мин ${rounded % 60} с`; };
const percent = fraction => `${(fraction * 100).toLocaleString('ru-RU', { maximumFractionDigits: 1 })}%`;
function time(value, anchor = value) {
  const offset = anchor?.match(/([+-])(\d{2}):(\d{2})$/);
  const minutes = offset ? (Number(offset[2]) * 60 + Number(offset[3])) * (offset[1] === '+' ? 1 : -1) : 0;
  return new Date(new Date(value).getTime() + minutes * 60000).toLocaleString('ru-RU', { timeZone: 'UTC' });
}
function zone(value) { return `UTC${value?.match(/[+-]\d{2}:\d{2}$/)?.[0] || '+00:00'}`; }
async function request(path, signal) {
  const response = await fetch(path, { credentials: 'same-origin', signal, cache: 'no-store' });
  if (response.status === 401) {
    location.assign('/login/?return=%2Fexcavation%2F');
    throw new Error('Требуется вход в систему');
  }
  if (!response.ok) {
    const codes = { 403: 'Нет доступа к выбранной области', 404: 'Отчёт не найден или относится к другой машине', 409: 'Отчёт не настроен или повреждён. Проверьте артефакты на сервере.' };
    throw new Error(codes[response.status] || `Не удалось загрузить данные (HTTP ${response.status})`);
  }
  return response;
}
function base() { return `/api/v1/scopes/${encodeURIComponent($('scopeSelector').value)}/excavation/runs`; }
function begin() {
  state.controller?.abort();
  state.controller = new AbortController();
  const generation = ++state.generation;
  state.current = null;
  $('report').hidden = true;
  $('exportCsv').disabled = true;
  $('error').hidden = $('empty').hidden = true;
  $('cycleDialog').close();
  $('loading').hidden = false;
  return { generation, signal: state.controller.signal };
}
function failure(error, generation) {
  if (generation !== state.generation || error.name === 'AbortError') return;
  $('error').textContent = error.message;
  $('error').hidden = false;
  $('loading').hidden = true;
}
async function loadRuns() {
  const previous = $('runSelector').value;
  const { generation, signal } = begin();
  $('runSelector').disabled = true;
  $('runSelector').innerHTML = '<option value="">Загрузка…</option>';
  $('storageWarning').hidden = true;
  $('loading').textContent = 'Загрузка сохранённых смен…';
  if (!$('scopeSelector').value) {
    $('loading').hidden = true;
    $('empty').textContent = 'У учётной записи нет доступных областей. Обратитесь к администратору.';
    $('empty').hidden = false;
    return;
  }
  try {
    const list = await (await request(base(), signal)).json();
    if (generation !== state.generation) return;
    state.runs = list.items || [];
    $('runSelector').innerHTML = state.runs.map(run => `<option value="${esc(run.runId)}">${esc(time(run.start))} · ${esc(zone(run.start))} · ${run.isSynthetic ? 'Синтетика' : 'Записанный поток'}</option>`).join('');
    $('runSelector').disabled = !state.runs.length;
    if (list.invalidCount || list.truncated) {
      $('storageWarning').textContent = `${list.invalidCount ? `Повреждённых отчётов: ${list.invalidCount}. Они исключены из списка. ` : ''}${list.truncated ? 'Список ограничен первыми 1000 каталогами.' : ''}`;
      $('storageWarning').hidden = false;
    }
    if (!state.runs.length) {
      const messages = { NotConfigured: 'Источник отчётов для этой области ещё не настроен.', Unavailable: 'Хранилище отчётов недоступно. Обратитесь к инженеру.' };
      $('empty').textContent = messages[list.state] || 'Сохранённых смен для этого экскаватора пока нет. Создайте replay-отчёт и подключите его каталог.';
      $('empty').hidden = false;
      $('loading').hidden = true;
      return;
    }
    if (state.runs.some(run => run.runId === previous)) $('runSelector').value = previous;
    await loadReport();
  } catch (error) { failure(error, generation); }
}
async function loadReport() {
  const runId = $('runSelector').value;
  if (!runId) return;
  const { generation, signal } = begin();
  $('loading').textContent = 'Загрузка отчёта смены…';
  try {
    const data = await (await request(`${base()}/${encodeURIComponent(runId)}/report`, signal)).json();
    if (generation !== state.generation) return;
    state.current = data;
    render(data);
    $('report').hidden = false;
    $('exportCsv').disabled = false;
    $('loading').hidden = true;
  } catch (error) { failure(error, generation); }
}
function render(data) {
  const report = data.report;
  $('dataMode').className = `mode-banner ${report.isSynthetic ? 'synthetic' : 'recorded'}`;
  $('dataMode').textContent = report.isSynthetic ? 'СИНТЕТИЧЕСКИЕ ДАННЫЕ · Проверка программной логики. Полевая точность не подтверждена.' : 'ЗАПИСАННЫЙ ПОТОК · Источники и качество приведены в отчёте. Полевая приёмка оценивается отдельно.';
  $('completed').textContent = report.completedCycles;
  $('interrupted').textContent = `${report.interruptedCycles} / ${report.incompleteCycles}`;
  $('digging').textContent = duration(report.phaseSeconds.Digging);
  $('coverage').textContent = percent(report.coverageFraction);
  $('unknown').textContent = `${duration(report.phaseSeconds.Unknown)} без достоверных данных`;
  $('shiftBounds').textContent = `${time(report.start)} — ${time(report.end, report.start)} · ${zone(report.start)}`;
  $('axisStart').textContent = time(report.start);
  $('axisEnd').textContent = time(report.end, report.start);
  const totalSeconds = (new Date(report.end) - new Date(report.start)) / 1000;
  $('phaseLegend').innerHTML = Object.entries(phases).map(([key, name]) => `<span class="legend-item"><i class="legend-dot phase-${key}" aria-hidden="true"></i>${name}</span>`).join('');
  $('phaseTotals').innerHTML = Object.entries(phases).map(([key, name]) => `<tr><td>${name}</td><td>${duration(report.phaseSeconds[key])}</td><td>${percent(report.phaseSeconds[key] / totalSeconds)}</td></tr>`).join('');
  const merged = [];
  for (const span of report.timeline) {
    const last = merged.at(-1);
    if (last && last.phase === span.phase && last.reason === span.reason && new Date(last.end).getTime() === new Date(span.start).getTime()) last.end = span.end;
    else merged.push({ ...span });
  }
  $('phaseTimeline').innerHTML = merged.map(span => {
    const width = Math.max(0, Math.min(100, (new Date(span.end) - new Date(span.start)) / 1000 / totalSeconds * 100));
    const key = Object.hasOwn(phases, span.phase) ? span.phase : 'Unknown';
    const title = `${phases[key]} · ${time(span.start, report.start)} — ${time(span.end, report.start)} · ${reasons[span.reason] || span.reason}`;
    return `<span class="phase-${key}" style="width:${width}%" title="${esc(title)}"></span>`;
  }).join('');
  $('phaseTimeline').setAttribute('aria-label', Object.entries(phases).map(([key, name]) => `${name}: ${duration(report.phaseSeconds[key])}`).join('; '));
  const pairs = [['Экскаватор', report.excavatorId], ['Источник', report.sourceId], ['Шкала времени', report.clockId], ['Алгоритм', report.algorithmVersion], ['Смещение смены', zone(report.start)], ['Прогон', data.runId], ['SHA-256 входа', data.inputSha256]];
  $('provenance').innerHTML = pairs.map(([key, value]) => `<dt>${key}</dt><dd>${esc(value)}</dd>`).join('');
  renderCycles();
}
function renderCycles() {
  if (!state.current) return;
  const report = state.current.report;
  const selected = $('cycleFilter').value;
  const cycles = report.cycles.filter(cycle => !selected || cycle.status === selected);
  $('cycleCount').textContent = `Показано ${cycles.length} из ${report.cycles.length}. Длительность полного цикла может выходить за границы смены.`;
  $('cycles').innerHTML = cycles.length ? cycles.map(cycle => {
    const boundary = new Date(cycle.start) < new Date(report.start) || new Date(cycle.end) >= new Date(report.end) ? ' · Пересекает границу смены' : '';
    return `<tr><td>${esc(time(cycle.start, report.start))}</td><td>${duration((new Date(cycle.end) - new Date(cycle.start)) / 1000)}</td><td><span class="pill ${cycle.status === 'Completed' ? 'state-ready' : 'state-warning'}">${esc(statuses[cycle.status] || cycle.status)}</span></td><td>${esc(reasons[cycle.reason] || cycle.reason)}${boundary}</td><td><button class="button" data-cycle="${esc(cycle.cycleId)}">Фазы</button></td></tr>`;
  }).join('') : '<tr><td colspan="5" class="empty">Циклов с выбранным статусом нет</td></tr>';
}
$('scopeSelector').addEventListener('change', () => { state.runs = []; $('runSelector').value = ''; loadRuns(); });
$('runSelector').addEventListener('change', loadReport);
$('refresh').addEventListener('click', loadRuns);
$('cycleFilter').addEventListener('change', renderCycles);
$('closeCycle').addEventListener('click', () => $('cycleDialog').close());
$('cycles').addEventListener('click', event => {
  const button = event.target.closest('[data-cycle]');
  if (!button || !state.current) return;
  const cycle = state.current.report.cycles.find(item => item.cycleId === button.dataset.cycle);
  if (!cycle) return;
  $('cycleIdentity').textContent = `${cycle.cycleId} · ${statuses[cycle.status]}`;
  const grouped = [];
  for (const phase of cycle.phases) {
    const last = grouped.at(-1);
    if (last && last.phase === phase.phase && last.reason === phase.reason) last.end = phase.end;
    else grouped.push({ ...phase });
  }
  $('cycleDetails').innerHTML = `<div class="cycle-details">${grouped.map(span => `<article class="cycle-phase"><b>${esc(phases[span.phase] || span.phase)}</b> · ${duration((new Date(span.end) - new Date(span.start)) / 1000)}<small>${esc(time(span.start, state.current.report.start))} — ${esc(time(span.end, state.current.report.start))}</small><small>${esc(reasons[span.reason] || span.reason)}</small></article>`).join('')}</div>`;
  $('cycleDialog').showModal();
});
$('exportCsv').addEventListener('click', async () => {
  if (!state.current) return;
  const generation = state.generation;
  const runId = state.current.runId;
  $('exportCsv').disabled = true;
  try {
    const response = await request(`${base()}/${encodeURIComponent(runId)}/csv`, state.controller.signal);
    const blob = await response.blob();
    if (generation !== state.generation) return;
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url; link.download = `shift-${runId.slice(0, 12)}.csv`;
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (error) { failure(error, generation); }
  finally { if (generation === state.generation && state.current) $('exportCsv').disabled = false; }
});
$('logout').addEventListener('click', async () => {
  try {
    const csrf = await (await request('/api/auth/csrf')).json();
    const response = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-Token': csrf.token } });
    if (!response.ok) throw new Error('Не удалось завершить сессию');
    location.assign('/login/');
  } catch (error) { failure(error, state.generation); }
});
async function initialize() {
  try {
    const identity = await (await request('/api/auth/me')).json();
    $('currentUser').textContent = identity.displayName || identity.username;
    const workstations = await (await request('/api/v1/workstations')).json();
    state.scopes = workstations.scopes || [];
    $('scopeSelector').innerHTML = state.scopes.map(scope => `<option value="${esc(scope.id)}">${esc(scope.siteId)} · ${esc(scope.excavatorId)}</option>`).join('');
    $('scopeSelector').disabled = !state.scopes.length;
    await loadRuns();
  } catch (error) { failure(error, state.generation); }
}
initialize();
