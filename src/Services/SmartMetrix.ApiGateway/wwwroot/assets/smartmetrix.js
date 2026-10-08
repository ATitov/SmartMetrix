const mode = document.body.dataset.mode;
const apiKey = document.querySelector('#apiKey');
const state = { status: null, measurements: [], audit: [], lastSuccess: null, refreshing: false };
state.scopes = [];
state.scopeId = null;
state.roles = [];
const labels = { Ready:'Готов', Degraded:'Ограниченно', Unavailable:'Недоступен', NotConfigured:'Не настроен', Requested:'Запрошено', Capturing:'Захват', QualityControl:'Контроль качества', Reconstructing:'Реконструкция глубины', Segmenting:'Сегментация', Persisting:'Сохранение', Analysing:'Анализ блоков', Georeferencing:'Геопривязка', Failed:'Ошибка', Rejected:'Отклонено', Completed:'Завершено' };

const esc = value => { const node = document.createElement('span'); node.textContent = value ?? '—'; return node.innerHTML; };
const date = value => value ? new Date(value).toLocaleString('ru-RU') : '—';
const label = value => labels[value] || value || 'Неизвестно';
const cssState = value => value === 'Ready' || value === 'Completed' ? 'state-ready' : value === 'Failed' || value === 'Rejected' || value === 'Cancelled' ? 'state-danger' : value === 'Degraded' || value === 'Unavailable' ? 'state-warning' : 'state-muted';
const isActive = value => ['Requested','Capturing','QualityControl','Reconstructing','Segmenting','Analysing','Georeferencing','Persisting'].includes(value);
const isProblem = value => ['Failed','Rejected','Degraded','Unavailable'].includes(value);

async function api(path, options = {}) {
  if (state.scoped && !state.scopeId && !path.startsWith('/auth/') && !path.startsWith('/admin/') && path !== '/workstations')
    throw new Error('У учётной записи нет доступных областей измерений');
  let base = path.startsWith('/auth/') || path.startsWith('/admin/') ? '/api' : '/api/operator';
  if (state.scopeId && !path.startsWith('/auth/') && !path.startsWith('/admin/')) {
    base = `/api/v1/scopes/${encodeURIComponent(state.scopeId)}`;
    if (path === '/status') path = '/operator/status';
    else if (path === '/audit') path = '/engineer/audit';
    else if (path.startsWith('/engineering/system') || path.startsWith('/engineering/logs')) {
      base = '/api/v1'; path = path.replace('/engineering/', '/engineer/');
    } else if (path.startsWith('/engineering/config')) path = path.replace('/engineering/', '/engineer/');
    else if (options.method === 'POST' && /\/measurements\/[^/]+\/(cancel|retry)$/.test(path)) path = '/engineer' + path;
  }
  if (path === '/workstations') base = '/api/v1';
  const headers = { ...options.headers, 'Content-Type':'application/json' };
  if (apiKey?.value) headers['X-API-Key'] = apiKey.value;
  if (!apiKey?.value && options.method && !['GET','HEAD'].includes(options.method.toUpperCase())) {
    const csrf = await fetch('/api/auth/csrf', { credentials:'same-origin' });
    if (!csrf.ok) throw new Error('Не удалось получить токен сессии');
    headers['X-CSRF-Token'] = (await csrf.json()).token;
  }
  const response = await fetch(base + path, { ...options, credentials:'same-origin', headers });
  if (response.status === 401) {
    location.assign(`/login/?return=${encodeURIComponent(location.pathname)}`);
    throw new Error('Требуется вход в систему');
  }
  if (!response.ok) throw new Error((await response.text()) || `HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}

async function loadIdentity() {
  const identity = await api('/auth/me');
  state.roles = identity.roles || [identity.role];
  const workstations = await api('/workstations');
  state.scopes = workstations.scopes || [];
  state.scoped = workstations.scoped;
  const selector = document.querySelector('#scopeSelector');
  selector.innerHTML = state.scopes.map(scope => `<option value="${esc(scope.id)}">${esc(scope.siteId)} · ${esc(scope.excavatorId)}</option>`).join('');
  selector.hidden = state.scopes.length === 0;
  state.scopeId = state.scopes[0]?.id || null;
  updateScopeContext();
  const currentUser = document.querySelector('#currentUser');
  if (currentUser) currentUser.textContent = `${identity.displayName} · ${identity.role}`;
  const administration = document.querySelector('#userAdministration');
  if (administration) {
    administration.hidden = !state.roles.includes('administrator');
    if (!administration.hidden) await loadUsers();
  }
  if (state.scoped && state.scopes.length === 0) throw new Error('У учётной записи нет доступных областей измерений');
}

function updateScopeContext() {
  const scope = state.scopes.find(item => item.id === state.scopeId);
  const form = document.querySelector('#startForm');
  if (form && scope) {
    form.elements.excavatorId.value = scope.excavatorId;
    form.elements.coordinateSystemId.value = scope.coordinateSystemId;
    form.elements.excavatorId.readOnly = form.elements.coordinateSystemId.readOnly = true;
    delete form.dataset.commandId; delete form.dataset.measurementId;
  }
  document.querySelector('#detailDialog')?.close();
  document.querySelector('#commandDialog')?.close();
  document.querySelector('#startDialog')?.close();
}

document.querySelector('#scopeSelector').addEventListener('change', async event => {
  if (state.refreshing || state.applyingSettings) return;
  state.scopeId = event.target.value;
  state.measurements = []; state.audit = []; state.status = null; state.lastSuccess = null;
  updateScopeContext();
  await refresh();
  if (mode === 'engineer') await loadEngineeringTools();
});

async function loadUsers() {
  const users = await api('/admin/users');
  const body = document.querySelector('#users');
  if (!body) return;
  body.innerHTML = users.map(user => `<tr><td><b>${esc(user.username)}</b><br><small>${esc(user.displayName)}</small></td><td>${esc(user.role)}</td><td>${user.enabled ? 'Активен' : 'Отключен'}${user.lockedUntil ? '<br><small>Заблокирован</small>' : ''}</td><td><button class="button" data-user-toggle="${esc(user.username)}" data-enabled="${!user.enabled}">${user.enabled ? 'Отключить' : 'Включить'}</button><button class="button" data-user-password="${esc(user.username)}">Сменить пароль</button></td></tr>`).join('');
}

function toast(message, isError = false) {
  const root = document.querySelector('#toast');
  root.textContent = message;
  root.className = `toast show${isError ? ' error-toast' : ''}`;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => root.className = 'toast', 3500);
}

function renderStatus(status) {
  if (status.checks) state.status = status = { ...status, configured:status.checks.length > 0 && status.checks.every(check => check.state !== 'NotConfigured'), components:status.checks.map(check => ({ ...check, kind:check.name })), checkedAt:status.checkedAt || status.checks.map(check=>check.checkedAt).filter(Boolean).sort()[0] };
  const root = document.querySelector('#systemState');
  root.innerHTML = `<div class="status-line ${cssState(status.state)}"><span class="dot"></span><b>${esc(label(status.state))}</b></div><div class="metric">${!status.configured ? 'Требуется настройка' : status.state === 'Ready' ? 'Сервисы доступны' : 'Есть недоступные компоненты'}</div><div class="muted">Проверено ${date(status.checkedAt)}${status.triggerConditionsChecked === false ? ' · Условия съёмки не проверены' : ''}</div>`;
  document.querySelector('#activeMeasurement').textContent = status.activeMeasurementId || (status.checks ? 'Не предоставлено API' : 'Нет активного измерения');
  document.querySelector('#components').innerHTML = (status.components || []).map(component => `<article class="card component"><div class="caption">${esc(component.kind)}</div><h3>${esc(component.name)}</h3><div class="status-line ${cssState(component.state)}"><span class="dot"></span>${esc(label(component.state))}</div><p class="muted">${esc(component.detail || (component.state === 'Ready' ? 'Сервис отвечает на проверку готовности' : 'Готовность не подтверждена'))}</p></article>`).join('');
  const start = document.querySelector('#start');
  if (start) start.disabled = status.state !== 'Ready' || (state.scoped && !state.scopeId) || !state.roles.some(role => ['operator','engineer'].includes(role));
  if (mode === 'engineer') renderConfiguration(status.components || []);
}

function renderKpis() {
  const components = state.status?.components || [];
  const items = mode === 'engineer'
    ? [['Компонентов готово', components.filter(x => x.state === 'Ready').length], ['Требуют внимания', components.filter(x => x.state !== 'Ready').length], ['Ошибок измерений', state.measurements.filter(x => isProblem(x.status)).length], ['Записей аудита', state.audit.length]]
    : [['Всего измерений', state.measurements.length], ['В работе', state.measurements.filter(x => isActive(x.status)).length], ['Завершено', state.measurements.filter(x => x.status === 'Completed').length], ['Требуют внимания', state.measurements.filter(x => isProblem(x.status)).length]];
  document.querySelector('#kpis').innerHTML = items.map(([name,value]) => `<article class="card kpi"><div class="caption">${name}</div><div class="kpi-value">${value}</div></article>`).join('');
}

function filteredMeasurements() {
  const query = document.querySelector('#measurementSearch').value.trim().toLowerCase();
  const status = document.querySelector('#statusFilter').value;
  return state.measurements.filter(item => (!status || item.status === status) && (!query || `${item.id} ${item.status}`.toLowerCase().includes(query)));
}

function populateStatuses() {
  const select = document.querySelector('#statusFilter');
  const selected = select.value;
  const values = [...new Set(state.measurements.map(item => item.status).filter(Boolean))].sort();
  select.innerHTML = '<option value="">Все</option>' + values.map(value => `<option value="${esc(value)}">${esc(label(value))}</option>`).join('');
  if (values.includes(selected)) select.value = selected;
}

function renderMeasurements() {
  const items = filteredMeasurements();
  document.querySelector('#resultCount').textContent = `Показано ${items.length} из ${state.measurements.length}`;
  const body = document.querySelector('#measurements');
  body.innerHTML = items.length ? items.map(item => {
    const actions = mode !== 'engineer' ? '' : `${['Failed','Rejected'].includes(item.status) ? `<button class="button" data-retry="${esc(item.id)}" data-version="${item.version ?? 0}">Повторить</button>` : ''} ${isActive(item.status) ? `<button class="button danger" data-cancel="${esc(item.id)}" data-version="${item.version ?? 0}">Отменить</button>` : ''}`;
    const result = item.d50 == null && item.d80 == null ? '<span class="muted">Нет результата</span>' : `${item.d50 ?? '—'} / ${item.d80 ?? '—'}`;
    return `<tr><td><button class="link" data-detail="${esc(item.id)}">${esc(String(item.id).slice(0,8))}…</button><br><small class="muted">${esc(item.excavatorId)}</small>${item.isTestData === true ? '<br><strong class="state-warning">Тестовые данные</strong>' : item.isTestData !== false ? '<br><span class="state-warning">Происхождение не подтверждено</span>' : ''}</td><td><span class="pill ${cssState(item.status)}">${esc(label(item.status))}</span></td><td>${date(item.updatedAt)}</td><td>${result}</td><td>${item.confidence == null ? '—' : Math.round(item.confidence * 100) + '%'}</td><td>${actions}</td></tr>`;
  }).join('') : '<tr><td colspan="6" class="empty">Измерений пока нет. Создайте первое измерение кнопкой выше.</td></tr>';
}

function renderAudit() {
  const root = document.querySelector('#audit');
  const filter = document.querySelector('#auditFilter')?.value || '';
  const rows = state.audit.filter(item => !filter || (filter === 'ok' ? item.succeeded : !item.succeeded)).slice().reverse().slice(0, 30);
  document.querySelector('#auditCount').textContent = `Показано ${rows.length} из ${state.audit.length}`;
  root.innerHTML = rows.length ? rows.map(item => `<div class="audit-item"><span class="muted">${date(item.occurredAt)}</span><span><b>${esc(item.action)}</b><br><small>${esc(item.actor)} · ${esc(item.reason)}</small></span><span class="${item.succeeded ? 'state-ready' : 'state-danger'}">${item.succeeded ? 'OK' : 'Ошибка'}</span></div>`).join('') : '<div class="empty">Записей по выбранному фильтру нет</div>';
}

function renderConfiguration(components) {
  const root = document.querySelector('#configChecks');
  const checks = components.map(component => ({ name: component.name, detail: component.kind, ready: component.state === 'Ready' }));
  checks.push({ name:'Инженерный доступ', detail:'Права текущей сессии', ready:state.roles.includes('engineer') });
  root.innerHTML = checks.map(check => `<div class="config-row"><span class="status-line ${check.ready ? 'state-ready' : 'state-warning'}"><span class="dot"></span></span><div><b>${esc(check.name)}</b><small>${esc(check.detail)} · ${check.ready ? 'готово' : 'проверьте настройку'}</small></div></div>`).join('');
}

function renderResultVisual(item) {
  const root = document.querySelector('#resultVisual');
  if (!root) return;
  if (item.d50 == null) { root.innerHTML = `${item.isTestData === true ? '<div class="warning">Тестовые данные: не использовать для производственных решений.</div>' : ''}<div class="empty">Расчётный результат для этого измерения отсутствует</div>`; return; }
  const values = [['D10',item.d10],['D20',item.d20],['D50',item.d50],['D80',item.d80],['D90',item.d90],['D95',item.d95]];
  const shownValues = values.filter(([,value]) => value != null);
  const maximum = Math.max(...values.map(([,value]) => value || 0), 1);
  root.innerHTML = `<div class="result-grid"><div class="result-cell"><span class="muted">Блоков</span><b>${item.blockCount ?? '—'}</b></div><div class="result-cell"><span class="muted">Confidence</span><b>${item.confidence == null ? '—' : Math.round(item.confidence*100)+'%'}</b></div><div class="result-cell"><span class="muted">Покрытие</span><b>${item.coverage == null ? '—' : Math.round(item.coverage*100)+'%'}</b></div><div class="result-cell"><span class="muted">Негабарит</span><b>${item.oversizeFraction == null ? '—' : Math.round(item.oversizeFraction*100)+'%'}</b></div></div><div class="histogram">${shownValues.map(([name,value]) => `<div class="hist-bar" style="height:${Math.max(5,(value||0)/maximum*100)}%"><span>${value ?? '—'}</span><small>${name}</small></div>`).join('')}</div>${item.isTestData ? '<div class="warning">Тестовые данные: не использовать для производственных решений.</div>' : ''}`;
}

function download(name, content, type) {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); URL.revokeObjectURL(url);
}

function renderPipeline(item) {
  const root = document.querySelector('#pipelineStages');
  if (!root) return;
  const names = { calibration:'Калибровка', capture:'Кадры', pose:'Положение при съёмке', quality:'Контроль качества', depth:'Глубина', segmentation:'Сегментация', analysis:'Анализ блоков', georeference:'Геопривязка', result:'Итоговый результат' };
  root.innerHTML = `${item.failureReason ? `<p class="warning">${esc(item.failureReason)}</p>` : ''}${item.pipeline ? `<h3>Этапы обработки</h3><p class="muted">${item.pipeline.activeStage ? 'Текущий этап: '+esc(names[item.pipeline.activeStage] || item.pipeline.activeStage) : 'Сохранённые результаты'}</p>${(item.pipeline.stages || []).map(stage => `<div class="config-row"><span>${esc(names[stage.name] || stage.name)} · ${date(stage.completedAt)}</span><button class="button" data-stage="${esc(stage.name)}" data-measurement="${esc(item.id)}">Скачать JSON</button></div>`).join('')}` : ''}`;
}

document.addEventListener('click', async event => {
  const button = event.target.closest('[data-stage]');
  if (!button) return;
  button.disabled = true;
  try {
    const value = await api(`/measurements/${button.dataset.measurement}/stages/${button.dataset.stage}`);
    download(`${button.dataset.measurement}-${button.dataset.stage}.json`, JSON.stringify(value, null, 2), 'application/json');
  } catch (error) { toast(error.message, true); } finally { button.disabled = false; }
});

async function loadEngineeringTools() {
  if (mode !== 'engineer') return;
  try {
    const requestedScope = state.scopeId;
    const [system, config] = await Promise.all([api('/engineering/system'), api('/engineering/config')]);
    if (requestedScope !== state.scopeId) return;
    state.config = config;
    const drive = system.drives?.[0];
    document.querySelector('#systemMetrics').innerHTML = [['Компьютер',system.machine],['ОС',system.os],['Процессоры',system.processors],['Память процесса',Math.round(system.processMemoryBytes/1048576)+' МБ'],['Диск',drive ? `${drive.usedPercent}% занято` : '—'],['.NET',system.runtime]].map(([name,value]) => `<div class="config-row"><div><b>${esc(name)}</b><small>${esc(value)}</small></div></div>`).join('');
    if (state.scopeId) {
      document.querySelector('#runtimeConfig').innerHTML = Object.entries(config).map(([service,snapshot]) =>
        `<fieldset><legend>${esc(service)} · версия ${esc(snapshot.revision || 'unknown')}</legend>${snapshot.error ? `<p class="warning">${esc(snapshot.error)}</p>` : Object.entries(snapshot.values || {}).map(([name,value]) => `<label>${esc(name)}<input data-service="${esc(service)}" data-config="${esc(name)}" value="${esc(value)}"></label>`).join('')}</fieldset>`).join('');
    } else document.querySelector('#runtimeConfig').innerHTML = '<p>Для применения настроек настройте область доступа к сервисам.</p>';
  } catch (error) { toast('Диагностика: '+error.message, true); }
}

async function loadLogs() {
  const root = document.querySelector('#serviceLogs'); if (!root) return;
  try {
    const service = encodeURIComponent(document.querySelector('#logService').value.trim());
    const level = encodeURIComponent(document.querySelector('#logLevel').value);
    const rows = await api(`/engineering/logs?service=${service}&level=${level}&take=150`);
    root.innerHTML = rows.length ? rows.map(row => `<div class="log-row"><span>${date(row.timestamp)}</span><span class="${cssState(row.level==='Error'||row.level==='Critical'?'Failed':'Ready')}">${esc(row.level)}</span><span>${esc(row.service)}</span><span>${esc(row.message)}</span></div>`).join('') : '<div class="empty">Записей по фильтру нет</div>';
  } catch (error) { root.innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}

function updateFreshness() {
  const root = document.querySelector('#freshness');
  if (state.readFailure) { root.textContent = 'Нет актуальных данных: связь или сервис недоступны'; root.className = 'freshness stale'; return; }
  if (!state.lastSuccess) { root.textContent = 'Ожидание данных'; root.className = 'freshness stale'; return; }
  const seconds = Math.max(0, Math.round((Date.now() - state.lastSuccess) / 1000));
  root.textContent = seconds < 2 ? 'Данные обновлены сейчас' : `Обновлено ${seconds} сек назад`;
  root.className = `freshness${seconds > 30 ? ' stale' : ''}`;
}

async function refresh() {
  if (state.refreshing) return;
  state.refreshing = true;
  const button = document.querySelector('#refresh');
  const banner = document.querySelector('#error');
  button.disabled = true;
  document.querySelector('#scopeSelector').disabled = true;
  banner.hidden = true;
  try {
    const requests = [api('/status'), api('/measurements')];
    if (mode === 'engineer') requests.push(api('/audit'));
    const [status, measurements, audit = []] = await Promise.all(requests);
    state.status = status;
    state.measurements = Array.isArray(measurements) ? measurements : measurements.items || [];
    state.audit = Array.isArray(audit) ? audit : [];
    state.lastSuccess = Date.now();
    state.readFailure = false;
    renderStatus(status);
    populateStatuses();
    renderMeasurements();
    if (mode === 'engineer') renderAudit();
    renderKpis();
  } catch (error) {
    banner.hidden = false;
    state.readFailure = true;
    banner.textContent = `Не удалось получить данные: ${error.message}`;
    document.querySelector('#start')?.setAttribute('disabled', '');
    document.querySelector('#freshness').textContent = 'Связь потеряна: показаны последние полученные данные';
  } finally {
    state.refreshing = false;
    button.disabled = false;
    document.querySelector('#scopeSelector').disabled = !!state.applyingSettings;
    updateFreshness();
  }
}

document.querySelector('#refresh').addEventListener('click', refresh);
document.querySelector('#logout')?.addEventListener('click', async () => {
  await api('/auth/logout', { method:'POST' });
  location.assign('/login/');
});
document.querySelector('#userForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  try {
    await api('/admin/users', { method:'POST', body:JSON.stringify(Object.fromEntries(new FormData(event.target))) });
    event.target.reset(); toast('Пользователь создан'); await loadUsers();
  } catch (error) { toast(error.message, true); }
});
document.querySelector('#users')?.addEventListener('click', async event => {
  const toggle=event.target.closest('[data-user-toggle]'); const reset=event.target.closest('[data-user-password]');
  try {
    if(toggle) await api(`/admin/users/${encodeURIComponent(toggle.dataset.userToggle)}/enabled/${toggle.dataset.enabled}`,{method:'PUT'});
    if(reset){const password=prompt(`Новый пароль для ${reset.dataset.userPassword} (не менее 12 символов)`);if(!password)return;await api(`/admin/users/${encodeURIComponent(reset.dataset.userPassword)}/password`,{method:'PUT',body:JSON.stringify({password})});}
    await loadUsers();
  } catch(error){toast(error.message,true);}
});
document.querySelector('#measurementSearch').addEventListener('input', renderMeasurements);
document.querySelector('#statusFilter').addEventListener('change', renderMeasurements);
document.querySelector('#auditFilter')?.addEventListener('change', renderAudit);
document.querySelector('#start')?.addEventListener('click', () => document.querySelector('#startDialog').showModal());
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => document.querySelector(`#${button.dataset.close}`).close()));

document.querySelector('#startForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = event.submitter;
  submit.disabled = true;
  try {
    const data = new FormData(event.target);
    event.target.dataset.commandId ||= crypto.randomUUID();
    event.target.dataset.measurementId ||= crypto.randomUUID();
    await api('/measurements', { method:'POST', body:JSON.stringify({ commandId:event.target.dataset.commandId, measurementId:event.target.dataset.measurementId, excavatorId:data.get('excavatorId'), coordinateSystemId:data.get('coordinateSystemId'), reason:data.get('reason') }) });
    document.querySelector('#startDialog').close();
    event.target.reset();
    delete event.target.dataset.commandId;
    delete event.target.dataset.measurementId;
    updateScopeContext();
    toast('Измерение запущено');
    await refresh();
  } catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
});

document.addEventListener('click', async event => {
  const target = event.target.closest('[data-detail],[data-retry],[data-cancel]');
  if (!target) return;
  if (target.dataset.detail) {
    try {
      const requestedScope = state.scopeId;
      const value = await api(`/measurements/${target.dataset.detail}`);
      if (requestedScope !== state.scopeId) return;
      const measurement = value.measurement || value;
      if (measurement.stages && !measurement.pipeline) measurement.pipeline = { stages:measurement.stages };
      renderResultVisual(measurement); renderPipeline(measurement);
      document.querySelector('#detailJson').textContent = JSON.stringify(value, null, 2);
      document.querySelector('#detailDialog').showModal();
    } catch (error) { toast(error.message, true); }
    return;
  }
  const form = document.querySelector('#commandForm');
  const command = target.dataset.retry ? 'retry' : 'cancel';
  const id = target.dataset.retry || target.dataset.cancel;
  form.elements.id.value = id;
  form.elements.command.value = command;
  form.elements.version.value = target.dataset.version;
  form.elements.reason.value = '';
  document.querySelector('#commandTitle').textContent = command === 'retry' ? 'Повторить измерение' : 'Отменить измерение';
  document.querySelector('#commandTarget').textContent = `Измерение ${id}`;
  document.querySelector('#commandDialog').showModal();
});

document.querySelector('#commandForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = event.submitter;
  const data = new FormData(event.target);
  submit.disabled = true;
  try {
    await api(`/measurements/${data.get('id')}/${data.get('command')}`, { method:'POST', body:JSON.stringify({ commandId:crypto.randomUUID(), expectedVersion:Number(data.get('version')), reason:data.get('reason'), confirmed:true }) });
    document.querySelector('#commandDialog').close();
    toast('Команда выполнена и записана в аудит');
    await refresh();
  } catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
});

document.querySelector('#exportDiagnostics')?.addEventListener('click', () => {
  if (!state.status) { toast('Сначала загрузите данные', true); return; }
  const snapshot = { generatedAt:new Date().toISOString(), system:state.status, measurements:state.measurements, audit:state.audit };
  const blob = new Blob([JSON.stringify(snapshot, null, 2)], { type:'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `smartmetrix-diagnostics-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
  toast('Диагностический снимок подготовлен');
});

document.querySelector('#exportCsv')?.addEventListener('click', () => {
  const columns = ['id','excavatorId','status','updatedAt','d10','d20','d50','d80','d90','d95','isTestData','confidence','blockCount','oversizeFraction','coverage'];
  const csv = '\ufeff' + [columns.join(';'), ...state.measurements.map(item => columns.map(name => String(item[name] ?? '').replaceAll(';',',')).join(';'))].join('\r\n');
  download(`smartmetrix-report-${new Date().toISOString().slice(0,10)}.csv`, csv, 'text/csv;charset=utf-8');
});
document.querySelector('#printReport')?.addEventListener('click', () => window.print());
document.querySelector('#refreshSystem')?.addEventListener('click', loadEngineeringTools);
document.querySelector('#loadLogs')?.addEventListener('click', loadLogs);
document.querySelector('#saveConfig')?.addEventListener('click', async () => {
  if (state.applyingSettings) return;
  state.applyingSettings = true;
  document.querySelector('#scopeSelector').disabled = true;
  document.querySelector('#saveConfig').disabled = true;
  try {
    if (!state.scopeId) throw new Error('Выберите настроенную область доступа');
    const groups = {};
    document.querySelectorAll('[data-config]').forEach(input => { (groups[input.dataset.service] ||= {})[input.dataset.config] = input.value; });
    if (Object.keys(groups).length === 0) throw new Error('Нет доступных настроек сервисов');
    for (const [service,values] of Object.entries(groups)) {
      await api(`/engineering/config/${encodeURIComponent(service)}`,{method:'PUT',body:JSON.stringify({ values, expectedRevision:state.config[service].revision })});
    }
    toast('Настройки применены сервисами'); await loadEngineeringTools();
  } catch(error) { toast('Применение остановлено: '+error.message,true); await loadEngineeringTools(); }
  finally {
    state.applyingSettings = false;
    document.querySelector('#scopeSelector').disabled = state.refreshing;
    document.querySelector('#saveConfig').disabled = false;
  }
});

async function boot() {
  await loadIdentity();
  await refresh();
  if (mode === 'engineer') { await loadEngineeringTools(); await loadLogs(); }
}
boot().catch(error => toast(error.message, true));
setInterval(refresh, 10000);
setInterval(updateFreshness, 1000);
